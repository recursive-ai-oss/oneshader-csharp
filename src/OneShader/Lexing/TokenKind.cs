namespace OneShader.Lexing;

/// <summary>
/// The kind of a token. Keywords are not classified: they are <see cref="Identifier"/> tokens
/// (macros may be named like keywords, so only the parser knows which names are keywords).
/// </summary>
public enum TokenKind : byte
{
    /// <summary>End of the token stream.</summary>
    EndOfFile = 0,

    /// <summary>A name: letters, digits and underscores, not starting with a digit.</summary>
    Identifier,

    /// <summary>An integer literal such as <c>42</c>, <c>0x2Au</c> or <c>7l</c>; the text keeps the suffix.</summary>
    IntLiteral,

    /// <summary>A floating-point literal such as <c>1.0</c>, <c>.5f</c>, <c>2e-3h</c> or <c>1.L</c>; the text keeps the suffix.</summary>
    FloatLiteral,

    /// <summary>A string literal including its quotes; escapes are not decoded.</summary>
    StringLiteral,

    /// <summary>A character literal including its quotes.</summary>
    CharLiteral,

    /// <summary>The <c>&lt;name&gt;</c> operand of <c>#include</c>, brackets included. Only the lexer's directive tracking produces it.</summary>
    HeaderName,

    /// <summary>
    /// A <c>#pragma</c> directive that survived preprocessing. <see cref="Token.Text"/> is the text after
    /// the word <c>pragma</c> (for example <c>pack_matrix(row_major)</c>), tokens joined by single spaces
    /// where the source had whitespace. Later stages act on the pragmas they know and ignore the rest.
    /// </summary>
    Pragma,

    /// <summary>A stray character that is not part of HLSL's token set (for example <c>@</c>); the parser reports it.</summary>
    Unknown,

    /// <summary>A numeric literal with an invalid suffix or shape, such as <c>12abc</c> or <c>1e</c>. Reported by the preprocessor.</summary>
    InvalidNumber,

    /// <summary>A string literal missing its closing quote. Reported by the preprocessor.</summary>
    UnterminatedString,

    /// <summary>A character literal missing its closing quote. Reported by the preprocessor.</summary>
    UnterminatedChar,

    /// <summary>A <c>/*</c> comment without <c>*/</c>. Reported by the preprocessor.</summary>
    UnterminatedComment,

    /// <summary>Internal marker for an empty macro argument next to <c>##</c>; never leaves the preprocessor.</summary>
    Placemarker,

    // Punctuators. "[[" and "]]" are two bracket tokens; the parser pairs adjacent ones (the second
    // has no leading space) so that a[b[0]] still parses. Likewise ">>" is one token that the parser
    // splits when it closes nested template argument lists.

    /// <summary><c>{</c></summary>
    LBrace,
    /// <summary><c>}</c></summary>
    RBrace,
    /// <summary><c>(</c></summary>
    LParen,
    /// <summary><c>)</c></summary>
    RParen,
    /// <summary><c>[</c></summary>
    LBracket,
    /// <summary><c>]</c></summary>
    RBracket,
    /// <summary><c>;</c></summary>
    Semicolon,
    /// <summary><c>,</c></summary>
    Comma,
    /// <summary><c>.</c></summary>
    Dot,
    /// <summary><c>:</c></summary>
    Colon,
    /// <summary><c>::</c></summary>
    ColonColon,
    /// <summary><c>?</c></summary>
    Question,
    /// <summary><c>+</c></summary>
    Plus,
    /// <summary><c>-</c></summary>
    Minus,
    /// <summary><c>*</c></summary>
    Star,
    /// <summary><c>/</c></summary>
    Slash,
    /// <summary><c>%</c></summary>
    Percent,
    /// <summary><c>&amp;</c></summary>
    Amp,
    /// <summary><c>|</c></summary>
    Pipe,
    /// <summary><c>^</c></summary>
    Caret,
    /// <summary><c>~</c></summary>
    Tilde,
    /// <summary><c>!</c></summary>
    Bang,
    /// <summary><c>&lt;</c></summary>
    Less,
    /// <summary><c>&gt;</c></summary>
    Greater,
    /// <summary><c>=</c></summary>
    Equal,
    /// <summary><c>++</c></summary>
    PlusPlus,
    /// <summary><c>--</c></summary>
    MinusMinus,
    /// <summary><c>&lt;&lt;</c></summary>
    LessLess,
    /// <summary><c>&gt;&gt;</c></summary>
    GreaterGreater,
    /// <summary><c>&lt;=</c></summary>
    LessEqual,
    /// <summary><c>&gt;=</c></summary>
    GreaterEqual,
    /// <summary><c>==</c></summary>
    EqualEqual,
    /// <summary><c>!=</c></summary>
    BangEqual,
    /// <summary><c>&amp;&amp;</c></summary>
    AmpAmp,
    /// <summary><c>||</c></summary>
    PipePipe,
    /// <summary><c>+=</c></summary>
    PlusEqual,
    /// <summary><c>-=</c></summary>
    MinusEqual,
    /// <summary><c>*=</c></summary>
    StarEqual,
    /// <summary><c>/=</c></summary>
    SlashEqual,
    /// <summary><c>%=</c></summary>
    PercentEqual,
    /// <summary><c>&amp;=</c></summary>
    AmpEqual,
    /// <summary><c>|=</c></summary>
    PipeEqual,
    /// <summary><c>^=</c></summary>
    CaretEqual,
    /// <summary><c>&lt;&lt;=</c></summary>
    LessLessEqual,
    /// <summary><c>&gt;&gt;=</c></summary>
    GreaterGreaterEqual,
    /// <summary><c>#</c></summary>
    Hash,
    /// <summary><c>##</c></summary>
    HashHash,
    /// <summary><c>...</c></summary>
    Ellipsis,
}
