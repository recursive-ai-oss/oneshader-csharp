using System;
using System.Collections.Generic;
using System.Globalization;
using OneShader.Diagnostics;
using OneShader.Lexing;
using OneShader.Types;

namespace OneShader.Syntax;

internal sealed partial class ParserImpl
{
    // ------------------------------------------------------------ expressions

    private BadExpression BadExpr()
    {
        return new BadExpression(SpanOf(Cur));
    }

    /// <summary>expression: assignment (',' assignment)*</summary>
    private Expression ParseExpression()
    {
        var left = ParseAssignment();
        int added = 0;
        while (At(TokenKind.Comma))
        {
            if (!Enter())
            {
                added++;
                break;
            }

            added++;
            Advance();
            var right = ParseAssignment();
            left = new CommaExpression(SpanFrom(left.Span), left, right);
        }

        _depth -= added;
        return left;
    }

    private static BinaryOperator? CompoundOperator(TokenKind k, out bool isAssign)
    {
        isAssign = true;
        switch (k)
        {
            case TokenKind.Equal: return null;
            case TokenKind.PlusEqual: return BinaryOperator.Add;
            case TokenKind.MinusEqual: return BinaryOperator.Subtract;
            case TokenKind.StarEqual: return BinaryOperator.Multiply;
            case TokenKind.SlashEqual: return BinaryOperator.Divide;
            case TokenKind.PercentEqual: return BinaryOperator.Modulo;
            case TokenKind.AmpEqual: return BinaryOperator.BitwiseAnd;
            case TokenKind.PipeEqual: return BinaryOperator.BitwiseOr;
            case TokenKind.CaretEqual: return BinaryOperator.BitwiseXor;
            case TokenKind.LessLessEqual: return BinaryOperator.ShiftLeft;
            case TokenKind.GreaterGreaterEqual: return BinaryOperator.ShiftRight;
            default:
                isAssign = false;
                return null;
        }
    }

    private Expression ParseAssignment()
    {
        if (!Enter())
        {
            Leave();
            return BadExpr();
        }

        try
        {
            var left = ParseConditional();
            var op = CompoundOperator(Kind, out bool isAssign);
            if (isAssign && !(_noAngle > 0 && Kind == TokenKind.GreaterGreaterEqual))
            {
                Advance();
                var right = ParseAssignment();
                return new AssignmentExpression(SpanFrom(left.Span), op, left, right);
            }

            return left;
        }
        finally
        {
            Leave();
        }
    }

    private Expression ParseConditional()
    {
        var cond = ParseBinary(1);
        if (!At(TokenKind.Question))
        {
            return cond;
        }

        if (!Enter())
        {
            Leave();
            return cond;
        }

        try
        {
            Advance();
            int saved = _noAngle;
            _noAngle = 0;
            var a = ParseAssignment();
            _noAngle = saved;
            Expect(TokenKind.Colon, "in the '?:' expression");
            var b = ParseAssignment();
            return new ConditionalExpression(SpanFrom(cond.Span), cond, a, b);
        }
        finally
        {
            Leave();
        }
    }

    private int BinaryPrecedence(TokenKind k, out BinaryOperator op)
    {
        switch (k)
        {
            case TokenKind.PipePipe: op = BinaryOperator.LogicalOr; return 1;
            case TokenKind.AmpAmp: op = BinaryOperator.LogicalAnd; return 2;
            case TokenKind.Pipe: op = BinaryOperator.BitwiseOr; return 3;
            case TokenKind.Caret: op = BinaryOperator.BitwiseXor; return 4;
            case TokenKind.Amp: op = BinaryOperator.BitwiseAnd; return 5;
            case TokenKind.EqualEqual: op = BinaryOperator.Equal; return 6;
            case TokenKind.BangEqual: op = BinaryOperator.NotEqual; return 6;
            case TokenKind.Less: op = BinaryOperator.Less; return 7;
            case TokenKind.LessEqual: op = BinaryOperator.LessEqual; return 7;
            case TokenKind.Greater when _noAngle == 0: op = BinaryOperator.Greater; return 7;
            case TokenKind.GreaterEqual when _noAngle == 0: op = BinaryOperator.GreaterEqual; return 7;
            case TokenKind.LessLess: op = BinaryOperator.ShiftLeft; return 8;
            case TokenKind.GreaterGreater when _noAngle == 0: op = BinaryOperator.ShiftRight; return 8;
            case TokenKind.Plus: op = BinaryOperator.Add; return 9;
            case TokenKind.Minus: op = BinaryOperator.Subtract; return 9;
            case TokenKind.Star: op = BinaryOperator.Multiply; return 10;
            case TokenKind.Slash: op = BinaryOperator.Divide; return 10;
            case TokenKind.Percent: op = BinaryOperator.Modulo; return 10;
            default:
                op = default;
                return 0;
        }
    }

