using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using OneShader.Diagnostics;
using OneShader.Lexing;

namespace OneShader.Preprocessing;

/// <summary>One preprocessing run: file stack, directives and the macro table. Macro expansion is in Session.Expand.cs.</summary>
internal sealed partial class Session
{
    private readonly PreprocessorOptions _opts;
    private readonly DiagnosticBag _diag;
    private readonly CancellationToken _ct;
    private readonly StringInterner _interner = new();
    private readonly Dictionary<string, Macro> _macros = new(StringComparer.Ordinal);
    private readonly List<FileState> _files = [];
    private readonly HashSet<string> _once = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CachedFile> _cache = new(StringComparer.Ordinal);
    private readonly List<string> _fileList = [];
    private readonly List<Token> _out = [];
    private readonly IIncludeResolver _resolver;
    private bool _aborted;
    private bool _quiet;
    private int _includesOpened;
    private int _counter;

    public Session(PreprocessorOptions opts, DiagnosticBag diag, CancellationToken ct)
    {
        _opts = opts;
        _diag = diag;
        _ct = ct;
        _resolver = opts.IncludeResolver ?? new FileSystemIncludeResolver();
    }

    private sealed class CachedFile
    {
        public CachedFile(Token[] tokens) => Tokens = tokens;

        public Token[] Tokens { get; }

        /// <summary>Set when the file is wrapped in <c>#ifndef X ... #endif</c> so a repeat include with X defined can be skipped unread.</summary>
        public string? GuardMacro { get; set; }
    }

    private struct Cond
    {
        public Token Directive;
        public bool ParentActive;
        public bool Active;
        public bool AnyTaken;
        public bool SawElse;
    }

    private sealed class FileState
    {
        public FileState(SourceFile file, CachedFile cached)
        {
            File = file;
            Cached = cached;
            Tokens = cached.Tokens;
        }

        public SourceFile File { get; }

        public CachedFile Cached { get; }

        public Token[] Tokens { get; }

        public int Pos { get; set; }

        public List<Cond> Conds { get; } = [];

        public int LineDelta { get; set; }

        public string? PresumedName { get; set; }

        public string? GuardCandidate { get; set; }
    }

    public PreprocessResult Run(SourceFile main)
    {
        _quiet = true;
        DefinePredefined();
        _quiet = false;

        PushFile(main);
        var ctx = new Ctx(isMain: true);
        while (!_aborted)
        {
            Pump(ctx, _out);
            if (_aborted || !PopFile())
            {
                break;
            }
        }

        (int line, int col) = main.GetLineColumn(main.Text.Length);
        _out.Add(new Token(TokenKind.EndOfFile, string.Empty, new SourceSpan(main.Path, line, col, main.Text.Length, 0)));
        return new PreprocessResult(_out, _fileList);
    }

    private void Abort(string code, string message, SourceSpan span)
    {
        if (!_aborted)
        {
            _diag.Error(code, message, span);
        }

        _aborted = true;
    }

    // ---- files -------------------------------------------------------------------------------

    private CachedFile GetCached(SourceFile file)
    {
        if (_cache.TryGetValue(file.Identity, out CachedFile? c))
        {
            return c;
        }

        var list = new List<Token>();
        Lexer.LexInto(file, _interner, list);
        c = new CachedFile(list.ToArray());
        _cache[file.Identity] = c;
        return c;
    }

    private void PushFile(SourceFile file)
    {
        if (!_fileList.Contains(file.Path))
        {
            _fileList.Add(file.Path);
        }

        _files.Add(new FileState(file, GetCached(file)));
    }

    private bool PopFile()
    {
        FileState f = _files[^1];
        for (int i = f.Conds.Count - 1; i >= 0; i--)
        {
            Token d = f.Conds[i].Directive;
            _diag.Error(PreprocessorCodes.UnterminatedConditional, $"'#{DirectiveName(d)}' is never closed; add a matching '#endif' before the end of {f.File.Path}", d.Span);
        }

        _files.RemoveAt(_files.Count - 1);
        return _files.Count > 0;
    }

    private static string DirectiveName(Token hash)
    {
        // The token after '#' names the directive; it is stored in Text of the saved directive token.
        return hash.Text.Length > 1 ? hash.Text[1..] : "if";
    }

