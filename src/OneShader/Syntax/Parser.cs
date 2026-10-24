using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using OneShader.Diagnostics;
using OneShader.Lexing;
using OneShader.Types;

namespace OneShader.Syntax;

/// <summary>Diagnostic codes reported by the parser.</summary>
public static class SyntaxCodes
{
    /// <summary>A token other than the expected one.</summary>
    public const string Expected = "OSH0200";

    /// <summary>An expression was expected.</summary>
    public const string ExpectedExpression = "OSH0201";

    /// <summary>A declaration or statement cannot start with this token.</summary>
    public const string UnexpectedToken = "OSH0202";

    /// <summary>Nesting is deeper than the parser allows.</summary>
    public const string TooDeep = "OSH0203";

    /// <summary>A type name was expected.</summary>
    public const string ExpectedType = "OSH0204";

    /// <summary>A malformed literal.</summary>
    public const string BadLiteral = "OSH0205";

    /// <summary>A malformed register, packoffset or attribute.</summary>
    public const string BadAnnotation = "OSH0206";

    /// <summary>A character the language does not use.</summary>
    public const string StrayCharacter = "OSH0207";

    /// <summary>A construct the parser does not support.</summary>
    public const string Unsupported = "OSH0208";
}

/// <summary>Parses the preprocessed token stream of one shader into a syntax tree.</summary>
public static class Parser
{
    /// <summary>Deepest nesting of expressions, statements and declarations accepted. Deeper input is an error, not a stack overflow.</summary>
    public const int MaxNesting = 256;

    /// <summary>
    /// Parses a token stream that ends with an end-of-file token. Syntax errors are reported to
    /// <paramref name="diagnostics"/> and the parser recovers at the next statement or declaration, so
    /// the returned tree is always usable (erroneous parts are <see cref="BadExpression"/>,
    /// <see cref="BadStatement"/> and <see cref="BadDeclaration"/> nodes). Never throws for bad input.
    /// </summary>
    /// <param name="tokens">The tokens from <see cref="Preprocessing.Preprocessor.Preprocess"/>.</param>
    /// <param name="diagnostics">Receives syntax errors.</param>
    /// <param name="fileName">Path used for the span of the whole unit.</param>
    /// <param name="cancellationToken">Cancels parsing.</param>
    public static CompilationUnit Parse(IReadOnlyList<Token> tokens, DiagnosticBag diagnostics, string fileName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(fileName);
        return new ParserImpl(tokens, diagnostics, fileName, cancellationToken).ParseUnit();
    }
}

internal sealed partial class ParserImpl
{
    private readonly Token[] _t;
    private readonly DiagnosticBag _diag;
    private readonly string _file;
    private readonly CancellationToken _ct;
    private readonly HashSet<string> _userTypes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _templateNames = new(StringComparer.Ordinal);
    private int _pos;
    private int _lastErrorPos = -1;
    private int _depth;
    private int _speculating;
    private bool _speculationFailed;
    private int _noAngle;
    private bool _tooDeepReported;
    private IReadOnlyList<TemplateParameter> _pendingTemplate = [];

    public ParserImpl(IReadOnlyList<Token> tokens, DiagnosticBag diag, string file, CancellationToken ct)
    {
        var arr = new Token[tokens.Count + 1];
        for (int i = 0; i < tokens.Count; i++)
        {
            arr[i] = tokens[i];
        }

        // Guarantee a terminating end-of-file token even for hand-made streams.
        var eofSpan = tokens.Count > 0 ? tokens[^1].Span : new SourceSpan(file, 1, 1);
        arr[^1] = new Token(TokenKind.EndOfFile, "", eofSpan);
        if (tokens.Count > 0 && tokens[^1].Kind == TokenKind.EndOfFile)
        {
            arr[^1] = tokens[^1];
        }

        _t = arr;
        _diag = diag;
        _file = file;
        _ct = ct;
    }

    private Token Cur => _t[_pos];

    private TokenKind Kind => _t[_pos].Kind;

    private bool AtEof => _t[_pos].Kind == TokenKind.EndOfFile;

    private Token Peek(int n)
    {
        int i = _pos + n;
        return i < _t.Length ? _t[i] : _t[^1];
    }

    private Token Prev => _pos > 0 ? _t[_pos - 1] : _t[0];

    private bool At(TokenKind k) => _t[_pos].Kind == k;

    private bool AtIdent(string text) => _t[_pos].Kind == TokenKind.Identifier && string.Equals(_t[_pos].Text, text, StringComparison.Ordinal);

    private bool PeekIs(int n, TokenKind k) => Peek(n).Kind == k;

    private Token Advance()
    {
        var t = _t[_pos];
        if (_pos < _t.Length - 1)
        {
            _pos++;
        }

        return t;
    }

    private bool Accept(TokenKind k)
    {
        if (_t[_pos].Kind == k)
        {
            Advance();
            return true;
        }

        return false;
    }

    private bool AcceptIdent(string text)
    {
        if (AtIdent(text))
        {
            Advance();
            return true;
        }

        return false;
    }

