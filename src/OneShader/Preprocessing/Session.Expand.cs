using System;
using System.Collections.Generic;
using System.Text;
using OneShader.Diagnostics;
using OneShader.Lexing;

namespace OneShader.Preprocessing;

// Macro definition and expansion. Expansion follows the C standard's algorithm with hide sets: every
// token remembers which macros it came out of and is never expanded by them again, which is what
// makes self-referential macros terminate. Rescanning uses an explicit stack of token lists instead of
// recursion; only the expansion of macro arguments recurses, and that depth is limited.
internal sealed partial class Session
{
    private sealed class Frame
    {
        public Frame(List<Token> items) => Items = items;

        public List<Token> Items { get; }

        public int Pos { get; set; }
    }

    private sealed class Ctx
    {
        public Ctx(bool isMain) => IsMain = isMain;

        public bool IsMain { get; }

        public List<Frame> Frames { get; } = [];

        public void Push(List<Token> tokens)
        {
            if (tokens.Count > 0)
            {
                Frames.Add(new Frame(tokens));
            }
        }
    }

    private long _steps;
    private int _nesting;

    private bool Next(Ctx ctx, out Token token)
    {
        while (ctx.Frames.Count > 0)
        {
            Frame fr = ctx.Frames[^1];
            token = fr.Items[fr.Pos++];
            if (fr.Pos >= fr.Items.Count)
            {
                ctx.Frames.RemoveAt(ctx.Frames.Count - 1);
            }

            if ((++_steps & 0xFFF) == 0)
            {
                _ct.ThrowIfCancellationRequested();
                if (_steps > _opts.MaxExpansionSteps)
                {
                    Abort(PreprocessorCodes.LimitExceeded,
                        $"macro expansion did more than {_opts.MaxExpansionSteps.ToString(System.Globalization.CultureInfo.InvariantCulture)} steps; a macro probably expands exponentially, simplify it",
                        token.Span);
                    token = default;
                    return false;
                }
            }

            return true;
        }

        if (ctx.IsMain)
        {
            return ReadFromFile(out token);
        }

        token = default;
        return false;
    }

    private void Pump(Ctx ctx, List<Token> sink)
    {
        while (!_aborted && Next(ctx, out Token t))
        {
            if (t.Kind == TokenKind.Identifier && TryExpand(ctx, t))
            {
                continue;
            }

            sink.Add(t);
            if (sink.Count > _opts.MaxOutputTokens)
            {
                Abort(PreprocessorCodes.LimitExceeded, $"the preprocessed output exceeds {Preprocessor.Int(_opts.MaxOutputTokens)} tokens; check for runaway macros or includes", t.Span);
            }
        }
    }

    private List<Token> ExpandList(List<Token> tokens)
    {
        if (tokens.Count == 0 || _aborted)
        {
            return tokens;
        }

        if (_nesting >= _opts.MaxMacroNesting)
        {
            Abort(PreprocessorCodes.ExpansionTooDeep,
                $"macro calls are nested more than {Preprocessor.Int(_opts.MaxMacroNesting)} levels deep inside macro arguments; split the expression into smaller macros",
                tokens[0].Span);
            return tokens;
        }

        _nesting++;
        var ctx = new Ctx(isMain: false);
        ctx.Push(tokens);
        var sink = new List<Token>(tokens.Count);
        Pump(ctx, sink);
        _nesting--;
        return sink;
    }

    private bool TryExpand(Ctx ctx, in Token t)
    {
        if (!_macros.TryGetValue(t.Text, out Macro? m) || m.Builtin == BuiltinKind.HasInclude)
        {
            return false;
        }

        if (t.Hide is not null && t.Hide.Contains(m))
        {
            return false;
        }

        if (m.Builtin != BuiltinKind.None)
        {
            ctx.Push([ExpandBuiltin(m, t)]);
            return true;
        }

        HideSet? hide;
        List<List<Token>>? args = null;
        bool vaOmitted = false;
        if (m.IsFunction)
        {
            if (!Next(ctx, out Token p))
            {
                return false;
            }

            if (p.Kind != TokenKind.LParen)
            {
                ctx.Frames.Add(new Frame([p]));
                return false;
            }

            if (!GatherArgs(ctx, m, t, out args, out Token rparen, out vaOmitted))
            {
                return true;
            }

            hide = HideSet.Add(HideSet.Intersect(t.Hide, rparen.Hide), m);
        }
        else
        {
            hide = HideSet.Add(t.Hide, m);
        }

        ctx.Push(Substitute(m, args, vaOmitted, t, hide));
        return true;
    }

