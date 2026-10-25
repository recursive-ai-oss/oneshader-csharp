using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using OneShader.Diagnostics;
using OneShader.Lexing;

namespace OneShader.Preprocessing;

/// <summary>A macro given on the command line or by the build: <c>NAME</c> or <c>NAME=value</c>.</summary>
/// <param name="Name">The macro name; may include a parameter list, as in <c>SQR(x)</c>.</param>
/// <param name="Value">The replacement text. Null means <c>1</c>, like <c>-DNAME</c>; empty means an empty macro.</param>
public readonly record struct MacroDefinition(string Name, string? Value);

/// <summary>Settings for <see cref="Preprocessor.Preprocess"/>. Every property has a safe default.</summary>
public sealed class PreprocessorOptions
{
    /// <summary>Finds <c>#include</c> files. Default: a <see cref="FileSystemIncludeResolver"/> with no directories (the including file's own directory only).</summary>
    public IIncludeResolver? IncludeResolver { get; set; }

    /// <summary>Macros defined before the first line, in order, after the predefined ones (so they may replace them).</summary>
    public List<MacroDefinition> Defines { get; } = [];

    /// <summary>Adds a macro; see <see cref="MacroDefinition"/>.</summary>
    public PreprocessorOptions Define(string name, string? value = null)
    {
        Defines.Add(new MacroDefinition(name, value));
        return this;
    }

    /// <summary>The value of <c>__HLSL_VERSION</c>. Default 2021.</summary>
    public int HlslVersion { get; set; } = 2021;

    /// <summary>
    /// The target profile, such as <c>vs_6_0</c>, <c>ps_6_2</c> or <c>cs_6_6</c>. When set, defines
    /// <c>__SHADER_TARGET_STAGE</c>, <c>__SHADER_TARGET_MAJOR</c> and <c>__SHADER_TARGET_MINOR</c>
    /// with the values DXC uses. A profile that does not parse is ignored here; the caller reports it.
    /// </summary>
    public string? TargetProfile { get; set; }

    /// <summary>
    /// When set, the SPIR-V target is active: defines <c>__spirv__</c> (1) and
    /// <c>__SPIRV_MAJOR_VERSION__</c> (1) / <c>__SPIRV_MINOR_VERSION__</c> (this value, for example 5 for
    /// Vulkan 1.2, 6 for Vulkan 1.3), as DXC does. Default null: not a SPIR-V compile.
    /// </summary>
    public int? SpirvMinorVersion { get; set; }

    /// <summary>Deepest <c>#include</c> nesting before the include is refused. Default 64.</summary>
    public int MaxIncludeDepth { get; set; } = 64;

    /// <summary>Most files opened by <c>#include</c> in one run (catches exponential include trees). Default 4096.</summary>
    public int MaxIncludeCount { get; set; } = 4096;

    /// <summary>Most tokens read while expanding macros before the run stops (catches exponential macros). Default 20 million.</summary>
    public long MaxExpansionSteps { get; set; } = 20_000_000;

    /// <summary>Most tokens in the output before the run stops. Default 2 million (the largest shaders have a few hundred thousand tokens).</summary>
    public int MaxOutputTokens { get; set; } = 2_000_000;

    /// <summary>Deepest nesting of macro calls inside macro arguments. Default 128.</summary>
    public int MaxMacroNesting { get; set; } = 128;
}

/// <summary>The result of preprocessing one shader.</summary>
public sealed class PreprocessResult
{
    internal PreprocessResult(List<Token> tokens, List<string> files)
    {
        Tokens = tokens;
        Files = files;
    }

    /// <summary>
    /// The preprocessed tokens, ending with one <see cref="TokenKind.EndOfFile"/> token. Directives are gone;
    /// <c>#pragma</c> lines other than <c>once</c> remain as <see cref="TokenKind.Pragma"/> tokens in place.
    /// </summary>
    public IReadOnlyList<Token> Tokens { get; }

    /// <summary>Paths of every file read, main file first, in the order first opened.</summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>
    /// The tokens as text for debugging: one source line per output line (a macro call spread over lines
    /// stays on the lines of its tokens), original spacing kept, a space inserted where two tokens would
    /// otherwise fuse. The result lexes back to the same token texts.
    /// </summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        Token prev = default;
        bool first = true;
        foreach (Token t in Tokens)
        {
            if (t.Kind == TokenKind.EndOfFile)
            {
                break;
            }

            if (t.Kind == TokenKind.Pragma)
            {
                if (!first)
                {
                    sb.Append('\n');
                }

                sb.Append("#pragma ").Append(t.Text).Append('\n');
                first = true;
                continue;
            }

            if (!first)
            {
                if (t.AtLineStart)
                {
                    sb.Append('\n');
                }
                else if (t.HasLeadingSpace || WouldFuse(prev, t))
                {
                    sb.Append(' ');
                }
            }

            sb.Append(t.Text);
            prev = t;
            first = false;
        }

        if (!first)
        {
            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static bool WouldFuse(in Token a, in Token b)
    {
        if (a.Text.Length == 0 || b.Text.Length == 0)
        {
            return false;
        }

        char x = a.Text[^1], y = b.Text[0];
        bool wx = char.IsLetterOrDigit(x) || x == '_', wy = char.IsLetterOrDigit(y) || y == '_';
        if (wx && wy)
        {
            return true;
        }

        if (wx || wy)
        {
            // A number followed by '.' reads as one number, and '.' followed by a digit too.
            return (a.Kind is TokenKind.IntLiteral or TokenKind.FloatLiteral && y == '.') || (x == '.' && char.IsAsciiDigit(y));
        }

        var tmp = new List<Token>(3);
        Lexer.LexInto(new SourceFile("<join>", a.Text + b.Text), new StringInterner(), tmp);
        return tmp.Count != 2 || tmp[0].Text.Length != a.Text.Length;
    }
}

/// <summary>Entry point of the preprocessor.</summary>
public static class Preprocessor
{
    /// <summary>
    /// Preprocesses <paramref name="main"/>: resolves <c>#include</c>, expands macros, evaluates conditionals
    /// and returns the remaining tokens with their original locations. Problems are added to
    /// <paramref name="diagnostics"/> and processing continues where it can; bad input never throws. Include
    /// nesting, macro expansion work and output size are limited (see <see cref="PreprocessorOptions"/>), and
    /// hitting a limit is an error diagnostic.
    /// </summary>
    /// <param name="main">The shader's main file.</param>
    /// <param name="options">Settings; null means defaults.</param>
    /// <param name="diagnostics">Receives errors and warnings.</param>
    /// <param name="cancellationToken">Cancels the run with <see cref="OperationCanceledException"/>.</param>
    public static PreprocessResult Preprocess(SourceFile main, PreprocessorOptions? options, DiagnosticBag diagnostics, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(diagnostics);
        return new Session(options ?? new PreprocessorOptions(), diagnostics, cancellationToken).Run(main);
    }

    /// <summary>The value DXC gives <c>__SHADER_TARGET_STAGE</c> for a stage prefix (<c>vs</c> 1, <c>ps</c> 0, <c>cs</c> 5); -1 when unknown.</summary>
    internal static int StageNumber(string prefix) => prefix switch
    {
        "vs" => 1,
        "ps" => 0,
        "gs" => 2,
        "hs" => 3,
        "ds" => 4,
        "cs" => 5,
        "lib" => 6,
        "ms" => 13,
        "as" => 14,
        _ => -1,
    };

    internal static string Int(int v) => v.ToString(CultureInfo.InvariantCulture);
}