    private static bool Skipping(FileState f) => f.Conds.Count > 0 && !f.Conds[^1].Active;

    /// <summary>Next unprocessed token of the current file; false at the end of that file.</summary>
    private bool ReadFromFile(out Token token)
    {
        while (!_aborted && _files.Count > 0)
        {
            FileState f = _files[^1];
            if (f.Pos >= f.Tokens.Length)
            {
                break;
            }

            Token t = f.Tokens[f.Pos++];
            if (t.Kind == TokenKind.Hash && t.AtLineStart)
            {
                if (HandleDirective(f, t, out Token emitted))
                {
                    token = emitted;
                    return true;
                }

                continue;
            }

            if (Skipping(f))
            {
                continue;
            }

            if (Lexer.IsErrorKind(t.Kind))
            {
                PreprocessorCodes.ReportIfErrorToken(t, _diag);
                continue;
            }

            token = t;
            return true;
        }

        token = default;
        return false;
    }

    // ---- directives --------------------------------------------------------------------------

    private static List<Token> GatherLine(FileState f)
    {
        var line = new List<Token>();
        while (f.Pos < f.Tokens.Length && !f.Tokens[f.Pos].AtLineStart)
        {
            line.Add(f.Tokens[f.Pos++]);
        }

        return line;
    }

    private static string Join(List<Token> tokens, int from)
    {
        var sb = new StringBuilder();
        for (int i = from; i < tokens.Count; i++)
        {
            if (i > from && tokens[i].HasLeadingSpace)
            {
                sb.Append(' ');
            }

            sb.Append(tokens[i].Text);
        }

        return sb.ToString();
    }

    /// <summary>Handles one directive line. Returns true with a token when the directive produces output (a pragma).</summary>
    private bool HandleDirective(FileState f, Token hash, out Token emitted)
    {
        emitted = default;
        int hashIndex = f.Pos - 1;
        List<Token> line = GatherLine(f);
        if (line.Count == 0)
        {
            return false; // the null directive
        }

        Token nameTok = line[0];
        bool active = !Skipping(f);
        string name = nameTok.Kind == TokenKind.Identifier ? nameTok.Text : string.Empty;
        if (nameTok.Kind == TokenKind.IntLiteral)
        {
            name = "line-marker";
        }

        switch (name)
        {
            case "if":
            case "ifdef":
            case "ifndef":
                DoIf(f, hash, hashIndex, line, name, active);
                return false;
            case "elif":
                DoElif(f, hash, line);
                return false;
            case "else":
                DoElse(f, hash, line);
                return false;
            case "endif":
                DoEndif(f, hash, line);
                return false;
        }

        if (!active)
        {
            return false;
        }

        switch (name)
        {
            case "define":
                DefineMacro(line, 1, hash.Span);
                break;
            case "undef":
                if (line.Count < 2 || line[1].Kind != TokenKind.Identifier)
                {
                    _diag.Error(PreprocessorCodes.BadMacroName, "'#undef' needs a macro name, as in '#undef NAME'", (line.Count > 1 ? line[1] : nameTok).Span);
                }
                else
                {
                    _macros.Remove(line[1].Text);
                    WarnExtra(line, 2, "#undef");
                }

                break;
            case "include":
                DoInclude(f, hash, line);
                break;
            case "line":
            case "line-marker":
                DoLine(f, hash, line, name == "line" ? 1 : 0);
                break;
            case "error":
                _diag.Error(PreprocessorCodes.ErrorDirective, $"#error {Join(line, 1)}".TrimEnd(), hash.Span);
                break;
            case "warning":
                _diag.Warning(PreprocessorCodes.WarningDirective, $"#warning {Join(line, 1)}".TrimEnd(), hash.Span);
                break;
            case "pragma":
                if (line.Count > 1 && line[1].IsIdentifier("once"))
                {
                    _once.Add(f.File.Identity);
                }
                else if (line.Count > 1)
                {
                    emitted = new Token(TokenKind.Pragma, Join(line, 1), hash.Span, TokenTraits.AtLineStart);
                    return true;
                }

                break;
            default:
                _diag.Error(PreprocessorCodes.UnknownDirective,
                    nameTok.Kind == TokenKind.Identifier
                        ? $"unknown preprocessor directive '#{nameTok.Text}'; the directives are define, undef, include, if, ifdef, ifndef, elif, else, endif, line, error, warning and pragma"
                        : $"expected a directive name after '#', found '{nameTok.Text}'",
                    nameTok.Span);
                break;
        }

        return false;
    }

