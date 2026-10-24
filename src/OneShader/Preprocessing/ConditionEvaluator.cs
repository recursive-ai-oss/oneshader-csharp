using System;
using System.Collections.Generic;
using System.Globalization;
using OneShader.Diagnostics;
using OneShader.Lexing;

namespace OneShader.Preprocessing;

/// <summary>
/// Evaluates the integer constant expression of <c>#if</c>/<c>#elif</c> on already macro-expanded tokens.
/// Arithmetic is 64-bit with C's rules: wrapping, and unsigned when either operand is unsigned. Names that
/// are still identifiers evaluate to 0, except <c>true</c> (1), as DXC treats them. The first problem is
/// reported once and the condition counts as false, so one mistake gives one error.
/// </summary>
internal sealed class ConditionEvaluator
{
    private const int MaxDepth = 200;

    private readonly List<Token> _t;
    private readonly DiagnosticBag _diag;
    private readonly Token _directive;
    private int _p;
    private int _depth;
    private bool _failed;

    private readonly record struct Val(long V, bool U);

    private ConditionEvaluator(List<Token> tokens, Token directive, DiagnosticBag diag)
    {
        _t = tokens;
        _directive = directive;
        _diag = diag;
    }

    public static bool Evaluate(List<Token> tokens, Token directive, DiagnosticBag diag)
    {
        var e = new ConditionEvaluator(tokens, directive, diag);
        if (tokens.Count == 0)
        {
            e.Fail(directive, "the condition has no expression; write a value or comparison after the directive");
            return false;
        }

        Val v = e.Ternary(true);
        if (!e._failed && e._p < tokens.Count)
        {
            Token extra = tokens[e._p];
            e.Fail(extra, $"unexpected '{extra.Text}' after the expression; use an operator to combine values, or remove it");
        }

        return !e._failed && v.V != 0;
    }

    private void Fail(Token at, string message)
    {
        if (_failed)
        {
            return;
        }

        _failed = true;
        _diag.Error(PreprocessorCodes.BadExpression, message, at.Span.IsKnown ? at.Span : _directive.Span);
    }

    private Token Last => _t[^1];

    private bool Peek(TokenKind k) => _p < _t.Count && _t[_p].Kind == k;

    private bool Enter()
    {
        if (++_depth > MaxDepth)
        {
            Fail(_p < _t.Count ? _t[_p] : Last, "the expression is nested too deeply; simplify it");
            return false;
        }

        return true;
    }

    private Val Ternary(bool eval)
    {
        if (!Enter())
        {
            _depth--;
            return default;
        }

        Val cond = Binary(1, eval);
        if (_failed)
        {
            _depth--;
            return default;
        }

        if (Peek(TokenKind.Question))
        {
            _p++;
            Val a = Ternary(eval && cond.V != 0);
            if (!_failed && !Peek(TokenKind.Colon))
            {
                Fail(_p < _t.Count ? _t[_p] : Last, "expected ':' to finish the '?' conditional; add the ':' and the value for the false case");
            }

            if (_failed)
            {
                _depth--;
                return default;
            }

            _p++;
            Val b = Ternary(eval && cond.V == 0);
            _depth--;
            bool u = a.U || b.U;
            return new Val(cond.V != 0 ? a.V : b.V, u);
        }

        _depth--;
        return cond;
    }

    private static int Precedence(TokenKind k) => k switch
    {
        TokenKind.PipePipe => 1,
        TokenKind.AmpAmp => 2,
        TokenKind.Pipe => 3,
        TokenKind.Caret => 4,
        TokenKind.Amp => 5,
        TokenKind.EqualEqual or TokenKind.BangEqual => 6,
        TokenKind.Less or TokenKind.Greater or TokenKind.LessEqual or TokenKind.GreaterEqual => 7,
        TokenKind.LessLess or TokenKind.GreaterGreater => 8,
        TokenKind.Plus or TokenKind.Minus => 9,
        TokenKind.Star or TokenKind.Slash or TokenKind.Percent => 10,
        _ => 0,
    };