    private Expression ParseBinary(int minPrecedence)
    {
        var left = ParseUnary();
        int added = 0;
        while (true)
        {
            int prec = BinaryPrecedence(Kind, out var op);
            if (prec == 0 || prec < minPrecedence)
            {
                break;
            }

            // A left-leaning chain a+b+c+... is one tree level per operator; count it like nesting.
            added++;
            if (!Enter())
            {
                break;
            }

            Advance();
            var right = ParseBinary(prec + 1);
            left = new BinaryExpression(SpanFrom(left.Span), op, left, right);
        }

        _depth -= added;
        return left;
    }

    private Expression ParseUnary()
    {
        if (!Enter())
        {
            Leave();
            return BadExpr();
        }

        try
        {
            var start = SpanOf(Cur);
            UnaryOperator op;
            switch (Kind)
            {
                case TokenKind.Plus: op = UnaryOperator.Plus; break;
                case TokenKind.Minus: op = UnaryOperator.Minus; break;
                case TokenKind.Bang: op = UnaryOperator.LogicalNot; break;
                case TokenKind.Tilde: op = UnaryOperator.BitwiseNot; break;
                case TokenKind.PlusPlus: op = UnaryOperator.PreIncrement; break;
                case TokenKind.MinusMinus: op = UnaryOperator.PreDecrement; break;
                default:
                    return ParsePostfix();
            }

            Advance();
            var operand = ParseUnary();
            return new UnaryExpression(SpanFrom(start), op, operand);
        }
        finally
        {
            Leave();
        }
    }

    private bool LooksLikeCast()
    {
        // At '(' : "( [modifiers] KnownType [::X] [<...>] [[N]] )"
        int i = 1;
        while (Peek(i).Kind == TokenKind.Identifier && ModifierFor(Peek(i).Text) != Modifiers.None && Peek(i + 1).Kind == TokenKind.Identifier)
        {
            i++;
        }

        var t = Peek(i);
        if (t.Kind != TokenKind.Identifier || IsReservedWord(t.Text) || !IsKnownTypeName(t.Text))
        {
            return false;
        }

        i++;
        while (Peek(i).Kind == TokenKind.ColonColon && Peek(i + 1).Kind == TokenKind.Identifier)
        {
            i += 2;
        }

        if (Peek(i).Kind == TokenKind.Less)
        {
            int angle = 0;
            int limit = i + 64;
            while (i < limit)
            {
                var k = Peek(i).Kind;
                if (k == TokenKind.Less)
                {
                    angle++;
                }
                else if (k == TokenKind.Greater)
                {
                    angle--;
                }
                else if (k == TokenKind.GreaterGreater)
                {
                    angle -= 2;
                }
                else if (k is TokenKind.EndOfFile or TokenKind.Semicolon or TokenKind.LBrace)
                {
                    return false;
                }

                i++;
                if (angle <= 0)
                {
                    break;
                }
            }
        }

        while (Peek(i).Kind == TokenKind.LBracket && Peek(i + 1).Kind == TokenKind.IntLiteral && Peek(i + 2).Kind == TokenKind.RBracket)
        {
            i += 3;
        }

        return Peek(i).Kind == TokenKind.RParen;
    }

