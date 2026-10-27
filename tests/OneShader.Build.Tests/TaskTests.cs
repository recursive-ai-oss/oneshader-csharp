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

    /// <summary>A small valid shader with one vertex entry point.</summary>
    private const string Valid = "[shader(\"vertex\")]\nfloat4 VSMain() : SV_Position { return float4(0, 0, 0, 1); }\n";

    [Fact]
    public void CompilerErrorsAreLoggedOnTheHlslLine()
    {
        // `missing` starts at line 3, column 12.
        Write("Shaders/Sprite.hlsl", "[shader(\"vertex\")]\nfloat4 VSMain() : SV_Position {\n    return missing;\n}\n");
        var (ok, engine, task) = Run();
        Assert.False(ok);
        var e = Assert.Single(engine.Errors);
        Assert.Equal("OSH0300", e.Code);
        Assert.Equal(Path.Combine(_dir, "Shaders", "Sprite.hlsl"), e.File);
        Assert.Equal(3, e.LineNumber);
        Assert.Equal(12, e.ColumnNumber);
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

    [Fact]
    public void BadDebugInfoValueIsReportedWithTheFix()
    {
        Write("Shaders/Sprite.hlsl", Valid);
        var engine = new Engine();
        var item = new TaskItem(Path.Combine(_dir, "Shaders", "Sprite.hlsl"));
        var task = new CompileShaders { BuildEngine = engine, Shaders = [item], OutputDirectory = "obj/oneshader", ProjectDirectory = _dir, DefaultNamespace = "Test.Shaders", DefaultDebugInfo = "yes" };
        Assert.False(task.Execute());
        var e = Assert.Single(engine.Errors);
        Assert.Equal("OSH0901", e.Code);
        Assert.Contains("Use true, full, lines or false", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TurningDebugInfoOnOrOffRecompilesTheShader()
    {
        Write("Shaders/Sprite.hlsl", Valid);
        var plain = new Engine();
        var item = new TaskItem(Path.Combine(_dir, "Shaders", "Sprite.hlsl"));
        CompileShaders Make(string debug, Engine e) => new() { BuildEngine = e, Shaders = [item], OutputDirectory = "obj/oneshader", ProjectDirectory = _dir, DefaultNamespace = "Test.Shaders", DefaultDebugInfo = debug };
        Assert.True(Make("", plain).Execute());
        byte[] before = File.ReadAllBytes(OutputPath);
        Assert.True(Make("true", new Engine()).Execute());
        byte[] debug = File.ReadAllBytes(OutputPath);
        Assert.NotEqual(before, debug);
        Assert.True(Make("", new Engine()).Execute());
        Assert.Equal(before, File.ReadAllBytes(OutputPath));
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
        Assert.Empty(engine.Errors); // had the shader been compiled, the include-only file would also have warned that it has no entry point
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
        // Each step seeds an output that looks current, changes one input, and expects a real compile
        // to replace it: the seeded text describes the golden Sprite model, a real compile of Valid does not.
        Write("Shaders/common.hlsli", "// shared\n");

        void ExpectCompiled(Func<(bool Ok, Engine Engine, CompileShaders Task)> run)
        {
            string seeded = File.ReadAllText(OutputPath);
            var (ok, engine, _) = run();
            Assert.True(ok, string.Join("; ", engine.Errors.Select(e => e.Message)));
            Assert.NotEqual(seeded, File.ReadAllText(OutputPath));
            Assert.Contains("VSMain", File.ReadAllText(OutputPath), StringComparison.Ordinal);
        }

        // The shader itself.
        Write("Shaders/Sprite.hlsl", "#include \"common.hlsli\"\n");
        SeedOutput("", "Shaders/common.hlsli");
        Write("Shaders/Sprite.hlsl", "#include \"common.hlsli\"\n" + Valid);
        ExpectCompiled(() => Run());

        // An included file.
        Write("Shaders/Sprite.hlsl", "#include \"common.hlsli\"\n" + Valid);
        SeedOutput("", "Shaders/common.hlsli");
        string beforeInclude = File.ReadAllText(OutputPath);
        Assert.True(Run().Ok);
        Assert.Equal(beforeInclude, File.ReadAllText(OutputPath)); // nothing changed: kept as is
        Write("Shaders/common.hlsli", "// changed\n");
        ExpectCompiled(() => Run());

        // Options.
        SeedOutput("", "Shaders/common.hlsli");
        ExpectCompiled(() => Run(defines: "FANCY"));
        SeedOutput("", "Shaders/common.hlsli");
        ExpectCompiled(() => Run(entryPoints: "VSMain:Vertex"));
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
