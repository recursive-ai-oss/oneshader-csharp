using OneShader.Diagnostics;
using OneShader.Lexing;

namespace OneShader.Preprocessing;

/// <summary>Stable diagnostic codes of the lexer and preprocessor (OSH0100-OSH0199). Codes are never reused.</summary>
internal static class PreprocessorCodes
{
    public const string UnterminatedString = "OSH0100";
    public const string UnterminatedChar = "OSH0101";
    public const string UnterminatedComment = "OSH0102";
    public const string InvalidNumber = "OSH0103";
    public const string UnterminatedConditional = "OSH0110";
    public const string StrayConditional = "OSH0111";
    public const string ElseAfterElse = "OSH0112";
    public const string UnknownDirective = "OSH0113";
    public const string ErrorDirective = "OSH0114";
    public const string WarningDirective = "OSH0115";
    public const string BadExpression = "OSH0116";
    public const string BadMacroName = "OSH0117";
    public const string BadMacroDefinition = "OSH0118";
    public const string MacroRedefined = "OSH0119";
    public const string IncludeNotFound = "OSH0120";
    public const string BadInclude = "OSH0121";
    public const string IncludeTooDeep = "OSH0122";
    public const string BadLine = "OSH0123";
    public const string MacroArgumentCount = "OSH0124";
    public const string UnterminatedMacroCall = "OSH0125";
    public const string BadPaste = "OSH0126";
    public const string ExpansionTooDeep = "OSH0127";
    public const string LimitExceeded = "OSH0128";
    public const string ExtraTokens = "OSH0129";
    public const string DivisionByZero = "OSH0130";
    public const string BadDefined = "OSH0131";
    public const string BadStringify = "OSH0132";
    public const string IncludeFailed = "OSH0133";

    /// <summary>Reports the diagnostic that belongs to a malformed-token kind; does nothing for other kinds.</summary>
    public static void ReportIfErrorToken(in Token t, DiagnosticBag diagnostics)
    {
        switch (t.Kind)
        {
            case TokenKind.UnterminatedString:
                diagnostics.Error(UnterminatedString, "string literal has no closing '\"' on its line; add the closing quote", t.Span);
                break;
            case TokenKind.UnterminatedChar:
                diagnostics.Error(UnterminatedChar, "character literal has no closing '\\'' on its line; add the closing quote", t.Span);
                break;
            case TokenKind.UnterminatedComment:
                diagnostics.Error(UnterminatedComment, "comment starting here has no closing '*/'; add it", t.Span);
                break;
            case TokenKind.InvalidNumber:
                diagnostics.Error(InvalidNumber, $"'{t.Text}' is not a valid number; valid suffixes are u/l for integers and h/f/l for floating-point values", t.Span);
                break;
        }
    }
}