    private void WarnExtra(List<Token> line, int from, string directive)
    {
        if (line.Count > from && !_quiet)
        {
            _diag.Warning(PreprocessorCodes.ExtraTokens, $"extra tokens after '{directive}' are ignored; remove them or put them in a comment", line[from].Span);
        }
    }

    private void DoIf(FileState f, Token hash, int hashIndex, List<Token> line, string kind, bool active)
    {
        // The directive name is remembered in the token text so "never closed" can name it.
        var dir = new Token(TokenKind.Hash, "#" + kind, hash.Span, hash.Flags);
        bool value = false;
        if (active)
        {
            if (kind == "if")
            {
                value = EvalCondition(line, 1, dir);
            }
            else if (line.Count < 2 || line[1].Kind != TokenKind.Identifier)
            {
                _diag.Error(PreprocessorCodes.BadMacroName, $"'#{kind}' needs a macro name, as in '#{kind} NAME'", (line.Count > 1 ? line[1] : line[0]).Span);
            }
            else
            {
                bool defined = _macros.ContainsKey(line[1].Text);
                value = kind == "ifdef" ? defined : !defined;
                WarnExtra(line, 2, "#" + kind);
                if (kind == "ifndef" && hashIndex == 0 && f.Conds.Count == 0)
                {
                    f.GuardCandidate = line[1].Text;
                }
            }
        }

        f.Conds.Add(new Cond { Directive = dir, ParentActive = active, Active = active && value, AnyTaken = !active || value });
    }

    private void DoElif(FileState f, Token hash, List<Token> line)
    {
        if (f.Conds.Count == 0)
        {
            _diag.Error(PreprocessorCodes.StrayConditional, "'#elif' without a matching '#if'; remove it or add the '#if'", hash.Span);
            return;
        }

        if (f.Conds.Count == 1)
        {
            f.GuardCandidate = null;
        }

        Cond c = f.Conds[^1];
        if (c.SawElse)
        {
            _diag.Error(PreprocessorCodes.ElseAfterElse, "'#elif' after '#else'; put '#elif' branches before the '#else'", hash.Span);
            c.Active = false;
        }
        else if (!c.ParentActive || c.AnyTaken)
        {
            c.Active = false;
        }
        else
        {
            bool v = EvalCondition(line, 1, hash);
            c.Active = v;
            c.AnyTaken = v;
        }

        f.Conds[^1] = c;
    }

    private void DoElse(FileState f, Token hash, List<Token> line)
    {
        if (f.Conds.Count == 0)
        {
            _diag.Error(PreprocessorCodes.StrayConditional, "'#else' without a matching '#if'; remove it or add the '#if'", hash.Span);
            return;
        }

        if (f.Conds.Count == 1)
        {
            f.GuardCandidate = null;
        }

        Cond c = f.Conds[^1];
        if (c.SawElse)
        {
            _diag.Error(PreprocessorCodes.ElseAfterElse, "a second '#else' for the same '#if'; remove it", hash.Span);
            c.Active = false;
        }
        else
        {
            c.SawElse = true;
            c.Active = c.ParentActive && !c.AnyTaken;
            c.AnyTaken = true;
            if (c.ParentActive)
            {
                WarnExtra(line, 1, "#else");
            }
        }

        f.Conds[^1] = c;
    }

    private void DoEndif(FileState f, Token hash, List<Token> line)
    {
        if (f.Conds.Count == 0)
        {
            _diag.Error(PreprocessorCodes.StrayConditional, "'#endif' without a matching '#if'; remove it or add the '#if'", hash.Span);
            return;
        }

        if (f.Conds[^1].ParentActive)
        {
            WarnExtra(line, 1, "#endif");
        }

        f.Conds.RemoveAt(f.Conds.Count - 1);
        if (f.Conds.Count == 0)
        {
            if (f.GuardCandidate is not null && f.Pos >= f.Tokens.Length)
            {
                f.Cached.GuardMacro = f.GuardCandidate;
            }

            f.GuardCandidate = null;
        }
    }