    private static string Describe(Token t) => t.Kind == TokenKind.EndOfFile ? "end of file" : "'" + t.Text + "'";

    private static string Describe(TokenKind k) => k switch
    {
        TokenKind.Semicolon => "';'",
        TokenKind.LParen => "'('",
        TokenKind.RParen => "')'",
        TokenKind.LBrace => "'{'",
        TokenKind.RBrace => "'}'",
        TokenKind.LBracket => "'['",
        TokenKind.RBracket => "']'",
        TokenKind.Comma => "','",
        TokenKind.Colon => "':'",
        TokenKind.Greater => "'>'",
        TokenKind.Equal => "'='",
        TokenKind.Identifier => "a name",
        _ => k.ToString(),
    };

    // ------------------------------------------------------------ errors

    private SourceSpan SpanOf(Token t) => t.Span.Path.Length == 0 ? new SourceSpan(_file, t.Span.Line, t.Span.Column, t.Span.Offset, t.Span.Length) : t.Span;

    /// <summary>The position just after the previous token, where a missing terminator belongs.</summary>
    private SourceSpan AfterPrevious()
    {
        var p = Prev;
        var s = SpanOf(p);
        if (p.Flags.HasFlag(TokenTraits.FromMacro) || p.Text.Length == 0 || p.Text.Contains('\n', StringComparison.Ordinal))
        {
            return s;
        }

        return new SourceSpan(s.Path, s.Line, s.Column + p.Text.Length, s.Offset + p.Text.Length, 0);
    }

    private void Report(string code, string message, SourceSpan span)
    {
        // One mistake reports once: anything found at or before the last error position is a consequence of it.
        if (_speculating > 0)
        {
            _speculationFailed = true;
            return;
        }

        if (_pos <= _lastErrorPos)
        {
            return;
        }

        _lastErrorPos = _pos;
        _diag.Error(code, message, span);
    }

    private void ErrorHere(string code, string message) => Report(code, message, SpanOf(Cur));

    private Token Expect(TokenKind k, string context = "")
    {
        if (_t[_pos].Kind == k)
        {
            return Advance();
        }

        string where = context.Length > 0 ? " " + context : "";
        string msg = "Expected " + Describe(k) + where + " but found " + Describe(Cur) + ".";
        if (k is TokenKind.Semicolon or TokenKind.RParen or TokenKind.RBracket)
        {
            msg += k == TokenKind.Semicolon ? " Add the missing ';'." : "";
            Report(SyntaxCodes.Expected, msg, AfterPrevious());
        }
        else
        {
            Report(SyntaxCodes.Expected, msg, SpanOf(Cur));
        }

        return new Token(k, "", SpanOf(Cur));
    }

    private Identifier ExpectIdentifier(string what)
    {
        if (At(TokenKind.Identifier))
        {
            var t = Advance();
            return new Identifier(SpanOf(t), t.Text);
        }

        Report(SyntaxCodes.Expected, "Expected " + what + " but found " + Describe(Cur) + ".", SpanOf(Cur));
        return new Identifier(SpanOf(Cur), "<missing>");
    }

    private SourceSpan SpanFrom(SourceSpan start)
    {
        var p = SpanOf(Prev);
        if (p.Path == start.Path && p.Offset + p.Length > start.Offset)
        {
            return start with { Length = p.Offset + p.Length - start.Offset };
        }

        return start;
    }

    private bool Enter()
    {
        if (++_depth > Parser.MaxNesting)
        {
            if (!_tooDeepReported && _speculating == 0)
            {
                _tooDeepReported = true;
                _lastErrorPos = -1;
                Report(SyntaxCodes.TooDeep, "The code is nested more than " + Parser.MaxNesting.ToString(CultureInfo.InvariantCulture) + " levels deep (expressions, blocks and initializers); simplify or split it.", SpanOf(Cur));
            }

            _speculationFailed = true;
            // Skip the rest of the input so every loop and recursion unwinds at once.
            _pos = _t.Length - 1;
            return false;
        }

        return true;
    }

    private void Leave() => _depth--;

    private void CheckCancel()
    {
        if ((_pos & 0xFF) == 0)
        {
            _ct.ThrowIfCancellationRequested();
        }
    }

    // ------------------------------------------------------------ unit and declaration lists

    public CompilationUnit ParseUnit()
    {
        var start = SpanOf(Cur);
        var decls = ParseDeclarations(false);
        return new CompilationUnit(start, decls);
    }

    private List<Declaration> ParseDeclarations(bool untilBrace)
    {
        var list = new List<Declaration>();
        while (!AtEof)
        {
            CheckCancel();
            if (untilBrace && At(TokenKind.RBrace))
            {
                break;
            }

            int before = _pos;
            if (Accept(TokenKind.Semicolon))
            {
                continue;
            }

            if (At(TokenKind.RBrace))
            {
                ErrorHere(SyntaxCodes.UnexpectedToken, "Unexpected '}': there is no open block to close here. Remove it or add the missing '{'.");
                Advance();
                continue;
            }

            var d = ParseDeclaration(true);
            if (d is not null)
            {
                list.Add(d);
            }

            if (_pos == before)
            {
                Advance();
            }
        }

        return list;
    }