    private Token ExpandBuiltin(Macro m, in Token t)
    {
        TokenTraits flags = t.Flags | TokenTraits.FromMacro;
        FileState? f = _files.Count > 0 ? _files[^1] : null;
        switch (m.Builtin)
        {
            case BuiltinKind.File:
            {
                string path = f?.PresumedName ?? f?.File.Path ?? string.Empty;
                string quoted = "\"" + path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
                return new Token(TokenKind.StringLiteral, quoted, t.Span, flags);
            }

            case BuiltinKind.Line:
                return new Token(TokenKind.IntLiteral, Preprocessor.Int(t.Span.Line + (f?.LineDelta ?? 0)), t.Span, flags);
            case BuiltinKind.Counter:
                return new Token(TokenKind.IntLiteral, Preprocessor.Int(_counter++), t.Span, flags);
            default:
                return new Token(TokenKind.IntLiteral, Preprocessor.Int(Math.Max(0, _files.Count - 1)), t.Span, flags);
        }
    }

    private bool GatherArgs(Ctx ctx, Macro m, in Token name, out List<List<Token>> args, out Token rparen, out bool vaOmitted)
    {
        args = [];
        vaOmitted = false;
        rparen = default;
        var cur = new List<Token>();
        int depth = 0;
        int nparams = m.Params.Length;
        while (true)
        {
            if (!Next(ctx, out Token tok))
            {
                if (!_aborted)
                {
                    _diag.Error(PreprocessorCodes.UnterminatedMacroCall, $"the argument list of macro '{m.Name}' is never closed; add the missing ')'", name.Span);
                }

                return false;
            }

            if (tok.Kind == TokenKind.LParen)
            {
                depth++;
            }
            else if (tok.Kind == TokenKind.RParen)
            {
                if (depth == 0)
                {
                    rparen = tok;
                    break;
                }

                depth--;
            }
            else if (tok.Kind == TokenKind.Comma && depth == 0 && !(m.IsVariadic && args.Count == nparams - 1))
            {
                args.Add(cur);
                cur = [];
                continue;
            }

            cur.Add(tok);
        }

        args.Add(cur);
        int n = args.Count;
        if (nparams == 0 && n == 1 && args[0].Count == 0)
        {
            args.Clear();
            n = 0;
        }

        if (m.IsVariadic)
        {
            int required = nparams - 1;
            if (n < required)
            {
                _diag.Error(PreprocessorCodes.MacroArgumentCount,
                    $"macro '{m.Name}' takes at least {Plural(required, "argument")} but {n.ToString(System.Globalization.CultureInfo.InvariantCulture)} {(n == 1 ? "was" : "were")} given; pass the missing {(required - n == 1 ? "argument" : "arguments")}",
                    name.Span);
                return false;
            }

            if (n == required)
            {
                vaOmitted = true;
                args.Add([]);
            }
        }
        else if (n != nparams)
        {
            _diag.Error(PreprocessorCodes.MacroArgumentCount,
                $"macro '{m.Name}' takes {Plural(nparams, "argument")} but {n.ToString(System.Globalization.CultureInfo.InvariantCulture)} {(n == 1 ? "was" : "were")} given; {(n < nparams ? "pass the missing arguments" : "remove the extra arguments")}",
                name.Span);
            return false;
        }

        return true;
    }

    private static string Plural(int n, string word) =>
        n.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + word + (n == 1 ? string.Empty : "s");