    private bool EvalCondition(List<Token> line, int from, Token directive)
    {
        var expr = line.GetRange(from, line.Count - from);
        List<Token>? resolved = ResolveDefined(expr, directive);
        if (resolved is null)
        {
            return false;
        }

        List<Token> expanded = ExpandList(resolved);
        if (_aborted)
        {
            return false;
        }

        return ConditionEvaluator.Evaluate(expanded, directive, _diag);
    }

    /// <summary>Replaces <c>defined X</c>, <c>defined(X)</c> and <c>__has_include(...)</c> by 1 or 0 before macros expand, as the standard requires.</summary>
    private List<Token>? ResolveDefined(List<Token> tokens, Token directive)
    {
        var result = new List<Token>(tokens.Count);
        for (int i = 0; i < tokens.Count; i++)
        {
            Token t = tokens[i];
            if (t.IsIdentifier("defined"))
            {
                bool paren = i + 1 < tokens.Count && tokens[i + 1].Kind == TokenKind.LParen;
                int nameAt = paren ? i + 2 : i + 1;
                if (nameAt >= tokens.Count || tokens[nameAt].Kind != TokenKind.Identifier || (paren && (nameAt + 1 >= tokens.Count || tokens[nameAt + 1].Kind != TokenKind.RParen)))
                {
                    _diag.Error(PreprocessorCodes.BadDefined, "'defined' must be followed by a macro name, as in 'defined(NAME)' or 'defined NAME'", t.Span);
                    return null;
                }

                result.Add(Number(_macros.ContainsKey(tokens[nameAt].Text) ? 1 : 0, t));
                i = paren ? nameAt + 1 : nameAt;
            }
            else if (t.IsIdentifier("__has_include") && _macros.ContainsKey("__has_include"))
            {
                if (!(i + 3 < tokens.Count && tokens[i + 1].Kind == TokenKind.LParen))
                {
                    _diag.Error(PreprocessorCodes.BadDefined, "'__has_include' must be followed by a file name in parentheses, as in '__has_include(\"file.h\")'", t.Span);
                    return null;
                }

                int j = i + 2;
                string? incName = null;
                bool angled = false;
                if (tokens[j].Kind == TokenKind.StringLiteral)
                {
                    incName = tokens[j].Text[1..^1];
                    j++;
                }
                else if (tokens[j].Kind == TokenKind.Less)
                {
                    var sb = new StringBuilder();
                    j++;
                    while (j < tokens.Count && tokens[j].Kind != TokenKind.Greater)
                    {
                        sb.Append(tokens[j].Text);
                        j++;
                    }

                    j++;
                    incName = sb.ToString();
                    angled = true;
                }

                if (incName is null || j >= tokens.Count || tokens[j].Kind != TokenKind.RParen)
                {
                    _diag.Error(PreprocessorCodes.BadDefined, "'__has_include' expects \"file\" or <file> followed by ')'", t.Span);
                    return null;
                }

                var searched = new List<string>();
                bool found = TryResolve(incName, angled, _files[^1].File, searched, directive) is not null;
                result.Add(Number(found ? 1 : 0, t));
                i = j;
            }
            else
            {
                result.Add(t);
            }
        }

        return result;
    }

    private static Token Number(int value, Token at) =>
        new(TokenKind.IntLiteral, Preprocessor.Int(value), at.Span, at.Flags | TokenTraits.FromMacro);

    private SourceFile? TryResolve(string name, bool angled, SourceFile includer, List<string> searched, Token at)
    {
        try
        {
            return _resolver.Resolve(name, angled, includer, searched);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A resolver is user code; its failure is an error on the include line, not a crash of the build.
            _diag.Error(PreprocessorCodes.IncludeFailed, $"the include resolver failed while looking for '{name}': {ex.Message}", at.Span);
            return null;
        }
    }

    // ---- #include ----------------------------------------------------------------------------