    private Expression ParsePostfix()
    {
        var e = ParsePrimary();
        int added = 0;
        while (true)
        {
            var k = Kind;
            if (k is not (TokenKind.Dot or TokenKind.LBracket or TokenKind.LParen or TokenKind.PlusPlus or TokenKind.MinusMinus))
            {
                break;
            }

            added++;
            if (!Enter())
            {
                break;
            }

            if (k == TokenKind.Dot)
            {
                Advance();
                if (!At(TokenKind.Identifier))
                {
                    Report(SyntaxCodes.Expected, "Expected a member name after '.' but found " + Describe(Cur) + ".", SpanOf(Cur));
                    break;
                }

                var mt = Advance();
                var member = new Identifier(SpanOf(mt), mt.Text);
                if (At(TokenKind.Less) && (member.Text is "Load" or "Store" || _templateNames.Contains(member.Text)) && TryParseTemplateCall(out var targs, out var cargs))
                {
                    var callee = new MemberAccessExpression(SpanFrom(e.Span), e, member);
                    e = new CallExpression(SpanFrom(e.Span), callee, targs, cargs);
                }
                else
                {
                    e = new MemberAccessExpression(SpanFrom(e.Span), e, member);
                }
            }
            else if (k == TokenKind.LBracket)
            {
                Advance();
                int saved = _noAngle;
                _noAngle = 0;
                var idx = ParseExpression();
                _noAngle = saved;
                Expect(TokenKind.RBracket, "to close the index");
                e = new IndexExpression(SpanFrom(e.Span), e, idx);
            }
            else if (k == TokenKind.LParen)
            {
                Advance();
                var args = ParseArguments();
                e = new CallExpression(SpanFrom(e.Span), e, [], args);
            }
            else
            {
                Advance();
                e = new UnaryExpression(SpanFrom(e.Span), k == TokenKind.PlusPlus ? UnaryOperator.PostIncrement : UnaryOperator.PostDecrement, e);
            }
        }

        _depth -= added;
        return e;
    }

    /// <summary>After '(' : arguments up to and including ')'.</summary>
    private List<Expression> ParseArguments()
    {
        var args = new List<Expression>();
        int saved = _noAngle;
        _noAngle = 0;
        while (!AtEof && !At(TokenKind.RParen))
        {
            CheckCancel();
            int before = _pos;
            args.Add(ParseAssignment());
            if (!Accept(TokenKind.Comma))
            {
                break;
            }

            if (_pos == before)
            {
                Advance();
            }
        }

        _noAngle = saved;
        Expect(TokenKind.RParen, "to close the argument list");
        return args;
    }

    /// <summary>At '&lt;' after a name: tries "&lt;args&gt;(call-args)"; restores the position and returns false if that is not a template call.</summary>
    private bool TryParseTemplateCall(out List<TemplateArgument> targs, out List<Expression> cargs)
    {
        targs = [];
        cargs = [];
        if (_speculating > 0)
        {
            // No nested attempts: they would multiply the work for inputs like 'a.Load < a.Load < ...'.
            return false;
        }

        int savedAngle = _noAngle;
        int save = _pos;
        int savedDepth = _depth;
        var savedTokens = (Token)_t[_pos];
        bool savedFail = _speculationFailed;
        _speculating++;
        _speculationFailed = false;
        var tmp = new List<TemplateArgument>();
        ParseTemplateArguments(tmp);
        bool ok = !_speculationFailed && At(TokenKind.LParen);
        _speculating--;
        _speculationFailed = savedFail;
        if (!ok)
        {
            _pos = save;
            _depth = savedDepth;
            _t[save] = savedTokens;
            _noAngle = savedAngle;
            return false;
        }

        // The '>>' split mutates a token; a successful parse keeps it.
        Advance();
        targs = tmp;
        cargs = ParseArguments();
        return true;
    }

    private static string StripSuffix(string text, out bool u, out bool l, out bool f, out bool h)
    {
        u = l = f = h = false;
        int end = text.Length;
        while (end > 0)
        {
            char c = text[end - 1];
            if (c is 'u' or 'U')
            {
                u = true;
            }
            else if (c is 'l' or 'L')
            {
                l = true;
            }
            else if ((c is 'f' or 'F') && !IsHexText(text))
            {
                f = true;
            }
            else if ((c is 'h' or 'H') && !IsHexText(text))
            {
                h = true;
            }
            else
            {
                break;
            }

            end--;
        }

        return text.Substring(0, end);
    }

    private static bool IsHexText(string t) => t.Length > 1 && t[0] == '0' && (t[1] is 'x' or 'X');

