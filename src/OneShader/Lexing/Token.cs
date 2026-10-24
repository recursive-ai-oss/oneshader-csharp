using System;
using OneShader.Diagnostics;
using OneShader.Preprocessing;

namespace OneShader.Lexing;

/// <summary>Extra facts about how a token appeared in the source.</summary>
[Flags]
public enum TokenTraits : byte
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>Whitespace or a comment came directly before the token.</summary>
    LeadingSpace = 1,

    /// <summary>The token is the first one on its source line.</summary>
    AtLineStart = 2,

    /// <summary>The token was produced by expanding a macro; its span is the expansion site.</summary>
    FromMacro = 4,
}

/// <summary>
/// One token with its location. After preprocessing, <see cref="Span"/> names the original file, line
/// and column of the token. A token that a macro produced points at the place the macro was used
/// (the macro's name, or for a function-like macro that name's line and column), so diagnostics land on
/// text the user wrote. Arguments passed to a macro keep their own spans.
/// </summary>
public readonly struct Token
{
    /// <summary>Creates a token.</summary>
    public Token(TokenKind kind, string text, SourceSpan span, TokenTraits flags = TokenTraits.None)
        : this(kind, text, span, flags, null)
    {
    }

    internal Token(TokenKind kind, string text, SourceSpan span, TokenTraits flags, HideSet? hide)
    {
        Kind = kind;
        Text = text;
        Span = span;
        Flags = flags;
        Hide = hide;
    }

    /// <summary>What kind of token this is.</summary>
    public TokenKind Kind { get; }

    /// <summary>The spelling as written (line continuations removed); empty for <see cref="TokenKind.EndOfFile"/>.</summary>
    public string Text { get; }

    /// <summary>Where the token is in its original file.</summary>
    public SourceSpan Span { get; }

    /// <summary>How the token appeared in the source.</summary>
    public TokenTraits Flags { get; }

    internal HideSet? Hide { get; }

    /// <summary>True when whitespace or a comment preceded the token.</summary>
    public bool HasLeadingSpace => (Flags & TokenTraits.LeadingSpace) != 0;

    /// <summary>True when the token is the first on its line.</summary>
    public bool AtLineStart => (Flags & TokenTraits.AtLineStart) != 0;

    /// <summary>True for <see cref="TokenKind.Identifier"/> tokens spelled exactly <paramref name="name"/>.</summary>
    public bool IsIdentifier(string name) => Kind == TokenKind.Identifier && string.Equals(Text, name, StringComparison.Ordinal);

    internal Token WithFlags(TokenTraits flags) => new(Kind, Text, Span, flags, Hide);

    internal Token WithHide(HideSet? hide) => new(Kind, Text, Span, Flags, hide);

    internal Token Relocated(SourceSpan span, HideSet? hide) => new(Kind, Text, span, Flags | TokenTraits.FromMacro, hide);

    /// <inheritdoc/>
    public override string ToString() => Kind == TokenKind.EndOfFile ? "<eof>" : Text;
}
