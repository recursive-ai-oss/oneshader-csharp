using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;

namespace OneShader.Bench;

/// <summary>BenchmarkDotNet settings: in-process toolchain (the repository's artifacts layout does not suit BDN's generated projects), a short iteration time because one compile takes milliseconds.</summary>
public sealed class InProcessConfig : ManualConfig
{
    /// <summary>Creates the configuration.</summary>
    public InProcessConfig()
    {
        AddJob(Job.Default
            .WithToolchain(InProcessNoEmitToolchain.Instance)
            .WithWarmupCount(3)
            .WithIterationCount(12)
            .WithIterationTime(Perfolizer.Horology.TimeInterval.FromMilliseconds(150)));
        AddExporter(JsonExporter.Full);
        AddLogger(BenchmarkDotNet.Loggers.ConsoleLogger.Default);
        AddColumnProvider(BenchmarkDotNet.Columns.DefaultColumnProviders.Instance);
        // No console summary for 150 rows; the JSON is the output.
        Options |= ConfigOptions.DisableOptimizationsValidator;
    }
}

/// <summary>Shared code for the benchmark classes: one benchmark row = all entry points of one corpus file, compiled in one call.</summary>
public abstract class CompileBenchmark
{
    private string _source = "";
    private string _path = "";
    private CompileOptions _options = new();

    /// <summary>The corpus file (and defines) being compiled.</summary>
    [ParamsSource("Files")]
    public string Shader { get; set; } = "";

    /// <summary>The target this class measures.</summary>
    protected abstract CompileTarget Target { get; }

    /// <summary>Whether the optimizer runs.</summary>
    protected abstract bool Optimize { get; }

    /// <summary>Reads the file once and checks that it compiles; throws with the diagnostics when it does not.</summary>
    [GlobalSetup]
    public void Setup()
    {
        string corpus = Manifest.FindCorpus();
        var group = Manifest.Groups(Manifest.Load(corpus)).First(g => g.Name == Shader);
        _path = Path.Combine(corpus, group.File);
        _source = File.ReadAllText(_path);
        _options = Manifest.Options(group, corpus, Target, Optimize);
        var r = ShaderCompiler.Compile(_source, _path, _options);
        if (!r.Success || r.Blobs.Count != group.Units.Count)
        {
            throw new InvalidOperationException(Shader + " did not compile: " + string.Join("; ", r.Diagnostics));
        }
    }

    /// <summary>One compile of every entry point of the file.</summary>
    protected CompileResult Run() => ShaderCompiler.Compile(_source, _path, _options);

    /// <summary>Writes the parameter lists (see <see cref="Supported"/>) for every target without running anything.</summary>
    public static void WriteParams()
    {
        foreach (var t in new[] { CompileTarget.SpirV, CompileTarget.Msl, CompileTarget.Wgsl })
        {
            _ = Supported(t).Count();
        }
    }

    /// <summary>Names of the files that compile for <paramref name="target"/>; others are recorded in the results as unsupported.</summary>
    protected static IEnumerable<string> Supported(CompileTarget target)
    {
        string corpus = Manifest.FindCorpus();
        var unsupported = new List<string>();
        var supported = new List<string>();
        foreach (var group in Manifest.Groups(Manifest.Load(corpus)))
        {
            string path = Path.Combine(corpus, group.File);
            var r = ShaderCompiler.Compile(File.ReadAllText(path), path, Manifest.Options(group, corpus, target, true));
            if (r.Success && r.Blobs.Count == group.Units.Count)
            {
                supported.Add(group.Name);
            }
            else
            {
                string first = r.Diagnostics.Count > 0 ? r.Diagnostics[0].ToString() : "no blob";
                unsupported.Add(group.Name + "\t" + first.Replace(corpus + Path.DirectorySeparatorChar, "", StringComparison.Ordinal).ReplaceLineEndings(" "));
            }
        }

        string? dir = Environment.GetEnvironmentVariable("ONESHADER_BENCH_OUT");
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, "unsupported-" + target + ".tsv"), unsupported);
            // BenchmarkDotNet shortens long parameter values in its reports; the report script maps rows back to names through this list (same order).
            File.WriteAllLines(Path.Combine(dir, "params-" + target + ".tsv"), supported);
        }

        return supported;
    }
}

/// <summary>Default compile: SPIR-V with the optimizer on.</summary>
[MemoryDiagnoser]
[Config(typeof(InProcessConfig))]
public class SpirvOptimized : CompileBenchmark
{
    /// <summary>Files to run.</summary>
    public static IEnumerable<string> Files() => Supported(CompileTarget.SpirV);

    /// <inheritdoc />
    protected override CompileTarget Target => CompileTarget.SpirV;

    /// <inheritdoc />
    protected override bool Optimize => true;

    /// <summary>Compiles the file.</summary>
    [Benchmark]
    public CompileResult Compile() => Run();
}

/// <summary>SPIR-V with the optimizer off.</summary>
[MemoryDiagnoser]
[Config(typeof(InProcessConfig))]
public class SpirvUnoptimized : CompileBenchmark
{
    /// <summary>Files to run.</summary>
    public static IEnumerable<string> Files() => Supported(CompileTarget.SpirV);

    /// <inheritdoc />
    protected override CompileTarget Target => CompileTarget.SpirV;

    /// <inheritdoc />
    protected override bool Optimize => false;

    /// <summary>Compiles the file.</summary>
    [Benchmark]
    public CompileResult Compile() => Run();
}

/// <summary>Metal Shading Language, optimizer on.</summary>
[MemoryDiagnoser]
[Config(typeof(InProcessConfig))]
public class MslOptimized : CompileBenchmark
{
    /// <summary>Files to run.</summary>
    public static IEnumerable<string> Files() => Supported(CompileTarget.Msl);

    /// <inheritdoc />
    protected override CompileTarget Target => CompileTarget.Msl;

    /// <inheritdoc />
    protected override bool Optimize => true;

    /// <summary>Compiles the file.</summary>
    [Benchmark]
    public CompileResult Compile() => Run();
}

/// <summary>WGSL, optimizer on.</summary>
[MemoryDiagnoser]
[Config(typeof(InProcessConfig))]
public class WgslOptimized : CompileBenchmark
{
    /// <summary>Files to run.</summary>
    public static IEnumerable<string> Files() => Supported(CompileTarget.Wgsl);

    /// <inheritdoc />
    protected override CompileTarget Target => CompileTarget.Wgsl;

    /// <inheritdoc />
    protected override bool Optimize => true;

    /// <summary>Compiles the file.</summary>
    [Benchmark]
    public CompileResult Compile() => Run();
}
