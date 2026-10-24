using System;
using System.Collections.Generic;
using OneShader.Diagnostics;
using OneShader.Preprocessing;

namespace OneShader.Lexing;

/// <summary>
/// Splits HLSL text into tokens. Comments and whitespace are dropped (recorded as token flags), line
/// continuations (backslash-newline) are removed from token text while columns still refer to the
/// original file, and identifiers share interned strings.
/// </summary>
public static class Lexer
{
    /// <summary>
    /// Lexes <paramref name="file"/> without preprocessing. Malformed tokens (unterminated strings and
    /// comments, bad numbers) are returned as error-kind tokens and, when <paramref name="diagnostics"/>
    /// is given, reported. The list ends with an <see cref="TokenKind.EndOfFile"/> token. Never throws on bad text.
    /// </summary>
    public static List<Token> Lex(SourceFile file, DiagnosticBag? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        var tokens = new List<Token>();
        LexInto(file, new StringInterner(), tokens);
        if (diagnostics is not null)
        {
            foreach (Token t in tokens)
            {
                PreprocessorCodes.ReportIfErrorToken(t, diagnostics);
            }
        }

        (int line, int col) = file.GetLineColumn(file.Text.Length);
        tokens.Add(new Token(TokenKind.EndOfFile, string.Empty, new SourceSpan(file.Path, line, col, file.Text.Length, 0)));
        return tokens;
    }

    internal static bool IsErrorKind(TokenKind kind) =>
        kind is TokenKind.InvalidNumber or TokenKind.UnterminatedString or TokenKind.UnterminatedChar or TokenKind.UnterminatedComment;

    internal static void LexInto(SourceFile file, StringInterner interner, List<Token> tokens)
    {
        string text = file.Text;
        int[]? map = null;
        if (HasSplice(text))
        {
            text = RemoveSplices(text, out map);
        }

        new State(file, text, map, interner, tokens).Run();
    }

    private static bool HasSplice(string text)
    {
        int i = 0;
        while ((i = text.IndexOf('\\', i)) >= 0)
        {
            if (i + 1 < text.Length && text[i + 1] is '\n' or '\r')
            {
                return true;
            }

            i++;
        }

        return false;
    }

