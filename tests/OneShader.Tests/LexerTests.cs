using System.Linq;
using OneShader.Diagnostics;
using OneShader.Lexing;
using Xunit;

namespace OneShader.Tests;

public class LexerTests
{
    private static Token[] Lex(string s) =>
        Lexer.Lex(new SourceFile("t.hlsl", s)).Where(t => t.Kind != TokenKind.EndOfFile).ToArray();

    [Theory]
    [InlineData("42", TokenKind.IntLiteral)]
    [InlineData("0x2Au", TokenKind.IntLiteral)]
    [InlineData("0xFF", TokenKind.IntLiteral)]
    [InlineData("7l", TokenKind.IntLiteral)]
    [InlineData("7UL", TokenKind.IntLiteral)]
    [InlineData("1.0", TokenKind.FloatLiteral)]
    [InlineData("1.", TokenKind.FloatLiteral)]
    [InlineData(".5f", TokenKind.FloatLiteral)]
    [InlineData("2e-3h", TokenKind.FloatLiteral)]
    [InlineData("1e5", TokenKind.FloatLiteral)]
    [InlineData("1.0L", TokenKind.FloatLiteral)]
    [InlineData("1.f", TokenKind.FloatLiteral)]
    [InlineData("3f", TokenKind.FloatLiteral)]
    [InlineData("12abc", TokenKind.InvalidNumber)]
    [InlineData("1e", TokenKind.InvalidNumber)]
    [InlineData("1.0u", TokenKind.InvalidNumber)]
    [InlineData("0x", TokenKind.InvalidNumber)]
    public void ClassifiesNumbers(string text, TokenKind kind)
    {
        Token[] t = Lex(text);
        Assert.Single(t);
        Assert.Equal(kind, t[0].Kind);
        Assert.Equal(text, t[0].Text);
    }

    [Fact]
    public void ExponentSignBelongsToTheNumberButNotAfterHexDigits()
    {
        Assert.Equal(["1e+5"], Lex("1e+5").Select(t => t.Text));
        Assert.Equal(["0x1e", "+", "5"], Lex("0x1e+5").Select(t => t.Text));
        Assert.Equal(["a", "+", "5"], Lex("a+5").Select(t => t.Text));
    }

    [Fact]
    public void OperatorsLexLongestFirst()
    {
        Assert.Equal(
            ["a", "<<=", "b", ">>=", "c", "::", "d", "...", "&&", "||", "++", "--", "<=", ">=", "==", "!=", "#", "##"],
            Lex("a <<= b >>= c :: d ... && || ++ -- <= >= == != # ##").Select(t => t.Text));
    }

    [Fact]
    public void AttributeBracketsAreSeparateAdjacentTokens()
    {
        Token[] t = Lex("[[vk::binding(0,1)]] a[b[0]]");
        Assert.Equal(TokenKind.LBracket, t[0].Kind);
        Assert.Equal(TokenKind.LBracket, t[1].Kind);
        Assert.False(t[1].HasLeadingSpace);
        Assert.Equal(TokenKind.ColonColon, t[3].Kind);
        Assert.Equal(TokenKind.RBracket, t[10].Kind);
        Assert.Equal(TokenKind.RBracket, t[11].Kind);
        Assert.False(t[11].HasLeadingSpace);
        Assert.Equal(TokenKind.RBracket, t[^1].Kind);
        Assert.Equal(TokenKind.RBracket, t[^2].Kind);
    }

    [Fact]
    public void LineContinuationKeepsOriginalColumns()
    {
        Token[] t = Lex("ab\\\ncd ef\\\r\n  gh x");
        Assert.Equal("abcd", t[0].Text);
        Assert.Equal((1, 1, 6), (t[0].Span.Line, t[0].Span.Column, t[0].Span.Length));
        Assert.Equal(("ef", 2, 4), (t[1].Text, t[1].Span.Line, t[1].Span.Column));
        Assert.Equal(("gh", 3, 3), (t[2].Text, t[2].Span.Line, t[2].Span.Column));
        Assert.Equal(("x", 3, 6), (t[3].Text, t[3].Span.Line, t[3].Span.Column));
    }

    [Fact]
    public void CommentsAreTriviaAndSetFlags()
    {
        Token[] t = Lex("a/*x\ny*/b // tail\nc");
        Assert.Equal(["a", "b", "c"], t.Select(x => x.Text));
        Assert.True(t[1].HasLeadingSpace);
        Assert.False(t[1].AtLineStart);
        Assert.True(t[2].AtLineStart);
        Assert.Equal((2, 4), (t[1].Span.Line, t[1].Span.Column));
    }

    [Fact]
    public void HeaderNameOnlyAfterInclude()
    {
        Assert.Equal(TokenKind.HeaderName, Lex("#include <sys/a.h>")[2].Kind);
        Assert.Equal(TokenKind.Less, Lex("#define X <a>")[3].Kind);
        Assert.Equal(TokenKind.Less, Lex("x\n#include <unclosed\n")[3].Kind);
    }

    [Fact]
    public void BadTextNeverThrows()
    {
        var bag = new DiagnosticBag();
        var toks = Lexer.Lex(new SourceFile("t.hlsl", "\uFEFF@ ` \\ \"abc\n 'x\n /* open"), bag);
        Assert.Equal(TokenKind.EndOfFile, toks[^1].Kind);
        Assert.Equal(3, bag.Items.Count);
    }

    [Fact]
    public void IdentifiersShareStrings()
    {
        Token[] t = Lex("foo foo");
        Assert.Same(t[0].Text, t[1].Text);
    }
}