    private void DoInclude(FileState f, Token hash, List<Token> line)
    {
        string? name = null;
        bool angled = false;
        Token at = line.Count > 1 ? line[1] : line[0];
        if (line.Count > 1)
        {
            List<Token> operand = line;
            int from = 1;
            if (line[1].Kind != TokenKind.HeaderName && line[1].Kind != TokenKind.StringLiteral)
            {
                operand = ExpandList(line.GetRange(1, line.Count - 1));
                from = 0;
            }

            if (operand.Count > from)
            {
                Token first = operand[from];
                if (first.Kind == TokenKind.HeaderName)
                {
                    name = first.Text[1..^1];
                    angled = true;
                }
                else if (first.Kind == TokenKind.StringLiteral)
                {
                    name = first.Text[1..^1];
                }
                else if (first.Kind == TokenKind.Less)
                {
                    var sb = new StringBuilder();
                    int j = from + 1;
                    while (j < operand.Count && operand[j].Kind != TokenKind.Greater)
                    {
                        if (j > from + 1 && operand[j].HasLeadingSpace)
                        {
                            sb.Append(' ');
                        }

                        sb.Append(operand[j].Text);
                        j++;
                    }

                    if (j < operand.Count)
                    {
                        name = sb.ToString();
                        angled = true;
                    }
                }
            }
        }

        if (name is null || name.Length == 0)
        {
            _diag.Error(PreprocessorCodes.BadInclude, "'#include' needs a file name in quotes or angle brackets, as in '#include \"common.hlsli\"'", at.Span);
            return;
        }

        if (_files.Count >= _opts.MaxIncludeDepth)
        {
            _diag.Error(PreprocessorCodes.IncludeTooDeep,
                $"'#include' nesting is deeper than {Preprocessor.Int(_opts.MaxIncludeDepth)} levels; include chain: {DescribeChain(name)}. Add '#pragma once' or an include guard to the file that includes itself",
                at.Span);
            _aborted = true;
            return;
        }

        if (++_includesOpened > _opts.MaxIncludeCount)
        {
            Abort(PreprocessorCodes.LimitExceeded, $"more than {Preprocessor.Int(_opts.MaxIncludeCount)} '#include' directives were processed; the includes are probably growing exponentially, add include guards", at.Span);
            return;
        }

        var searched = new List<string>();
        SourceFile? file = TryResolve(name, angled, f.File, searched, at);
        if (file is null)
        {
            if (!_diag.HasErrorAt(at.Span))
            {
                _diag.Error(PreprocessorCodes.IncludeNotFound,
                    $"cannot find include file '{name}'; searched: {string.Join(", ", searched.ConvertAll(d => "'" + d + "'"))}. Check the spelling, or add the directory that contains it to the include paths",
                    at.Span);
            }

            return;
        }

        if (_once.Contains(file.Identity))
        {
            return;
        }

        if (_cache.TryGetValue(file.Identity, out CachedFile? cached) && cached.GuardMacro is not null && _macros.ContainsKey(cached.GuardMacro))
        {
            return;
        }

        PushFile(file);
    }

    private string DescribeChain(string next)
    {
        var paths = new List<string>(_files.Count + 1);
        foreach (FileState s in _files)
        {
            paths.Add(s.File.Path);
        }

        // The file about to be included was already open further up: show the path to it and one lap of the cycle.
        int first = paths.IndexOf(next);
        if (first < 0)
        {
            return "... -> " + string.Join(" -> ", paths.GetRange(paths.Count - 3, 3)) + " -> " + next;
        }

        int second = paths.IndexOf(next, first + 1);
        int end = second < 0 ? paths.Count : second;
        var shown = new List<string>();
        if (first > 2)
        {
            shown.Add("...");
        }

        for (int i = Math.Max(0, first - 2); i <= end - 1; i++)
        {
            shown.Add(paths[i]);
        }

        shown.Add(next);
        return string.Join(" -> ", shown) + " (and so on until the nesting limit)";
    }

    // ---- #line -------------------------------------------------------------------------------