    private LiteralExpression ParseIntLiteral(Token t)
    {
        var span = SpanOf(t);
        string body = StripSuffix(t.Text, out bool u, out bool l, out _, out _);
        int radix = 10;
        string digits = body;
        if (IsHexText(body))
        {
            radix = 16;
            digits = body.Substring(2);
        }
        else if (body.Length > 2 && body[0] == '0' && body[1] is 'b' or 'B')
        {
            radix = 2;
            digits = body.Substring(2);
        }
        else if (body.Length > 1 && body[0] == '0')
        {
            radix = 8;
            digits = body.Substring(1);
        }

        ulong value = 0;
        bool overflow = digits.Length == 0;
        foreach (char c in digits)
        {
            int d = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : 99;
            if (d >= radix)
            {
                overflow = true;
                break;
            }

            ulong next = unchecked(value * (ulong)radix + (ulong)d);
            if (value > (ulong.MaxValue - (ulong)d) / (ulong)radix)
            {
                overflow = true;
                break;
            }

            value = next;
        }

        if (overflow)
        {
            Report(SyntaxCodes.BadLiteral, "'" + t.Text + "' is not a valid integer literal (it may be larger than 64 bits).", span);
            value = 0;
        }

        ScalarKind? suffix = u ? (l ? ScalarKind.UInt64 : ScalarKind.UInt) : (l ? ScalarKind.Int64 : null);
        return new LiteralExpression(span, LiteralKind.Integer, t.Text) { IntegerValue = value, SuffixType = suffix };
    }

    private LiteralExpression ParseFloatLiteral(Token t)
    {
        var span = SpanOf(t);
        string body = StripSuffix(t.Text, out _, out bool l, out bool f, out bool h);
        if (!double.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || double.IsInfinity(v))
        {
            Report(SyntaxCodes.BadLiteral, "'" + t.Text + "' is not a valid floating-point literal.", span);
            v = 0;
        }

        ScalarKind? suffix = l ? ScalarKind.Double : h ? ScalarKind.Half : f ? ScalarKind.Float : null;
        return new LiteralExpression(span, LiteralKind.Float, t.Text) { FloatValue = v, SuffixType = suffix };
    }

    private Expression ParsePrimary()
    {
        var t = Cur;
        var start = SpanOf(t);
        switch (t.Kind)
        {
            case TokenKind.IntLiteral:
                Advance();
                return ParseIntLiteral(t);
            case TokenKind.FloatLiteral:
                Advance();
                return ParseFloatLiteral(t);
            case TokenKind.StringLiteral:
            {
                Advance();
                string s = t.Text.Length >= 2 ? t.Text.Substring(1, t.Text.Length - 2) : "";
                s = s.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
                return new LiteralExpression(start, LiteralKind.String, s);
            }

            case TokenKind.CharLiteral:
            {
                Advance();
                Report(SyntaxCodes.BadLiteral, "Character literals are not supported in HLSL; use an integer such as 65 instead of " + t.Text + ".", start);
                return new LiteralExpression(start, LiteralKind.Integer, "0");
            }

            case TokenKind.LParen:
            {
                if (LooksLikeCast())
                {
                    Advance();
                    ParseModifiers();
                    var type = ParseType();
                    while (At(TokenKind.LBracket))
                    {
                        Advance();
                        ParseAssignment();
                        Expect(TokenKind.RBracket);
                    }

                    Expect(TokenKind.RParen, "to close the cast");
                    var operand = ParseUnary();
                    return new CastExpression(SpanFrom(start), type, operand);
                }

                Advance();
                int saved = _noAngle;
                _noAngle = 0;
                var inner = ParseExpression();
                _noAngle = saved;
                Expect(TokenKind.RParen, "to close the parenthesis");
                return new ParenthesizedExpression(SpanFrom(start), inner);
            }

            case TokenKind.LBrace:
                return ParseInitializer();
            case TokenKind.Identifier:
                return ParseIdentifierPrimary(t, start);
            case TokenKind.Unknown:
                Report(SyntaxCodes.StrayCharacter, "The character '" + t.Text + "' is not part of HLSL; remove it.", start);
                Advance();
                return new BadExpression(start);
            default:
                Report(SyntaxCodes.ExpectedExpression, "Expected an expression but found " + Describe(t) + ".", start);
                return new BadExpression(start);
        }
    }

