using System.Collections.Generic;
using System.Linq;
using OneShader.Diagnostics;
using OneShader.Lexing;
using OneShader.Preprocessing;

namespace OneShader.Tests;

internal static class Pp
{
    /// <summary>Preprocesses <paramref name="text"/> as a.hlsl with the given in-memory files.</summary>
    public static (PreprocessResult Result, DiagnosticBag Diagnostics) Run(
        string text,
        Dictionary<string, string>? files = null,
        PreprocessorOptions? options = null,
        string path = "a.hlsl",
        string[]? includeDirs = null)
    {
        files ??= [];
        options ??= new PreprocessorOptions();
        options.IncludeResolver ??= new InMemoryIncludeResolver(files, includeDirs);
        var bag = new DiagnosticBag();
        PreprocessResult r = Preprocessor.Preprocess(new SourceFile(path, text), options, bag);
        return (r, bag);
    }

    public static string[] Texts(PreprocessResult r) =>
        r.Tokens.Where(t => t.Kind != TokenKind.EndOfFile).Select(t => t.Text).ToArray();

    public static string[] Errors(DiagnosticBag bag) =>
        bag.Items.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToArray();

    public static string Joined(PreprocessResult r) => string.Join(' ', Texts(r));
}