    private Val Binary(int minPrec, bool eval)
    {
        Val lhs = Unary(eval);
        while (!_failed && _p < _t.Count)
        {
            Token opTok = _t[_p];
            int prec = Precedence(opTok.Kind);
            if (prec == 0 || prec < minPrec)
            {
                break;
            }

            _p++;
            bool rhsEval = eval;
            if (opTok.Kind == TokenKind.AmpAmp)
            {
                rhsEval = eval && lhs.V != 0;
            }
            else if (opTok.Kind == TokenKind.PipePipe)
            {
                rhsEval = eval && lhs.V == 0;
            }

            Val rhs = Binary(prec + 1, rhsEval);
            if (_failed)
            {
                break;
            }

            lhs = Apply(opTok, lhs, rhs, eval);
        }

        return lhs;
    }

    private Val Apply(Token op, Val a, Val b, bool eval)
    {
        bool u = a.U || b.U;
        long x = a.V, y = b.V;
        switch (op.Kind)
        {
            case TokenKind.PipePipe: return new Val(x != 0 || y != 0 ? 1 : 0, false);
            case TokenKind.AmpAmp: return new Val(x != 0 && y != 0 ? 1 : 0, false);
            case TokenKind.Pipe: return new Val(x | y, u);
            case TokenKind.Caret: return new Val(x ^ y, u);
            case TokenKind.Amp: return new Val(x & y, u);
            case TokenKind.EqualEqual: return new Val(x == y ? 1 : 0, false);
            case TokenKind.BangEqual: return new Val(x != y ? 1 : 0, false);
            case TokenKind.Less: return new Val((u ? (ulong)x < (ulong)y : x < y) ? 1 : 0, false);
            case TokenKind.Greater: return new Val((u ? (ulong)x > (ulong)y : x > y) ? 1 : 0, false);
            case TokenKind.LessEqual: return new Val((u ? (ulong)x <= (ulong)y : x <= y) ? 1 : 0, false);
            case TokenKind.GreaterEqual: return new Val((u ? (ulong)x >= (ulong)y : x >= y) ? 1 : 0, false);
            case TokenKind.LessLess: return new Val(Shift(x, y, left: true, a.U), a.U);
            case TokenKind.GreaterGreater: return new Val(Shift(x, y, left: false, a.U), a.U);
            case TokenKind.Plus: return new Val(unchecked(x + y), u);
            case TokenKind.Minus: return new Val(unchecked(x - y), u);
            case TokenKind.Star: return new Val(unchecked(x * y), u);
            default:
                if (y == 0)
                {
                    if (eval)
                    {
                        Fail(op, $"division by zero in the expression ('{op.Text}' with a right side of 0); change the divisor");
                    }

                    return default;
                }

                if (u)
                {
                    return new Val(unchecked(op.Kind == TokenKind.Slash ? (long)((ulong)x / (ulong)y) : (long)((ulong)x % (ulong)y)), true);
                }

                if (y == -1)
                {
                    // long.MinValue / -1 overflows in .NET; the wrapped C result is the value itself.
                    return new Val(op.Kind == TokenKind.Slash ? unchecked(-x) : 0, false);
                }

                return new Val(op.Kind == TokenKind.Slash ? x / y : x % y, false);
        }
    }

    private static long Shift(long x, long count, bool left, bool unsigned)
    {
        if (count < 0)
        {
            left = !left;
            count = count == long.MinValue ? 64 : -count;
        }

        if (count >= 64)
        {
            return !left && !unsigned && x < 0 ? -1 : 0;
        }

        int c = (int)count;
        if (left)
        {
            return unchecked(x << c);
        }

        return unsigned ? unchecked((long)((ulong)x >> c)) : x >> c;
    }

    private Val Unary(bool eval)
    {
        if (_p >= _t.Count)
        {
            Fail(Last, $"the expression ends after '{Last.Text}' where a value was expected; add the missing operand");
            return default;
        }

        if (!Enter())
        {
            _depth--;
            return default;
        }

        Val result = UnaryCore(eval);
        _depth--;
        return result;
    }