    private Expression ParseIdentifierPrimary(Token t, SourceSpan start)
    {
        string text = t.Text;
        switch (text)
        {
            case "true" or "false":
                Advance();
                return new LiteralExpression(start, LiteralKind.Bool, text) { BoolValue = text == "true" };
            case "this":
                Advance();
                return new ThisExpression(start);
            case "sizeof":
            {
                Advance();
                if (At(TokenKind.LParen) && Peek(1).Kind == TokenKind.Identifier && IsKnownTypeName(Peek(1).Text))
                {
                    Advance();
                    var ty = ParseType();
                    Expect(TokenKind.RParen);
                    return new SizeOfExpression(SpanFrom(start), ty, null);
                }

                var operand = ParseUnary();
                return new SizeOfExpression(SpanFrom(start), null, operand);
            }
        }

        if (IsReservedWord(text))
        {
            Report(SyntaxCodes.ExpectedExpression, "Expected an expression but found the keyword '" + text + "'.", start);
            return new BadExpression(start);
        }

        bool known = IsKnownTypeName(text);
        if (known && (Peek(1).Kind == TokenKind.LParen
            || (Peek(1).Kind == TokenKind.Less && (BuiltinTypes.IsTemplateTypeName(text) || _templateNames.Contains(text)))))
        {
            var type = ParseType();
            if (At(TokenKind.LParen))
            {
                Advance();
                var args = ParseArguments();
                return new ConstructExpression(SpanFrom(start), type, args);
            }

            Report(SyntaxCodes.Expected, "Expected '(' after the type name '" + text + "' to construct a value.", SpanOf(Cur));
            return new BadExpression(SpanFrom(start));
        }

        var name = ParseQualifiedName();
        if (At(TokenKind.Less) && _templateNames.Contains(name.Last.Text) && TryParseTemplateCall(out var targs, out var cargs))
        {
            var callee = new NameExpression(name.Span, name);
            return new CallExpression(SpanFrom(start), callee, targs, cargs);
        }

        return new NameExpression(name.Span, name);
    }

    private Expression ParseInitializer()
    {
        if (!At(TokenKind.LBrace))
        {
            return ParseAssignment();
        }

        if (!Enter())
        {
            Leave();
            return BadExpr();
        }

        try
        {
            var start = SpanOf(Cur);
            Advance();
            var items = new List<Expression>();
            while (!AtEof && !At(TokenKind.RBrace))
            {
                CheckCancel();
                int before = _pos;
                items.Add(ParseInitializer());
                if (!Accept(TokenKind.Comma))
                {
                    break;
                }

                if (_pos == before)
                {
                    Advance();
                }
            }

            Expect(TokenKind.RBrace, "to close the initializer list");
            return new InitializerListExpression(SpanFrom(start), items);
        }
        finally
        {
            Leave();
        }
    }

    // ------------------------------------------------------------ statements

    private BlockStatement ParseBlock(List<AttributeSyntax> attrs)
    {
        var start = SpanOf(Cur);
        var list = new List<Statement>();
        if (!Enter())
        {
            Leave();
            return new BlockStatement(start, attrs, list);
        }

        try
        {
            Expect(TokenKind.LBrace);
            while (!AtEof && !At(TokenKind.RBrace))
            {
                CheckCancel();
                int before = _pos;
                list.Add(ParseStatement());
                if (_pos == before)
                {
                    Advance();
                }
            }

            Expect(TokenKind.RBrace, "to close the block");
            return new BlockStatement(SpanFrom(start), attrs, list);
        }
        finally
        {
            Leave();
        }
    }