    private void DoLine(FileState f, Token hash, List<Token> line, int from)
    {
        List<Token> operand = ExpandList(line.GetRange(from, line.Count - from));
        if (operand.Count == 0 || operand[0].Kind != TokenKind.IntLiteral || !ConditionEvaluator.TryParseInt(operand[0].Text, out ulong n, out _) || n > int.MaxValue)
        {
            _diag.Error(PreprocessorCodes.BadLine, "'#line' needs a line number, as in '#line 100' or '#line 100 \"file.hlsl\"'", (operand.Count > 0 ? operand[0] : line[0]).Span);
            return;
        }

        string? presumed = f.PresumedName;
        if (operand.Count > 1)
        {
            if (operand[1].Kind != TokenKind.StringLiteral)
            {
                _diag.Error(PreprocessorCodes.BadLine, "the file name in '#line' must be a string in quotes", operand[1].Span);
                return;
            }

            presumed = operand[1].Text[1..^1].Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        int directiveLine = line[^1].Span.Line;
        f.LineDelta = (int)n - (directiveLine + 1);
        f.PresumedName = presumed;
    }

    // ---- predefined macros -------------------------------------------------------------------

    private void DefinePredefined()
    {
        void Builtin(string name, BuiltinKind kind) =>
            _macros[name] = new Macro(name, SourceSpan.None) { Builtin = kind };

        Builtin("__FILE__", BuiltinKind.File);
        Builtin("__LINE__", BuiltinKind.Line);
        Builtin("__COUNTER__", BuiltinKind.Counter);
        Builtin("__INCLUDE_LEVEL__", BuiltinKind.IncludeLevel);
        Builtin("__has_include", BuiltinKind.HasInclude);

        DefineText("__HLSL_VERSION", Preprocessor.Int(_opts.HlslVersion));
        // Stage numbers are the D3D program-type values DXC uses; shaders compare __SHADER_TARGET_STAGE with these.
        (string, int)[] stages =
        [
            ("VERTEX", 1), ("PIXEL", 0), ("GEOMETRY", 2), ("HULL", 3), ("DOMAIN", 4), ("COMPUTE", 5), ("LIBRARY", 6), ("MESH", 13), ("AMPLIFICATION", 14),
        ];
        foreach ((string n, int v) in stages)
        {
            DefineText("__SHADER_STAGE_" + n, Preprocessor.Int(v));
        }

        if (_opts.TargetProfile is { } profile)
        {
            string[] parts = profile.Split('_');
            if (parts.Length == 3 && Preprocessor.StageNumber(parts[0]) >= 0
                && int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int major)
                && int.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int minor))
            {
                DefineText("__SHADER_TARGET_STAGE", Preprocessor.Int(Preprocessor.StageNumber(parts[0])));
                DefineText("__SHADER_TARGET_MAJOR", Preprocessor.Int(major));
                DefineText("__SHADER_TARGET_MINOR", Preprocessor.Int(minor));
            }
        }

        if (_opts.SpirvMinorVersion is { } spv)
        {
            DefineText("__spirv__", "1");
            DefineText("__SPIRV_MAJOR_VERSION__", "1");
            DefineText("__SPIRV_MINOR_VERSION__", Preprocessor.Int(spv));
        }

        foreach (MacroDefinition d in _opts.Defines)
        {
            DefineText(d.Name, d.Value ?? "1");
        }
    }

    private void DefineText(string name, string value)
    {
        var file = new SourceFile("<command line>", name + " " + value);
        var tokens = new List<Token>();
        Lexer.LexInto(file, _interner, tokens);
        tokens.Insert(0, new Token(TokenKind.Identifier, "define", SourceSpan.None));
        DefineMacro(tokens, 1, SourceSpan.None);
    }
}

internal static class DiagnosticBagExtensions
{
    /// <summary>True when an error is already reported at exactly this span (the resolver reported its own failure).</summary>
    public static bool HasErrorAt(this DiagnosticBag bag, SourceSpan span)
    {
        for (int i = bag.Items.Count - 1; i >= 0 && i >= bag.Items.Count - 2; i--)
        {
            Diagnostic d = bag.Items[i];
            if (d.Severity == DiagnosticSeverity.Error && d.Span == span)
            {
                return true;
            }
        }

        return false;
    }
}
