using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using OneShader.Build.Tasks;
using OneShader.Diagnostics;
using Xunit;

namespace OneShader.Build.Tests;

public sealed class TaskTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "osh-task-" + Guid.NewGuid().ToString("N"));

    public TaskTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "Shaders"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Engine : IBuildEngine
    {
        public List<BuildErrorEventArgs> Errors { get; } = [];

        public List<BuildWarningEventArgs> Warnings { get; } = [];

        public bool ContinueOnError => false;

        public int LineNumberOfTaskNode => 0;

        public int ColumnNumberOfTaskNode => 0;

        public string ProjectFileOfTaskNode => "";

        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs) => true;

        public void LogCustomEvent(CustomBuildEventArgs e)
        {
        }

        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e);

        public void LogMessageEvent(BuildMessageEventArgs e)
        {
        }

        public void LogWarningEvent(BuildWarningEventArgs e) => Warnings.Add(e);
    }

    private string Write(string relative, string text)
    {
        string path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private (bool Ok, Engine Engine, CompileShaders Task) Run(string defines = "", string entryPoints = "")
    {
        var engine = new Engine();
        var item = new TaskItem(Path.Combine(_dir, "Shaders", "Sprite.hlsl"));
        if (defines.Length > 0)
        {
            item.SetMetadata("Defines", defines);
        }

        if (entryPoints.Length > 0)
        {
            item.SetMetadata("EntryPoints", entryPoints);
        }

        var task = new CompileShaders
        {
            BuildEngine = engine,
            Shaders = [item],
            OutputDirectory = "obj/oneshader",
            ProjectDirectory = _dir,
            DefaultNamespace = "Test.Shaders",
        };
        return (task.Execute(), engine, task);
    }

    private string OutputPath => Path.Combine(_dir, "obj", "oneshader", "Shaders", "Sprite.hlsl.oneshader");

    [Fact]
    public void CompilerErrorsAreLoggedOnTheHlslLine()
    {
        Write("Shaders/Sprite.hlsl", "float4 main() : SV_Target { return 0; }\n");
        var (ok, engine, task) = Run();
        Assert.False(ok);
        var e = Assert.Single(engine.Errors);
        Assert.Equal("OSH0001", e.Code);
        Assert.Equal(Path.Combine(_dir, "Shaders", "Sprite.hlsl"), e.File);
        Assert.Equal(1, e.LineNumber);
        Assert.Equal(1, e.ColumnNumber);
        Assert.Empty(task.GeneratedFiles);
        Assert.False(File.Exists(OutputPath));
    }

    [Fact]
    public void BadEntryPointMetadataIsReportedWithTheFix()
    {
        Write("Shaders/Sprite.hlsl", "\n");
        var (ok, engine, _) = Run(entryPoints: "VSMain:Geometry");
        Assert.False(ok);
        var e = Assert.Single(engine.Errors);
        Assert.Equal("OSH0901", e.Code);
        Assert.Contains("Vertex, Pixel or Compute", e.Message, StringComparison.Ordinal);
    }

    /// <summary>Writes the output file a successful compile of the current shader would have left.</summary>
    private void SeedOutput(string defines = "", params string[] includes)
    {
        var (reflection, blobs) = GoldenModels.Build("Sprite");
        var defineList = defines.Length == 0 ? [] : defines.Split(';').Select(d => new KeyValuePair<string, string?>(d, null)).ToList();
        string hash = CompileShaders.HashOptions("Shaders/Sprite.hlsl", "Test.Shaders", string.Empty, defineList, [], []);
        var deps = new SortedSet<string>(includes.Select(i => Path.Combine(_dir, i)), StringComparer.Ordinal);
        var warnings = new List<Diagnostic> { new(DiagnosticSeverity.Warning, "OSH9998", "stored warning", new SourceSpan("Shaders/Sprite.hlsl", 3, 7)) };
        string text = CompileShaders.Render(reflection, blobs, Path.Combine(_dir, "Shaders", "Sprite.hlsl"), "Shaders/Sprite.hlsl", "Test.Shaders", string.Empty, hash, deps, warnings, _dir);
        Directory.CreateDirectory(Path.GetDirectoryName(OutputPath)!);
        File.WriteAllText(OutputPath, text);
    }

    [Fact]
    public void UnchangedShaderIsNotCompiledAgainAndKeepsItsWarnings()
    {
        Write("Shaders/Sprite.hlsl", "#include \"common.hlsli\"\n");
        Write("Shaders/common.hlsli", "// shared\n");
        SeedOutput("", "Shaders/common.hlsli");
        var before = File.GetLastWriteTimeUtc(OutputPath);

        var (ok, engine, task) = Run();
        Assert.True(ok);
        Assert.Empty(engine.Errors); // the stub compiler would have failed had it run
        var w = Assert.Single(engine.Warnings);
        Assert.Equal("OSH9998", w.Code);
        Assert.Equal(3, w.LineNumber);
        Assert.Equal(7, w.ColumnNumber);
        Assert.Equal(Path.Combine(_dir, "Shaders", "Sprite.hlsl"), w.File);
        Assert.Equal(OutputPath, Assert.Single(task.GeneratedFiles).ItemSpec);
        Assert.Equal(before, File.GetLastWriteTimeUtc(OutputPath));
    }

    [Fact]
    public void ChangingTheShaderAnIncludeTheDefinesOrTheEntryPointsCompilesAgain()
    {
        Write("Shaders/Sprite.hlsl", "#include \"common.hlsli\"\n");
        Write("Shaders/common.hlsli", "// shared\n");

        // The shader itself.
        SeedOutput("", "Shaders/common.hlsli");
        Write("Shaders/Sprite.hlsl", "#include \"common.hlsli\"\nfloat x;\n");
        Assert.False(Run().Ok);
        Assert.False(File.Exists(OutputPath));

        // An included file.
        Write("Shaders/Sprite.hlsl", "#include \"common.hlsli\"\n");
        SeedOutput("", "Shaders/common.hlsli");
        Assert.True(Run().Ok);
        Write("Shaders/common.hlsli", "// changed\n");
        Assert.False(Run().Ok);

        // Options.
        SeedOutput("", "Shaders/common.hlsli");
        Assert.False(Run(defines: "FANCY").Ok);
        SeedOutput("", "Shaders/common.hlsli");
        Assert.False(Run(entryPoints: "VSMain:Vertex").Ok);
    }

    [Fact]
    public void OutputsOfRemovedShadersAreDeleted()
    {
        Write("Shaders/Sprite.hlsl", "\n");
        string stale = Write("obj/oneshader/Shaders/Gone.hlsl.oneshader", "oneshader-reflection 1\n");
        SeedOutput();
        Assert.True(Run().Ok);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(OutputPath));
    }

    [Fact]
    public void OutputIsIdenticalWhenWrittenTwice()
    {
        SeedOutput();
        byte[] a = File.ReadAllBytes(OutputPath);
        File.Delete(OutputPath);
        SeedOutput();
        Assert.Equal(a, File.ReadAllBytes(OutputPath));
        Assert.DoesNotContain(_dir, System.Text.Encoding.UTF8.GetString(a), StringComparison.Ordinal);
    }
}
