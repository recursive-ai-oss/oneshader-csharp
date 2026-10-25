using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace OneShader.Build.Tests;

/// <summary>Runs the generator the way the C# compiler would and compiles what it produced.</summary>
internal static class Harness
{
    public static string TestsDirectory { get; } = Path.GetDirectoryName(ThisFile())!;

    public static string RepoRoot { get; } = Path.GetFullPath(Path.Combine(TestsDirectory, "..", ".."));

    private static string ThisFile([CallerFilePath] string path = "") => path;

    private sealed class MemoryText : AdditionalText
    {
        private readonly SourceText _text;

        public MemoryText(string path, string text)
        {
            Path = path;
            _text = SourceText.From(text, new UTF8Encoding(false));
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }

    /// <summary>The generated sources (hint name, text) and the generator's diagnostics for these input files.</summary>
    public static (List<(string Name, string Text)> Sources, ImmutableArray<Diagnostic> Diagnostics) Generate(params (string Path, string Text)[] files)
    {
        var generator = new OneShader.Build.Generator.ShaderBindingsGenerator();
        var texts = files.Select(f => (AdditionalText)new MemoryText(f.Path, f.Text)).ToImmutableArray();
        GeneratorDriver driver = CSharpGeneratorDriver.Create([generator.AsSourceGenerator()], texts);
        var compilation = CSharpCompilation.Create("probe", [], [], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);
        var result = driver.GetRunResult();
        var sources = new List<(string, string)>();
        foreach (var r in result.Results)
        {
            foreach (var s in r.GeneratedSources)
            {
                sources.Add((s.HintName, s.SourceText.ToString()));
            }
        }

        return (sources, diagnostics);
    }

    /// <summary>
    /// Resolves symbolic links in every directory of a path, so it compares equal to what MSBuild reports
    /// (macOS temp directories live behind a /var -> /private/var link).
    /// </summary>
    public static string RealPath(string path)
    {
        string full = Path.GetFullPath(path);
        string current = Path.GetPathRoot(full)!;
        foreach (string part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var target = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
            {
                current = target.FullName;
            }
        }

        return current;
    }

    public static string Golden(string file) => Path.Combine(TestsDirectory, "Golden", file);

    /// <summary>Compares text with a checked-in golden file; ONESHADER_UPDATE_GOLDEN=1 rewrites the file instead.</summary>
    public static void AssertGolden(string file, string actual)
    {
        string path = Golden(file);
        if (Environment.GetEnvironmentVariable("ONESHADER_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(path, actual, new UTF8Encoding(false));
            return;
        }

        Assert.True(File.Exists(path), "missing golden file " + file + "; run with ONESHADER_UPDATE_GOLDEN=1 to create it");
        Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal), actual);
    }

    /// <summary>
    /// Compiles sources into an in-memory assembly with nullable on and every warning an error, as a
    /// careful consumer would. Returns the assembly and its load context (unload it when done).
    /// </summary>
    public static Assembly Compile(IEnumerable<string> sources, out AssemblyLoadContext context)
    {
        var trees = sources.Select(s => CSharpSyntaxTree.ParseText(s, new CSharpParseOptions(LanguageVersion.Latest))).ToList();
        string tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        var refs = tpa.Split(Path.PathSeparator).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithNullableContextOptions(NullableContextOptions.Enable)
            .WithWarningLevel(9999)
            .WithGeneralDiagnosticOption(ReportDiagnostic.Error);
        var compilation = CSharpCompilation.Create("GeneratedProbe", trees, refs, options);
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString())));
        ms.Position = 0;
        context = new AssemblyLoadContext("generated", isCollectible: true);
        return context.LoadFromStream(ms);
    }
}