    private Val UnaryCore(bool eval)
    {
        Token t = _t[_p];
        switch (t.Kind)
        {
            case TokenKind.Plus:
            case TokenKind.Minus:
            case TokenKind.Tilde:
            case TokenKind.Bang:
            {
                _p++;
                Val v = Unary(eval);
                if (_failed)
                {
                    return default;
                }

                return t.Kind switch
                {
                    TokenKind.Plus => v,
                    TokenKind.Minus => new Val(unchecked(-v.V), v.U),
                    TokenKind.Tilde => new Val(~v.V, v.U),
                    _ => new Val(v.V == 0 ? 1 : 0, false),
                };
            }

            case TokenKind.LParen:
            {
                _p++;
                Val v = Ternary(eval);
                if (_failed)
                {
                    return default;
                }

                if (!Peek(TokenKind.RParen))
                {
                    Fail(t, "this '(' is never closed; add the matching ')'");
                    return default;
                }

                _p++;
                return v;
            }

            case TokenKind.IntLiteral:
                _p++;
                if (!TryParseInt(t.Text, out ulong value, out bool isUnsigned))
                {
                    Fail(t, $"integer constant '{t.Text}' does not fit in 64 bits");
                    return default;
                }

                return new Val(unchecked((long)value), isUnsigned);

            case TokenKind.CharLiteral:
                _p++;
                if (!TryCharValue(t.Text, out long c))
                {
                    Fail(t, $"{t.Text} is not a valid single character constant");
                    return default;
                }

                return new Val(c, false);

            case TokenKind.Identifier:
                _p++;
                return new Val(t.Text == "true" ? 1 : 0, false);

            case TokenKind.FloatLiteral:
                Fail(t, $"'{t.Text}' is a floating-point constant; conditions only accept integers");
                return default;

            default:
                Fail(t, $"unexpected '{t.Text}' where a value was expected; use a number, a macro name, '(' or a unary operator");
                return default;
        }
    }

    internal static bool TryParseInt(string text, out ulong value, out bool isUnsigned)
    {
        int end = text.Length;
        isUnsigned = false;
        while (end > 0 && text[end - 1] is 'u' or 'U' or 'l' or 'L')
        {
            if (text[end - 1] is 'u' or 'U')
            {
                isUnsigned = true;
            }

            end--;
        }

        ReadOnlySpan<char> digits = text.AsSpan(0, end);
        bool ok;
        if (digits.Length > 2 && digits[0] == '0' && digits[1] is 'x' or 'X')
        {
            ok = ulong.TryParse(digits[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
        }
        else if (digits.Length > 1 && digits[0] == '0')
        {
            value = 0;
            ok = true;
            foreach (char c in digits[1..])
            {
                if (c is < '0' or > '7' || value > (ulong.MaxValue >> 3))
                {
                    ok = false;
                    break;
                }

                value = (value << 3) | (uint)(c - '0');
            }
        }
        else
        {
            ok = ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        if (ok && value > long.MaxValue)
        {
            isUnsigned = true;
        }

        return ok;
    }

    private static bool TryCharValue(string text, out long value)
    {
        value = 0;
        if (text.Length < 3)
        {
            return false;
        }

        string body = text.Substring(1, text.Length - 2);
        if (body[0] != '\\')
        {
            if (body.Length != 1)
            {
                return false;
            }

            value = body[0];
            return true;
        }

        if (body.Length == 2)
        {
            switch (body[1])
            {
                case 'n': value = 10; return true;
                case 't': value = 9; return true;
                case 'r': value = 13; return true;
                case '0': value = 0; return true;
                case 'a': value = 7; return true;
                case 'b': value = 8; return true;
                case 'f': value = 12; return true;
                case 'v': value = 11; return true;
                case '\\' or '\'' or '"' or '?': value = body[1]; return true;
                default: return false;
            }
        }

        if (body[1] == 'x' && body.Length > 2 && long.TryParse(body.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value))
        {
            return value <= 0xFF;
        }

        return false;
    }
}