    private List<Token> Substitute(Macro m, List<List<Token>>? args, bool vaOmitted, in Token name, HideSet? hide)
    {
        BodyItem[] body = m.Body;
        var result = new List<Token>(body.Length + 4);
        List<Token>?[]? expanded = args is null ? null : new List<Token>?[args.Count];
        var piece = new List<Token>();
        bool pasteNext = false;
        int vaIndex = m.IsVariadic ? m.Params.Length - 1 : -1;
        SourceSpan site = name.Span;

        foreach (BodyItem it in body)
        {
            if (it.Kind == BodyKind.Paste)
            {
                pasteNext = true;
                continue;
            }

            piece.Clear();
            switch (it.Kind)
            {
                case BodyKind.Token:
                    piece.Add(new Token(it.Tok.Kind, it.Tok.Text, site, it.Tok.Flags | TokenTraits.FromMacro));
                    break;
                case BodyKind.Stringify:
                    piece.Add(Stringify(args![it.Param], site, it.Tok.Flags));
                    break;
                case BodyKind.ParamRaw:
                    if (args![it.Param].Count == 0)
                    {
                        piece.Add(new Token(TokenKind.Placemarker, string.Empty, site, it.Tok.Flags));
                    }
                    else
                    {
                        piece.AddRange(args[it.Param]);
                    }

                    break;
                default:
                {
                    List<Token> ex = expanded![it.Param] ??= ExpandList(args![it.Param]);
                    piece.AddRange(ex);
                    break;
                }
            }

            if (piece.Count > 0)
            {
                Token first = piece[0];
                TokenTraits keep = first.Flags & ~(TokenTraits.LeadingSpace | TokenTraits.AtLineStart);
                piece[0] = new Token(first.Kind, first.Text, first.Span, keep | (it.Tok.Flags & TokenTraits.LeadingSpace), first.Hide);
            }

            if (pasteNext && result.Count > 0 && piece.Count > 0)
            {
                pasteNext = false;
                Token left = result[^1];
                if (it.Kind == BodyKind.ParamRaw && it.Param == vaIndex && left.Kind == TokenKind.Comma)
                {
                    // GNU extension: ", ## __VA_ARGS__" drops the comma when the variable arguments are left out
                    // entirely; an explicitly empty one (F(1,)) keeps it, as DXC does.
                    if (vaOmitted)
                    {
                        result.RemoveAt(result.Count - 1);
                        continue;
                    }

                    result.AddRange(piece);
                    continue;
                }

                Token? merged = Paste(left, piece[0], name, m);
                if (merged is { } mt)
                {
                    result[^1] = mt;
                    for (int i = 1; i < piece.Count; i++)
                    {
                        result.Add(piece[i]);
                    }
                }
                else
                {
                    result.AddRange(piece);
                }

                continue;
            }

            pasteNext = false;
            result.AddRange(piece);
        }

        var final = new List<Token>(result.Count);
        foreach (Token t in result)
        {
            if (t.Kind != TokenKind.Placemarker)
            {
                final.Add(t.WithHide(HideSet.Union(t.Hide, hide)));
            }
        }

        if (final.Count > 0)
        {
            Token first = final[0];
            const TokenTraits Mask = TokenTraits.LeadingSpace | TokenTraits.AtLineStart;
            final[0] = first.WithFlags((first.Flags & ~Mask) | (name.Flags & Mask));
        }

        return final;
    }

    private static Token Stringify(List<Token> arg, SourceSpan site, TokenTraits flags)
    {
        var sb = new StringBuilder("\"");
        for (int i = 0; i < arg.Count; i++)
        {
            Token t = arg[i];
            if (i > 0 && (t.Flags & (TokenTraits.LeadingSpace | TokenTraits.AtLineStart)) != 0)
            {
                sb.Append(' ');
            }

            if (t.Kind is TokenKind.StringLiteral or TokenKind.CharLiteral)
            {
                foreach (char c in t.Text)
                {
                    if (c is '"' or '\\')
                    {
                        sb.Append('\\');
                    }

                    sb.Append(c);
                }
            }
            else
            {
                sb.Append(t.Text);
            }
        }

        sb.Append('"');
        return new Token(TokenKind.StringLiteral, sb.ToString(), site, flags | TokenTraits.FromMacro);
    }

    private Token? Paste(in Token left, in Token right, in Token name, Macro m)
    {
        if (left.Kind == TokenKind.Placemarker)
        {
            return right.Kind == TokenKind.Placemarker ? left : right.WithFlags((right.Flags & ~TokenTraits.LeadingSpace) | (left.Flags & TokenTraits.LeadingSpace));
        }

        if (right.Kind == TokenKind.Placemarker)
        {
            return left;
        }

        string text = left.Text + right.Text;
        var tmp = new List<Token>(2);
        Lexer.LexInto(new SourceFile("<paste>", text), _interner, tmp);
        if (tmp.Count != 1 || Lexer.IsErrorKind(tmp[0].Kind) || tmp[0].Kind == TokenKind.Unknown && tmp[0].Text.Length == 0)
        {
            _diag.Error(PreprocessorCodes.BadPaste,
                $"pasting '{left.Text}' and '{right.Text}' with '##' in macro '{m.Name}' does not make one valid token; check the operands of '##'",
                name.Span);
            return null;
        }

        Token r = tmp[0];
        return new Token(r.Kind, r.Text, name.Span, (left.Flags & (TokenTraits.LeadingSpace | TokenTraits.AtLineStart)) | TokenTraits.FromMacro,
            HideSet.Intersect(left.Hide, right.Hide));
    }

    // ---- #define ----------------------------------------------------------------------------