    // Phase 2 of C translation: delete backslash-newline. map[i] is the original offset of spliced char i,
    // and map[length] the original length, so columns and spans stay in the user's file.
    private static string RemoveSplices(string text, out int[] map)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        var m = new List<int>(text.Length + 1);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length && text[i + 1] is '\n' or '\r')
            {
                i++;
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                continue;
            }

            sb.Append(c);
            m.Add(i);
        }

        m.Add(text.Length);
        map = m.ToArray();
        return sb.ToString();
    }

    private sealed class State
    {
        private readonly SourceFile _file;
        private readonly string _s;
        private readonly int[]? _map;
        private readonly StringInterner _interner;
        private readonly List<Token> _out;
        private int _pos;
        private bool _lineStart = true;
        private bool _space;

        // Tracks "# include" so that <name> lexes as one header-name token: 0 none, 1 after '#', 2 after 'include'.
        private int _directive;

        public State(SourceFile file, string s, int[]? map, StringInterner interner, List<Token> output)
        {
            _file = file;
            _s = s;
            _map = map;
            _interner = interner;
            _out = output;
        }

        private int Orig(int p) => _map is null ? p : _map[p];

        private SourceSpan Span(int start, int end)
        {
            int o = Orig(start);
            (int line, int col) = _file.GetLineColumn(o);
            return new SourceSpan(_file.Path, line, col, o, Orig(end) - o);
        }

        private void Emit(TokenKind kind, string text, int start, int end)
        {
            TokenTraits flags = TokenTraits.None;
            if (_space)
            {
                flags |= TokenTraits.LeadingSpace;
            }

            if (_lineStart)
            {
                flags |= TokenTraits.AtLineStart;
            }

            _out.Add(new Token(kind, text, Span(start, end), flags));
            _space = false;
            if (kind == TokenKind.Hash && _lineStart)
            {
                _directive = 1;
            }
            else if (_directive == 1 && kind == TokenKind.Identifier && text == "include")
            {
                _directive = 2;
            }
            else
            {
                _directive = 0;
            }

            _lineStart = false;
        }

        public void Run()
        {
            string s = _s;
            int n = s.Length;
            if (n > 0 && s[0] == '\uFEFF')
            {
                _pos = 1;
            }

            while (_pos < n)
            {
                char c = s[_pos];
                if (c == '\n' || c == '\r')
                {
                    _pos++;
                    if (c == '\r' && _pos < n && s[_pos] == '\n')
                    {
                        _pos++;
                    }

                    _lineStart = true;
                    _space = true;
                    _directive = 0;
                }
                else if (c is ' ' or '\t' or '\f' or '\v')
                {
                    _pos++;
                    _space = true;
                }
                else if (c == '/' && _pos + 1 < n && s[_pos + 1] == '/')
                {
                    while (_pos < n && s[_pos] is not ('\n' or '\r'))
                    {
                        _pos++;
                    }

                    _space = true;
                }
                else if (c == '/' && _pos + 1 < n && s[_pos + 1] == '*')
                {
                    BlockComment();
                }
                else if (IsIdentStart(c))
                {
                    int start = _pos++;
                    while (_pos < n && IsIdentPart(s[_pos]))
                    {
                        _pos++;
                    }

                    Emit(TokenKind.Identifier, _interner.Intern(s.AsSpan(start, _pos - start)), start, _pos);
                }
                else if (IsDigit(c) || (c == '.' && _pos + 1 < n && IsDigit(s[_pos + 1])))
                {
                    Number();
                }
                else if (c == '"' || c == '\'')
                {
                    Quoted(c);
                }
                else if (c == '<' && _directive == 2 && HeaderName())
                {
                    // emitted
                }
                else
                {
                    Punctuator();
                }
            }
        }

        private void BlockComment()
        {
            string s = _s;
            int start = _pos;
            int end = s.IndexOf("*/", _pos + 2, StringComparison.Ordinal);
            if (end < 0)
            {
                _pos = s.Length;
                Emit(TokenKind.UnterminatedComment, "/*", start, Math.Min(start + 2, s.Length));
                return;
            }

            _pos = end + 2;
            _space = true;
        }

        private bool HeaderName()
        {
            string s = _s;
            int start = _pos;
            int p = start + 1;
            while (p < s.Length && s[p] is not ('>' or '\n' or '\r'))
            {
                p++;
            }

            if (p >= s.Length || s[p] != '>')
            {
                return false;
            }

            _pos = p + 1;
            Emit(TokenKind.HeaderName, s.Substring(start, _pos - start), start, _pos);
            return true;
        }

        private void Quoted(char quote)
        {
            string s = _s;
            int start = _pos++;
            int n = s.Length;
            while (_pos < n)
            {
                char c = s[_pos];
                if (c == '\\' && _pos + 1 < n && s[_pos + 1] is not ('\n' or '\r'))
                {
                    _pos += 2;
                }
                else if (c == quote)
                {
                    _pos++;
                    Emit(quote == '"' ? TokenKind.StringLiteral : TokenKind.CharLiteral, s.Substring(start, _pos - start), start, _pos);
                    return;
                }
                else if (c is '\n' or '\r')
                {
                    break;
                }
                else
                {
                    _pos++;
                }
            }

            Emit(quote == '"' ? TokenKind.UnterminatedString : TokenKind.UnterminatedChar, s.Substring(start, _pos - start), start, _pos);
        }

        private void Number()
        {
            string s = _s;
            int start = _pos;
            int n = s.Length;
            bool hex = s[start] == '0' && start + 1 < n && s[start + 1] is 'x' or 'X';
            _pos++;
            while (_pos < n)
            {
                char c = s[_pos];
                if (IsIdentPart(c) || c == '.')
                {
                    // A sign continues the number only after an exponent letter: e/E in decimal, p/P in hex.
                    _pos++;
                    continue;
                }

                if (c is '+' or '-')
                {
                    char prev = s[_pos - 1];
                    if ((!hex && prev is 'e' or 'E') || (hex && prev is 'p' or 'P'))
                    {
                        _pos++;
                        continue;
                    }
                }

                break;
            }

            ReadOnlySpan<char> t = s.AsSpan(start, _pos - start);
            Emit(ClassifyNumber(t), _interner.Intern(t), start, _pos);
        }

        private void Punctuator()
        {
            string s = _s;
            int start = _pos;
            char c = s[start];
            char c1 = start + 1 < s.Length ? s[start + 1] : '\0';
            char c2 = start + 2 < s.Length ? s[start + 2] : '\0';
            (TokenKind kind, string text) = c switch
            {
                '{' => (TokenKind.LBrace, "{"),
                '}' => (TokenKind.RBrace, "}"),
                '(' => (TokenKind.LParen, "("),
                ')' => (TokenKind.RParen, ")"),
                '[' => (TokenKind.LBracket, "["),
                ']' => (TokenKind.RBracket, "]"),
                ';' => (TokenKind.Semicolon, ";"),
                ',' => (TokenKind.Comma, ","),
                '?' => (TokenKind.Question, "?"),
                '~' => (TokenKind.Tilde, "~"),
                '.' => c1 == '.' && c2 == '.' ? (TokenKind.Ellipsis, "...") : (TokenKind.Dot, "."),
                ':' => c1 == ':' ? (TokenKind.ColonColon, "::") : (TokenKind.Colon, ":"),
                '+' => c1 == '+' ? (TokenKind.PlusPlus, "++") : c1 == '=' ? (TokenKind.PlusEqual, "+=") : (TokenKind.Plus, "+"),
                '-' => c1 == '-' ? (TokenKind.MinusMinus, "--") : c1 == '=' ? (TokenKind.MinusEqual, "-=") : (TokenKind.Minus, "-"),
                '*' => c1 == '=' ? (TokenKind.StarEqual, "*=") : (TokenKind.Star, "*"),
                '/' => c1 == '=' ? (TokenKind.SlashEqual, "/=") : (TokenKind.Slash, "/"),
                '%' => c1 == '=' ? (TokenKind.PercentEqual, "%=") : (TokenKind.Percent, "%"),
                '&' => c1 == '&' ? (TokenKind.AmpAmp, "&&") : c1 == '=' ? (TokenKind.AmpEqual, "&=") : (TokenKind.Amp, "&"),
                '|' => c1 == '|' ? (TokenKind.PipePipe, "||") : c1 == '=' ? (TokenKind.PipeEqual, "|=") : (TokenKind.Pipe, "|"),
                '^' => c1 == '=' ? (TokenKind.CaretEqual, "^=") : (TokenKind.Caret, "^"),
                '!' => c1 == '=' ? (TokenKind.BangEqual, "!=") : (TokenKind.Bang, "!"),
                '=' => c1 == '=' ? (TokenKind.EqualEqual, "==") : (TokenKind.Equal, "="),
                '<' => c1 == '<' ? (c2 == '=' ? (TokenKind.LessLessEqual, "<<=") : (TokenKind.LessLess, "<<")) : c1 == '=' ? (TokenKind.LessEqual, "<=") : (TokenKind.Less, "<"),
                '>' => c1 == '>' ? (c2 == '=' ? (TokenKind.GreaterGreaterEqual, ">>=") : (TokenKind.GreaterGreater, ">>")) : c1 == '=' ? (TokenKind.GreaterEqual, ">=") : (TokenKind.Greater, ">"),
                '#' => c1 == '#' ? (TokenKind.HashHash, "##") : (TokenKind.Hash, "#"),
                _ => (TokenKind.Unknown, string.Empty),
            };

            if (kind == TokenKind.Unknown)
            {
                // Keep surrogate pairs whole so the text stays valid UTF-16.
                int len = char.IsHighSurrogate(c) && start + 1 < s.Length && char.IsLowSurrogate(c1) ? 2 : 1;
                _pos += len;
                Emit(kind, s.Substring(start, len), start, _pos);
                return;
            }

            _pos += text.Length;
            Emit(kind, text, start, _pos);
        }
    }

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    private static bool IsIdentStart(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_' || (c > 127 && char.IsLetter(c));

    private static bool IsIdentPart(char c) => IsIdentStart(c) || IsDigit(c);

    /// <summary>
    /// Decides whether a preprocessing number is a valid integer or floating literal. Integers take
    /// <c>u</c>/<c>l</c> suffixes (<c>u</c>, <c>l</c>, <c>ul</c>, <c>ll</c>, <c>ull</c>, in either case and order);
    /// floats take one of <c>h</c>, <c>f</c>, <c>l</c> in either case.
    /// </summary>
    internal static TokenKind ClassifyNumber(ReadOnlySpan<char> t)
    {
        int i = 0;
        int n = t.Length;
        if (n >= 2 && t[0] == '0' && t[1] is 'x' or 'X')
        {
            i = 2;
            int digitsStart = i;
            while (i < n && Uri.IsHexDigit(t[i]))
            {
                i++;
            }

            // The hex digits swallow a trailing f/F, which is a digit here, not a suffix.
            return i > digitsStart && IsIntSuffix(t[i..]) ? TokenKind.IntLiteral : TokenKind.InvalidNumber;
        }

        int intDigits = 0;
        while (i < n && IsDigit(t[i]))
        {
            i++;
            intDigits++;
        }

        bool isFloat = false;
        int fracDigits = 0;
        if (i < n && t[i] == '.')
        {
            isFloat = true;
            i++;
            while (i < n && IsDigit(t[i]))
            {
                i++;
                fracDigits++;
            }
        }

        if (intDigits + fracDigits == 0)
        {
            return TokenKind.InvalidNumber;
        }

        if (i < n && t[i] is 'e' or 'E')
        {
            isFloat = true;
            i++;
            if (i < n && t[i] is '+' or '-')
            {
                i++;
            }

            int expDigits = 0;
            while (i < n && IsDigit(t[i]))
            {
                i++;
                expDigits++;
            }

            if (expDigits == 0)
            {
                return TokenKind.InvalidNumber;
            }
        }

        ReadOnlySpan<char> suffix = t[i..];
        if (isFloat)
        {
            return suffix.Length == 0 || (suffix.Length == 1 && suffix[0] is 'f' or 'F' or 'h' or 'H' or 'l' or 'L')
                ? TokenKind.FloatLiteral
                : TokenKind.InvalidNumber;
        }

        if (IsIntSuffix(suffix))
        {
            return TokenKind.IntLiteral;
        }

        // Digits followed by h or f (3f, 2h) are floating-point values.
        return suffix.Length == 1 && suffix[0] is 'f' or 'F' or 'h' or 'H' ? TokenKind.FloatLiteral : TokenKind.InvalidNumber;
    }

    private static bool IsIntSuffix(ReadOnlySpan<char> s)
    {
        int u = 0, l = 0;
        foreach (char c in s)
        {
            if (c is 'u' or 'U')
            {
                u++;
            }
            else if (c is 'l' or 'L')
            {
                l++;
            }
            else
            {
                return false;
            }
        }

        return u <= 1 && l <= 2;
    }
}