    private void SynchronizeStatement()
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
            }
            else if (k == TokenKind.Semicolon && braces == 0)
            {
                Advance();
                return;
            }

            Advance();
        }
    }

    private bool LooksLikeLocalDeclaration()
    {
        if (!At(TokenKind.Identifier))
        {
            return false;
        }

        string text = Cur.Text;
        if (IsReservedWord(text))
        {
            return false;
        }

        if (text is "struct" or "class" or "enum" or "typedef" or "using" or "template" or "namespace")
        {
            return true;
        }

        if (IsModifierAt(0))
        {
            return true;
        }

        int i = 1;
        string last = text;
        while (Peek(i).Kind == TokenKind.ColonColon && Peek(i + 1).Kind == TokenKind.Identifier)
        {
            last = Peek(i + 1).Text;
            i += 2;
        }

        if (Peek(i).Kind == TokenKind.Less && (BuiltinTypes.IsTemplateTypeName(last) || _templateNames.Contains(last)))
        {
            int angle = 0;
            int limit = i + 64;
            while (i < limit)
            {
                var k = Peek(i).Kind;
                if (k == TokenKind.Less)
                {
                    angle++;
                }
                else if (k == TokenKind.Greater)
                {
                    angle--;
                }
                else if (k == TokenKind.GreaterGreater)
                {
                    angle -= 2;
                }
                else if (k is TokenKind.EndOfFile or TokenKind.Semicolon or TokenKind.LBrace)
                {
                    return false;
                }

                i++;
                if (angle <= 0)
                {
                    break;
                }
            }
        }

        var n = Peek(i);
        return n.Kind == TokenKind.Identifier && !IsReservedWord(n.Text);
    }

    private Statement ParseStatement()
    {
        if (!Enter())
        {
            Leave();
            return new BadStatement(SpanOf(Cur));
        }

        try
        {
            return ParseStatementCore();
        }
        finally
        {
            Leave();
        }
    }

    private Statement ParseStatementCore()
    {
        var start = SpanOf(Cur);
        if (At(TokenKind.Pragma))
        {
            var p = Advance();
            return new DeclarationStatement(start, [], new PragmaDeclaration(start, p.Text));
        }

        var attrs = ParseAttributes();
        if (attrs.Count > 0)
        {
            start = SpanOf(Cur);
        }

        switch (Kind)
        {
            case TokenKind.LBrace:
                return ParseBlock(attrs);
            case TokenKind.Semicolon:
                Advance();
                return new EmptyStatement(SpanFrom(start), attrs);
            case TokenKind.RBrace:
                ErrorHere(SyntaxCodes.UnexpectedToken, "Unexpected '}'.");
                return new BadStatement(start);
            case TokenKind.EndOfFile:
                return new BadStatement(start);
        }

        if (At(TokenKind.Identifier))
        {
            switch (Cur.Text)
            {
                case "if":
                    return ParseIf(attrs, start);
                case "for":
                    return ParseFor(attrs, start);
                case "while":
                    return ParseWhile(attrs, start);
                case "do":
                    return ParseDoWhile(attrs, start);
                case "switch":
                    return ParseSwitch(attrs, start);
                case "break":
                    Advance();
                    Expect(TokenKind.Semicolon, "after 'break'");
                    return new BreakStatement(SpanFrom(start), attrs);
                case "continue":
                    Advance();
                    Expect(TokenKind.Semicolon, "after 'continue'");
                    return new ContinueStatement(SpanFrom(start), attrs);
                case "discard":
                    Advance();
                    Expect(TokenKind.Semicolon, "after 'discard'");
                    return new DiscardStatement(SpanFrom(start), attrs);
                case "return":
                {
                    Advance();
                    Expression? value = null;
                    if (!At(TokenKind.Semicolon))
                    {
                        value = ParseExpression();
                    }

                    Expect(TokenKind.Semicolon, "after the return statement");
                    return new ReturnStatement(SpanFrom(start), attrs, value);
                }

                case "case" or "default":
                    ErrorHere(SyntaxCodes.UnexpectedToken, "'" + Cur.Text + "' can only appear directly inside a switch statement.");
                    SynchronizeStatement();
                    return new BadStatement(SpanFrom(start));
                case "else":
                    ErrorHere(SyntaxCodes.UnexpectedToken, "'else' has no matching 'if'.");
                    Advance();
                    return new BadStatement(SpanFrom(start));
            }

            if (LooksLikeLocalDeclaration())
            {
                var d = ParseDeclarationCore(false);
                if (d is null)
                {
                    return new BadStatement(SpanFrom(start));
                }

                return new DeclarationStatement(SpanFrom(start), attrs, d);
            }
        }

        var e = ParseExpression();
        if (e is BadExpression)
        {
            SynchronizeStatement();
            return new BadStatement(SpanFrom(start));
        }

        Expect(TokenKind.Semicolon, "after the expression");
        return new ExpressionStatement(SpanFrom(start), attrs, e);
    }

    private Expression ParseParenCondition(string what)
    {
        Expect(TokenKind.LParen, "after '" + what + "'");
        var c = ParseExpression();
        Expect(TokenKind.RParen, "to close the '" + what + "' condition");
        return c;
    }

    private IfStatement ParseIf(List<AttributeSyntax> attrs, SourceSpan start)
    {
        Advance();
        var cond = ParseParenCondition("if");
        var then = ParseStatement();
        Statement? els = null;
        if (AtIdent("else"))
        {
            Advance();
            els = ParseStatement();
        }

        return new IfStatement(SpanFrom(start), attrs, cond, then, els);
    }

    private WhileStatement ParseWhile(List<AttributeSyntax> attrs, SourceSpan start)
    {
        Advance();
        var cond = ParseParenCondition("while");
        var body = ParseStatement();
        return new WhileStatement(SpanFrom(start), attrs, cond, body);
    }

    private DoWhileStatement ParseDoWhile(List<AttributeSyntax> attrs, SourceSpan start)
    {
        Advance();
        var body = ParseStatement();
        if (!AcceptIdent("while"))
        {
            Report(SyntaxCodes.Expected, "Expected 'while' after the body of 'do' but found " + Describe(Cur) + ".", SpanOf(Cur));
        }

        var cond = ParseParenCondition("while");
        Expect(TokenKind.Semicolon, "after 'do ... while (...)'");
        return new DoWhileStatement(SpanFrom(start), attrs, body, cond);
    }

    private ForStatement ParseFor(List<AttributeSyntax> attrs, SourceSpan start)
    {
        Advance();
        Expect(TokenKind.LParen, "after 'for'");
        Statement? init = null;
        if (At(TokenKind.Semicolon))
        {
            Advance();
        }
        else if (LooksLikeLocalDeclaration())
        {
            var ds = SpanOf(Cur);
            var d = ParseDeclarationCore(false);
            init = d is null ? new BadStatement(ds) : new DeclarationStatement(SpanFrom(ds), [], d);
        }
        else
        {
            var es = SpanOf(Cur);
            var e = ParseExpression();
            Expect(TokenKind.Semicolon, "after the for-loop initializer");
            init = new ExpressionStatement(SpanFrom(es), [], e);
        }

        Expression? cond = null;
        if (!At(TokenKind.Semicolon))
        {
            cond = ParseExpression();
        }

        Expect(TokenKind.Semicolon, "after the for-loop condition");
        Expression? step = null;
        if (!At(TokenKind.RParen))
        {
            step = ParseExpression();
        }

        Expect(TokenKind.RParen, "to close the 'for' header");
        var body = ParseStatement();
        return new ForStatement(SpanFrom(start), attrs, init, cond, step, body);
    }

    private SwitchStatement ParseSwitch(List<AttributeSyntax> attrs, SourceSpan start)
    {
        Advance();
        var selector = ParseParenCondition("switch");
        var sections = new List<SwitchSection>();
        if (!At(TokenKind.LBrace))
        {
            Expect(TokenKind.LBrace, "to start the switch body");
            return new SwitchStatement(SpanFrom(start), attrs, selector, sections);
        }

        Advance();
        while (!AtEof && !At(TokenKind.RBrace))
        {
            CheckCancel();
            int before = _pos;
            var ss = SpanOf(Cur);
            var labels = new List<SwitchLabel>();
            while (AtIdent("case") || AtIdent("default"))
            {
                var ls = SpanOf(Cur);
                if (Cur.Text == "case")
                {
                    Advance();
                    var v = ParseConditional();
                    Expect(TokenKind.Colon, "after the case value");
                    labels.Add(new SwitchLabel(SpanFrom(ls), v));
                }
                else
                {
                    Advance();
                    Expect(TokenKind.Colon, "after 'default'");
                    labels.Add(new SwitchLabel(SpanFrom(ls), null));
                }
            }

            if (labels.Count == 0)
            {
                ErrorHere(SyntaxCodes.UnexpectedToken, "Expected 'case' or 'default' inside the switch but found " + Describe(Cur) + ".");
                SynchronizeStatement();
                if (_pos == before)
                {
                    Advance();
                }

                continue;
            }

            var stmts = new List<Statement>();
            while (!AtEof && !At(TokenKind.RBrace) && !AtIdent("case") && !AtIdent("default"))
            {
                int b2 = _pos;
                stmts.Add(ParseStatement());
                if (_pos == b2)
                {
                    Advance();
                }
            }

            sections.Add(new SwitchSection(SpanFrom(ss), labels, stmts));
        }

        Expect(TokenKind.RBrace, "to close the switch");
        return new SwitchStatement(SpanFrom(start), attrs, selector, sections);
    }
}