    private void SkipToDeclarationBoundary()
    {
        int braces = 0;
        while (!AtEof)
        {
            var k = Kind;
            if (k == TokenKind.LBrace)
            {
                braces++;
            }
            else if (k == TokenKind.RBrace)
            {
                if (braces == 0)
                {
                    return;
                }

                braces--;
                if (braces == 0)
                {
                    Advance();
                    Accept(TokenKind.Semicolon);
                    return;
                }
            }
            else if (k == TokenKind.Semicolon && braces == 0)
            {
                Advance();
                return;
            }

            Advance();
        }
    }

    /// <summary>Parses one declaration at global, namespace or struct-member level.</summary>
    private Declaration? ParseDeclaration(bool global)
    {
        if (!Enter())
        {
            Leave();
            return null;
        }

        try
        {
            return ParseDeclarationCore(global);
        }
        finally
        {
            Leave();
        }
    }

    private Declaration? ParseDeclarationCore(bool global)
    {
        if (At(TokenKind.Pragma))
        {
            var p = Advance();
            return new PragmaDeclaration(SpanOf(p), p.Text);
        }

        var startSpan = SpanOf(Cur);
        int startPos = _pos;
        var attrs = ParseAttributes();
        var template = _pendingTemplate;
        _pendingTemplate = [];
        if (At(TokenKind.Identifier))
        {
            switch (Cur.Text)
            {
                case "template":
                {
                    Advance();
                    var tp = ParseTemplateParameters();
                    foreach (var _ in tp)
                    {
                        // Parameters are only meaningful inside the declaration that follows.
                    }

                    _pendingTemplate = tp;
                    var inner = ParseDeclaration(global);
                    return inner;
                }

                case "struct" or "class" when Peek(1).Kind == TokenKind.LBrace
                    || (Peek(1).Kind == TokenKind.Identifier && Peek(2).Kind is TokenKind.LBrace or TokenKind.Colon or TokenKind.Semicolon):
                {
                    var sd = ParseStructDeclaration(attrs, template, startSpan);
                    if (At(TokenKind.Identifier) && !IsReservedWord(Cur.Text))
                    {
                        // struct { ... } instance;
                        var ty = new DeclaredTypeSyntax(SpanFrom(startSpan), sd);
                        var vn = ParseQualifiedName();
                        return ParseVariableRest([], Modifiers.None, ty, vn, startSpan);
                    }

                    Accept(TokenKind.Semicolon);
                    return sd;
                }

                case "enum" when Peek(1).Kind == TokenKind.LBrace
                    || (Peek(1).Kind == TokenKind.Identifier && Peek(2).Kind is TokenKind.LBrace or TokenKind.Colon)
                    || (Peek(1).Kind == TokenKind.Identifier && Peek(1).Text == "class"):
                    return ParseEnumDeclaration(attrs, startSpan);
                case "cbuffer" or "tbuffer":
                    return ParseConstantBuffer(attrs, startSpan);
                case "namespace":
                    return ParseNamespace(attrs, startSpan);
                case "typedef":
                    return ParseTypedef(attrs, startSpan);
                case "using" when Peek(1).Kind == TokenKind.Identifier && Peek(2).Kind == TokenKind.Equal:
                    return ParseUsing(attrs, startSpan);
            }
        }

        if (At(TokenKind.Pragma))
        {
            return null;
        }

        if (!IsDeclarationStart())
        {
            if (At(TokenKind.EndOfFile))
            {
                if (attrs.Count > 0)
                {
                    Report(SyntaxCodes.Expected, "Expected a declaration after the attribute but found end of file.", SpanOf(Cur));
                }

                return null;
            }

            ErrorHere(SyntaxCodes.UnexpectedToken, "Unexpected " + Describe(Cur) + ": expected a declaration (a function, variable, struct or cbuffer).");
            SkipToDeclarationBoundary();
            return new BadDeclaration(SpanFrom(startSpan));
        }

        _ = startPos;
        return ParseVariableOrFunction(attrs, template, startSpan);
    }

    private bool IsDeclarationStart()
    {
        if (!At(TokenKind.Identifier))
        {
            return false;
        }

        return !IsReservedWord(Cur.Text);
    }

    private static bool IsReservedWord(string s) => s is "if" or "else" or "for" or "while" or "do" or "switch" or "case" or "default"
        or "break" or "continue" or "discard" or "return" or "true" or "false" or "this" or "sizeof";

    // ------------------------------------------------------------ attributes and annotations

    private bool AtDoubleBracket() => At(TokenKind.LBracket) && Peek(1).Kind == TokenKind.LBracket && !Peek(1).HasLeadingSpace;