    /// <summary>Defines a macro from the tokens of a directive line; <paramref name="nameAt"/> is the index of the macro name.</summary>
    private void DefineMacro(List<Token> line, int nameAt, SourceSpan directive)
    {
        if (nameAt >= line.Count)
        {
            _diag.Error(PreprocessorCodes.BadMacroName, "'#define' needs a macro name, as in '#define NAME value'", line[^1].Span.IsKnown ? line[^1].Span : directive);
            return;
        }

        Token nameTok = line[nameAt];
        if (nameTok.Kind != TokenKind.Identifier)
        {
            _diag.Error(PreprocessorCodes.BadMacroName, $"'{nameTok.Text}' cannot be a macro name; use an identifier", nameTok.Span);
            return;
        }

        if (nameTok.Text == "defined")
        {
            _diag.Error(PreprocessorCodes.BadMacroName, "'defined' cannot be used as a macro name", nameTok.Span);
            return;
        }

        int i = nameAt + 1;
        bool function = i < line.Count && line[i].Kind == TokenKind.LParen && !line[i].HasLeadingSpace;
        var parms = new List<string>();
        bool variadic = false;
        if (function)
        {
            i++;
            bool expectParam = true;
            bool closed = false;
            while (i < line.Count)
            {
                Token t = line[i];
                if (t.Kind == TokenKind.RParen && (!expectParam || parms.Count == 0))
                {
                    closed = true;
                    i++;
                    break;
                }

                if (!expectParam)
                {
                    if (t.Kind == TokenKind.Comma && !variadic)
                    {
                        expectParam = true;
                        i++;
                        continue;
                    }

                    break;
                }

                if (t.Kind == TokenKind.Ellipsis)
                {
                    variadic = true;
                    parms.Add("__VA_ARGS__");
                    expectParam = false;
                    i++;
                }
                else if (t.Kind == TokenKind.Identifier)
                {
                    if (parms.Contains(t.Text))
                    {
                        _diag.Error(PreprocessorCodes.BadMacroDefinition, $"parameter '{t.Text}' of macro '{nameTok.Text}' is declared twice; rename one", t.Span);
                        return;
                    }

                    parms.Add(t.Text);
                    expectParam = false;
                    i++;
                    if (i < line.Count && line[i].Kind == TokenKind.Ellipsis)
                    {
                        variadic = true;
                        i++;
                    }
                }
                else
                {
                    break;
                }
            }

            if (!closed)
            {
                Token at = i < line.Count ? line[i] : line[^1];
                _diag.Error(PreprocessorCodes.BadMacroDefinition, $"the parameter list of macro '{nameTok.Text}' is malformed near '{at.Text}'; write names separated by commas and close it with ')'", at.Span);
                return;
            }
        }

        var items = new List<BodyItem>(line.Count - i);
        for (int k = i; k < line.Count; k++)
        {
            Token t = line[k];
            if (k == i)
            {
                t = t.WithFlags(t.Flags & ~TokenTraits.LeadingSpace);
            }

            if (t.Kind == TokenKind.HashHash)
            {
                if (k == i || k == line.Count - 1)
                {
                    _diag.Error(PreprocessorCodes.BadMacroDefinition, "'##' cannot be the first or last token of a macro body; it joins the tokens on both sides", t.Span);
                    return;
                }

                items.Add(new BodyItem(BodyKind.Paste, t, -1));
            }
            else if (function && t.Kind == TokenKind.Hash)
            {
                int p = k + 1 < line.Count && line[k + 1].Kind == TokenKind.Identifier ? parms.IndexOf(line[k + 1].Text) : -1;
                if (p < 0)
                {
                    _diag.Error(PreprocessorCodes.BadStringify, "'#' in a function-like macro must be followed by a parameter name, as in '#x'", t.Span);
                    return;
                }

                items.Add(new BodyItem(BodyKind.Stringify, t, p));
                k++;
            }
            else if (function && t.Kind == TokenKind.Identifier && parms.IndexOf(t.Text) >= 0)
            {
                int pi = parms.IndexOf(t.Text);
                bool nearPaste = (items.Count > 0 && items[^1].Kind == BodyKind.Paste) || (k + 1 < line.Count && line[k + 1].Kind == TokenKind.HashHash);
                items.Add(new BodyItem(nearPaste ? BodyKind.ParamRaw : BodyKind.Param, t, pi));
            }
            else
            {
                items.Add(new BodyItem(BodyKind.Token, t, -1));
            }
        }

        var macro = new Macro(nameTok.Text, nameTok.Span)
        {
            IsFunction = function,
            Params = parms.ToArray(),
            IsVariadic = variadic,
            Body = items.ToArray(),
        };

        if (_macros.TryGetValue(nameTok.Text, out Macro? old))
        {
            if (old.Builtin != BuiltinKind.None)
            {
                _diag.Error(PreprocessorCodes.BadMacroName, $"'{nameTok.Text}' is a built-in macro and cannot be redefined", nameTok.Span);
                return;
            }

            if (!_quiet && !old.SameAs(macro))
            {
                _diag.Warning(PreprocessorCodes.MacroRedefined,
                    $"macro '{nameTok.Text}' is redefined with a different value (first defined at {old.DefinedAt.Path}({Preprocessor.Int(old.DefinedAt.Line)},{Preprocessor.Int(old.DefinedAt.Column)})); '#undef' it first if that is intended",
                    nameTok.Span);
            }
        }

        _macros[nameTok.Text] = macro;
    }
}