    private List<AttributeSyntax> ParseAttributes()
    {
        var list = new List<AttributeSyntax>();
        while (At(TokenKind.LBracket))
        {
            CheckCancel();
            int before = _pos;
            var start = SpanOf(Cur);
            if (AtDoubleBracket())
            {
                Advance();
                Advance();
                while (!AtEof && !At(TokenKind.RBracket))
                {
                    var a = ParseOneAttribute(start, true);
                    if (a is not null)
                    {
                        list.Add(a);
                    }

                    if (!Accept(TokenKind.Comma))
                    {
                        break;
                    }
                }

                Expect(TokenKind.RBracket, "to close the attribute");
                Expect(TokenKind.RBracket, "to close the attribute");
            }
            else
            {
                Advance();
                var a = ParseOneAttribute(start, false);
                if (a is not null)
                {
                    list.Add(a);
                }

                Expect(TokenKind.RBracket, "to close the attribute");
            }

            if (_pos == before)
            {
                Advance();
            }
        }

        return list;
    }

    private AttributeSyntax? ParseOneAttribute(SourceSpan start, bool dbl)
    {
        if (!At(TokenKind.Identifier))
        {
            Report(SyntaxCodes.BadAnnotation, "Expected an attribute name but found " + Describe(Cur) + ".", SpanOf(Cur));
            return null;
        }

        var name = ParseQualifiedName();
        var args = new List<Expression>();
        if (Accept(TokenKind.LParen))
        {
            while (!AtEof && !At(TokenKind.RParen))
            {
                args.Add(ParseAssignment());
                if (!Accept(TokenKind.Comma))
                {
                    break;
                }
            }

            Expect(TokenKind.RParen, "to close the attribute arguments");
        }

        return new AttributeSyntax(SpanFrom(start), name, args, dbl);
    }

    private QualifiedName ParseQualifiedName()
    {
        var start = SpanOf(Cur);
        bool global = Accept(TokenKind.ColonColon);
        var parts = new List<Identifier>();
        parts.Add(ExpectIdentifier("a name"));
        while (At(TokenKind.ColonColon) && Peek(1).Kind == TokenKind.Identifier)
        {
            Advance();
            var t = Advance();
            parts.Add(new Identifier(SpanOf(t), t.Text));
        }

        return new QualifiedName(SpanFrom(start), parts) { IsGlobal = global };
    }

    private List<Annotation> ParseAnnotations()
    {
        var list = new List<Annotation>();
        while (At(TokenKind.Colon))
        {
            CheckCancel();
            int before = _pos;
            var start = SpanOf(Cur);
            Advance();
            if (!At(TokenKind.Identifier))
            {
                Report(SyntaxCodes.BadAnnotation, "Expected a semantic, register(...) or packoffset(...) after ':' but found " + Describe(Cur) + ".", SpanOf(Cur));
                break;
            }

            var word = Cur.Text;
            if (word == "register" && Peek(1).Kind == TokenKind.LParen)
            {
                Advance();
                var r = ParseRegister(start);
                if (r is not null)
                {
                    list.Add(r);
                }
            }
            else if (word == "packoffset" && Peek(1).Kind == TokenKind.LParen)
            {
                Advance();
                var p = ParsePackOffset(start);
                if (p is not null)
                {
                    list.Add(p);
                }
            }
            else
            {
                Advance();
                list.Add(new SemanticAnnotation(SpanFrom(start), word));
            }

            if (_pos == before)
            {
                Advance();
            }
        }

        // Effect-style annotation blocks <...> are not meaningful here; skip them silently.
        return list;
    }

    private static bool TryParseUInt(string s, out uint v) => uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v);

    private RegisterAnnotation? ParseRegister(SourceSpan start)
    {
        Expect(TokenKind.LParen);
        string? profile = null;
        var first = ExpectIdentifier("a register such as t0");
        string regText = first.Text;
        uint space = 0;
        if (Accept(TokenKind.Comma))
        {
            var second = ExpectIdentifier("a register or space");
            if (second.Text.StartsWith("space", StringComparison.Ordinal) && TryParseUInt(second.Text.AsSpan(5).ToString(), out uint sp))
            {
                space = sp;
            }
            else
            {
                profile = first.Text;
                regText = second.Text;
                if (Accept(TokenKind.Comma))
                {
                    var third = ExpectIdentifier("a register space such as space1");
                    if (third.Text.StartsWith("space", StringComparison.Ordinal) && TryParseUInt(third.Text.AsSpan(5).ToString(), out uint sp2))
                    {
                        space = sp2;
                    }
                    else
                    {
                        Report(SyntaxCodes.BadAnnotation, "Expected 'spaceN' (for example 'space1') but found '" + third.Text + "'.", third.Span);
                    }
                }
            }
        }

        Expect(TokenKind.RParen, "to close register(...)");
        if (regText.Length < 2 || !TryParseUInt(regText.AsSpan(1).ToString(), out uint index))
        {
            Report(SyntaxCodes.BadAnnotation, "'" + regText + "' is not a register: write a class letter and a number, such as t0, s1, b2 or u3.", first.Span);
            return null;
        }

        return new RegisterAnnotation(SpanFrom(start), profile, char.ToLowerInvariant(regText[0]), index, space);
    }

    private PackOffsetAnnotation? ParsePackOffset(SourceSpan start)
    {
        Expect(TokenKind.LParen);
        var reg = ExpectIdentifier("a register such as c2");
        uint comp = 0;
        if (Accept(TokenKind.Dot))
        {
            var c = ExpectIdentifier("a component x, y, z or w");
            comp = c.Text switch
            {
                "x" or "r" => 0,
                "y" or "g" => 1,
                "z" or "b" => 2,
                "w" or "a" => 3,
                _ => 99,
            };
            if (comp == 99)
            {
                Report(SyntaxCodes.BadAnnotation, "'" + c.Text + "' is not a packoffset component: use x, y, z or w.", c.Span);
                comp = 0;
            }
        }

        Expect(TokenKind.RParen, "to close packoffset(...)");
        if (reg.Text.Length < 2 || reg.Text[0] != 'c' || !TryParseUInt(reg.Text.AsSpan(1).ToString(), out uint index))
        {
            Report(SyntaxCodes.BadAnnotation, "'" + reg.Text + "' is not a packoffset register: write c followed by a number, such as c2.y.", reg.Span);
            return null;
        }

        return new PackOffsetAnnotation(SpanFrom(start), index, comp);
    }

    // ------------------------------------------------------------ modifiers and types

    private static Modifiers ModifierFor(string s) => s switch
    {
        "static" => Modifiers.Static,
        "const" => Modifiers.Const,
        "uniform" => Modifiers.Uniform,
        "volatile" => Modifiers.Volatile,
        "extern" => Modifiers.Extern,
        "shared" => Modifiers.Shared,
        "groupshared" => Modifiers.GroupShared,
        "precise" => Modifiers.Precise,
        "inline" or "__inline" or "forceinline" => Modifiers.Inline,
        "row_major" => Modifiers.RowMajor,
        "column_major" => Modifiers.ColumnMajor,
        "nointerpolation" => Modifiers.NoInterpolation,
        "linear" => Modifiers.Linear,
        "centroid" => Modifiers.Centroid,
        "sample" => Modifiers.Sample,
        "noperspective" => Modifiers.NoPerspective,
        "in" => Modifiers.In,
        "out" => Modifiers.Out,
        "inout" => Modifiers.InOut,
        "unorm" => Modifiers.Unorm,
        "snorm" => Modifiers.Snorm,
        "globallycoherent" => Modifiers.GloballyCoherent,
        "export" => Modifiers.Export,
        _ => Modifiers.None,
    };

    /// <summary>True when the identifier at <paramref name="n"/> is a modifier word used as one (a name that merely looks like a modifier, such as a variable called <c>sample</c>, is followed by an operator).</summary>
    private bool IsModifierAt(int n)
    {
        var t = Peek(n);
        if (t.Kind != TokenKind.Identifier || ModifierFor(t.Text) == Modifiers.None)
        {
            return false;
        }

        return Peek(n + 1).Kind == TokenKind.Identifier;
    }

    private Modifiers ParseModifiers()
    {
        var m = Modifiers.None;
        while (IsModifierAt(0))
        {
            m |= ModifierFor(Cur.Text);
            Advance();
        }

        return m;
    }

    private bool IsKnownTypeName(string name) => BuiltinTypes.IsTypeName(name) || _userTypes.Contains(name);

    private TypeSyntax ParseType()
    {
        var start = SpanOf(Cur);
        // 'struct Foo' / 'class Foo' / 'enum Foo' used as a type name; or an inline definition.
        if (At(TokenKind.Identifier) && Cur.Text is "struct" or "class" or "enum")
        {
            string kw = Cur.Text;
            if (Peek(1).Kind == TokenKind.LBrace || (Peek(1).Kind == TokenKind.Identifier && Peek(2).Kind is TokenKind.LBrace or TokenKind.Colon && kw != "enum"))
            {
                Declaration? d = kw == "enum" ? ParseEnumDeclaration([], start) : ParseStructDeclaration([], [], start);
                return new DeclaredTypeSyntax(SpanFrom(start), d ?? new BadDeclaration(start));
            }

            Advance();
        }

        if (!At(TokenKind.Identifier) || IsReservedWord(Cur.Text))
        {
            Report(SyntaxCodes.ExpectedType, "Expected a type name but found " + Describe(Cur) + ".", SpanOf(Cur));
            return new NamedTypeSyntax(SpanOf(Cur), new QualifiedName(SpanOf(Cur), [new Identifier(SpanOf(Cur), "<missing>")]), []);
        }

        var name = ParseQualifiedName();
        var args = new List<TemplateArgument>();
        if (At(TokenKind.Less) && (BuiltinTypes.IsTemplateTypeName(name.Last.Text) || _templateNames.Contains(name.Last.Text)))
        {
            ParseTemplateArguments(args);
        }

        return new NamedTypeSyntax(SpanFrom(start), name, args);
    }

    private void ParseTemplateArguments(List<TemplateArgument> args)
    {
        if (!Enter())
        {
            Leave();
            return;
        }

        Expect(TokenKind.Less);
        _noAngle++;
        while (!AtEof && !At(TokenKind.Greater) && !At(TokenKind.GreaterGreater))
        {
            int before = _pos;
            var s = SpanOf(Cur);
            if (At(TokenKind.Identifier) && (IsKnownTypeName(Cur.Text) || Peek(1).Kind is TokenKind.Greater or TokenKind.GreaterGreater or TokenKind.Comma or TokenKind.ColonColon or TokenKind.Less)
                && !IsReservedWord(Cur.Text))
            {
                var t = ParseType();
                args.Add(new TemplateArgument(SpanFrom(s), t, null));
            }
            else if (At(TokenKind.Identifier) && Cur.Text is "const" or "static" or "row_major" or "column_major" or "unorm" or "snorm")
            {
                var m = ParseModifiers();
                _ = m;
                var t = ParseType();
                args.Add(new TemplateArgument(SpanFrom(s), t, null));
            }
            else
            {
                var e = ParseConditional();
                args.Add(new TemplateArgument(SpanFrom(s), null, e));
            }

            if (!Accept(TokenKind.Comma))
            {
                break;
            }

            if (_pos == before)
            {
                Advance();
            }
        }

        _noAngle--;
        ExpectCloseAngle();
        Leave();
    }

    /// <summary>Consumes '>' closing a template list, splitting '>>' so that nested lists close one at a time.</summary>
    private void ExpectCloseAngle()
    {
        if (At(TokenKind.GreaterGreater))
        {
            var t = Cur;
            var s = SpanOf(t);
            _t[_pos] = new Token(TokenKind.Greater, ">", s with { Column = s.Column + 1, Offset = s.Offset + 1, Length = Math.Max(0, s.Length - 1) }, t.Flags & ~TokenTraits.LeadingSpace);
            return;
        }

        Expect(TokenKind.Greater, "to close the template argument list");
    }

    private List<TemplateParameter> ParseTemplateParameters()
    {
        var list = new List<TemplateParameter>();
        Expect(TokenKind.Less, "after 'template'");
        while (!AtEof && !At(TokenKind.Greater))
        {
            int before = _pos;
            var s = SpanOf(Cur);
            if (AtIdent("typename") || AtIdent("class"))
            {
                Advance();
                var name = ExpectIdentifier("a template parameter name");
                _userTypes.Add(name.Text);
                TemplateArgument? def = null;
                if (Accept(TokenKind.Equal))
                {
                    var ds = SpanOf(Cur);
                    _noAngle++;
                    var dt = ParseType();
                    _noAngle--;
                    def = new TemplateArgument(SpanFrom(ds), dt, null);
                }

                list.Add(new TemplateParameter(SpanFrom(s), name, null, def));
            }
            else
            {
                var vt = ParseType();
                var name = ExpectIdentifier("a template parameter name");
                TemplateArgument? def = null;
                if (Accept(TokenKind.Equal))
                {
                    var ds = SpanOf(Cur);
                    _noAngle++;
                    var e = ParseConditional();
                    _noAngle--;
                    def = new TemplateArgument(SpanFrom(ds), null, e);
                }

                list.Add(new TemplateParameter(SpanFrom(s), name, vt, def));
            }

            if (!Accept(TokenKind.Comma))
            {
                break;
            }

            if (_pos == before)
            {
                Advance();
            }
        }

        Expect(TokenKind.Greater, "to close the template parameter list");
        return list;
    }

    // ------------------------------------------------------------ declarations

    private Declaration ParseVariableOrFunction(List<AttributeSyntax> attrs, IReadOnlyList<TemplateParameter> template, SourceSpan start)
    {
        var mods = ParseModifiers();
        var type = ParseType();
        if (At(TokenKind.Semicolon) && type is DeclaredTypeSyntax dts)
        {
            Advance();
            return dts.Declaration;
        }

        // Name: possibly qualified, possibly an operator.
        QualifiedName name;
        if (AtIdent("operator"))
        {
            var os = SpanOf(Cur);
            Advance();
            string op = Cur.Text;
            if (At(TokenKind.LParen) && Peek(1).Kind == TokenKind.RParen)
            {
                Advance();
                op = "()";
                Advance();
            }
            else if (At(TokenKind.LBracket) && Peek(1).Kind == TokenKind.RBracket)
            {
                op = "[]";
                Advance();
                Advance();
            }
            else
            {
                Advance();
            }

            name = new QualifiedName(SpanFrom(os), [new Identifier(SpanFrom(os), "operator" + op)]);
        }
        else if (At(TokenKind.Identifier))
        {
            name = ParseQualifiedName();
        }
        else
        {
            ErrorHere(SyntaxCodes.Expected, "Expected a name after the type but found " + Describe(Cur) + ".");
            SkipToDeclarationBoundary();
            return new BadDeclaration(SpanFrom(start));
        }

        if (At(TokenKind.LParen))
        {
            return ParseFunctionRest(attrs, mods, type, name, template, start);
        }

        return ParseVariableRest(attrs, mods, type, name, start);
    }

    private VariableDeclaration ParseVariableRest(List<AttributeSyntax> attrs, Modifiers mods, TypeSyntax type, QualifiedName firstName, SourceSpan start)
    {
        var declarators = new List<Declarator>();
        var name = firstName.Parts[^1];
        while (true)
        {
            declarators.Add(ParseDeclaratorRest(name));
            if (!Accept(TokenKind.Comma))
            {
                break;
            }

            name = ExpectIdentifier("a variable name");
        }

        Expect(TokenKind.Semicolon, "after the declaration");
        if (type is DeclaredTypeSyntax)
        {
            // fine: struct { ... } instance;
        }

        return new VariableDeclaration(SpanFrom(start), attrs, mods, type, declarators);
    }

    private Declarator ParseDeclaratorRest(Identifier name)
    {
        var dims = new List<Expression?>();
        while (At(TokenKind.LBracket))
        {
            Advance();
            if (At(TokenKind.RBracket))
            {
                dims.Add(null);
            }
            else
            {
                dims.Add(ParseAssignment());
            }

            Expect(TokenKind.RBracket, "to close the array size");
        }

        var ann = ParseAnnotations();
        Expression? init = null;
        if (Accept(TokenKind.Equal))
        {
            init = ParseInitializer();
        }

        // Annotations may also follow the initializer in some code; accept either order.
        if (At(TokenKind.Colon))
        {
            ann.AddRange(ParseAnnotations());
        }

        return new Declarator(SpanFrom(name.Span), name, dims, ann, init);
    }

    private FunctionDeclaration ParseFunctionRest(List<AttributeSyntax> attrs, Modifiers mods, TypeSyntax returnType, QualifiedName name, IReadOnlyList<TemplateParameter> template, SourceSpan start)
    {
        if (template.Count > 0)
        {
            _templateNames.Add(name.Last.Text);
        }

        Expect(TokenKind.LParen);
        var ps = new List<ParameterDeclaration>();
        if (AtIdent("void") && Peek(1).Kind == TokenKind.RParen)
        {
            Advance();
        }

        while (!AtEof && !At(TokenKind.RParen))
        {
            CheckCancel();
            int before = _pos;
            ps.Add(ParseParameter());
            if (!Accept(TokenKind.Comma))
            {
                break;
            }

            if (_pos == before)
            {
                Advance();
            }
        }

        Expect(TokenKind.RParen, "to close the parameter list");
        if (AtIdent("const"))
        {
            Advance();
            mods |= Modifiers.ConstMember;
        }

        var ret = ParseAnnotations();
        BlockStatement? body = null;
        if (At(TokenKind.LBrace))
        {
            body = ParseBlock([]);
        }
        else
        {
            Expect(TokenKind.Semicolon, "after the function prototype or '{' to start its body");
        }

        return new FunctionDeclaration(SpanFrom(start), attrs, mods, returnType, name, template, ps, ret, body);
    }

    private ParameterDeclaration ParseParameter()
    {
        var start = SpanOf(Cur);
        var attrs = ParseAttributes();
        var mods = ParseModifiers();
        var type = ParseType();
        Identifier? name = null;
        if (At(TokenKind.Identifier))
        {
            var t = Advance();
            name = new Identifier(SpanOf(t), t.Text);
        }

        var dims = new List<Expression?>();
        while (At(TokenKind.LBracket))
        {
            Advance();
            dims.Add(At(TokenKind.RBracket) ? null : ParseAssignment());
            Expect(TokenKind.RBracket, "to close the array size");
        }

        var ann = ParseAnnotations();
        Expression? def = null;
        if (Accept(TokenKind.Equal))
        {
            def = ParseAssignment();
        }

        return new ParameterDeclaration(SpanFrom(start), attrs, mods, type, name, dims, ann, def);
    }

    private StructDeclaration ParseStructDeclaration(List<AttributeSyntax> attrs, IReadOnlyList<TemplateParameter> template, SourceSpan start)
    {
        bool isClass = Cur.Text == "class";
        Advance();
        Identifier? name = null;
        if (At(TokenKind.Identifier))
        {
            var t = Advance();
            name = new Identifier(SpanOf(t), t.Text);
            _userTypes.Add(t.Text);
            if (template.Count > 0)
            {
                _templateNames.Add(t.Text);
            }
        }

        var bases = new List<NamedTypeSyntax>();
        if (Accept(TokenKind.Colon))
        {
            do
            {
                AcceptIdent("public");
                var bt = ParseType();
                if (bt is NamedTypeSyntax nt)
                {
                    bases.Add(nt);
                }
            }
            while (Accept(TokenKind.Comma));
        }

        var members = new List<Declaration>();
        if (!At(TokenKind.LBrace))
        {
            if (At(TokenKind.Semicolon))
            {
                // Forward declaration: struct Foo;
                Advance();
                return new StructDeclaration(SpanFrom(start), attrs, isClass, name, template, bases, members);
            }

            Expect(TokenKind.LBrace, "to start the struct body");
            SkipToDeclarationBoundary();
            return new StructDeclaration(SpanFrom(start), attrs, isClass, name, template, bases, members);
        }

        Advance();
        while (!AtEof && !At(TokenKind.RBrace))
        {
            CheckCancel();
            int before = _pos;
            if (Accept(TokenKind.Semicolon))
            {
                continue;
            }

            var m = ParseDeclaration(false);
            if (m is not null)
            {
                members.Add(m);
            }

            if (_pos == before)
            {
                Advance();
            }
        }

        Expect(TokenKind.RBrace, "to close the struct");
        return new StructDeclaration(SpanFrom(start), attrs, isClass, name, template, bases, members);
    }

    private EnumDeclaration ParseEnumDeclaration(List<AttributeSyntax> attrs, SourceSpan start)
    {
        Advance();
        bool scoped = false;
        if (AtIdent("class") || AtIdent("struct"))
        {
            scoped = true;
            Advance();
        }

        Identifier? name = null;
        if (At(TokenKind.Identifier))
        {
            var t = Advance();
            name = new Identifier(SpanOf(t), t.Text);
            _userTypes.Add(t.Text);
        }

        TypeSyntax? underlying = null;
        if (Accept(TokenKind.Colon))
        {
            underlying = ParseType();
        }

        var members = new List<EnumMember>();
        Expect(TokenKind.LBrace, "to start the enum body");
        while (!AtEof && !At(TokenKind.RBrace))
        {
            int before = _pos;
            var ms = SpanOf(Cur);
            var mn = ExpectIdentifier("an enumerator name");
            Expression? val = null;
            if (Accept(TokenKind.Equal))
            {
                val = ParseConditional();
            }

            members.Add(new EnumMember(SpanFrom(ms), mn, val));
            if (!Accept(TokenKind.Comma))
            {
                break;
            }

            if (_pos == before)
            {
                Advance();
            }
        }

        Expect(TokenKind.RBrace, "to close the enum");
        var decl = new EnumDeclaration(SpanFrom(start), attrs, scoped, name, underlying, members);
        Accept(TokenKind.Semicolon);
        return decl;
    }

    private ConstantBufferDeclaration ParseConstantBuffer(List<AttributeSyntax> attrs, SourceSpan start)
    {
        bool tb = Cur.Text == "tbuffer";
        Advance();
        var name = ExpectIdentifier("a constant buffer name");
        var ann = ParseAnnotations();
        var members = new List<VariableDeclaration>();
        Expect(TokenKind.LBrace, "to start the constant buffer body");
        while (!AtEof && !At(TokenKind.RBrace))
        {
            CheckCancel();
            int before = _pos;
            if (Accept(TokenKind.Semicolon))
            {
                continue;
            }

            if (At(TokenKind.Pragma))
            {
                Advance();
                continue;
            }

            var d = ParseDeclaration(false);
            if (d is VariableDeclaration vd)
            {
                members.Add(vd);
            }
            else if (d is not null && d is not BadDeclaration)
            {
                Report(SyntaxCodes.UnexpectedToken, "Only variable declarations can appear inside a constant buffer.", d.Span);
            }

            if (_pos == before)
            {
                Advance();
            }
        }

        Expect(TokenKind.RBrace, "to close the constant buffer");
        Accept(TokenKind.Semicolon);
        return new ConstantBufferDeclaration(SpanFrom(start), attrs, tb, name, ann, members);
    }

    private NamespaceDeclaration ParseNamespace(List<AttributeSyntax> attrs, SourceSpan start)
    {
        Advance();
        var name = ExpectIdentifier("a namespace name");
        Expect(TokenKind.LBrace, "to start the namespace body");
        var members = ParseDeclarations(true);
        Expect(TokenKind.RBrace, "to close the namespace");
        return new NamespaceDeclaration(SpanFrom(start), attrs, name, members);
    }

    private TypedefDeclaration ParseTypedef(List<AttributeSyntax> attrs, SourceSpan start)
    {
        Advance();
        var mods = ParseModifiers();
        var type = ParseType();
        var ds = new List<Declarator>();
        do
        {
            var n = ExpectIdentifier("a type name");
            _userTypes.Add(n.Text);
            ds.Add(ParseDeclaratorRest(n));
        }
        while (Accept(TokenKind.Comma));
        Expect(TokenKind.Semicolon, "after the typedef");
        return new TypedefDeclaration(SpanFrom(start), attrs, mods, type, ds);
    }

    private TypedefDeclaration ParseUsing(List<AttributeSyntax> attrs, SourceSpan start)
    {
        Advance();
        var n = ExpectIdentifier("a type name");
        _userTypes.Add(n.Text);
        Expect(TokenKind.Equal);
        var type = ParseType();
        Expect(TokenKind.Semicolon, "after the alias");
        var d = new Declarator(n.Span, n, [], [], null);
        return new TypedefDeclaration(SpanFrom(start), attrs, Modifiers.None, type, [d]);
    }
}
