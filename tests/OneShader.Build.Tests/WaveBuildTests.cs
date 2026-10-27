using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using OneShader.Build.Tasks;
using OneShader.Reflection;
using Xunit;

namespace OneShader.Build.Tests;

/// <summary>A shader that uses wave operations: the generated entry class and layout say which subgroup operations it needs.</summary>
public sealed class WaveBuildTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "osh-wave-" + Guid.NewGuid().ToString("N"));

    public WaveBuildTests() => Directory.CreateDirectory(Path.Combine(_dir, "Shaders"));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Engine : IBuildEngine
    {
        public List<BuildErrorEventArgs> Errors { get; } = [];

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

        public void LogWarningEvent(BuildWarningEventArgs e)
        {
        }
    }

    [Fact]
    public void GeneratedEntryClassAndLayoutCarryTheSubgroupOperations()
    {
        string path = Path.Combine(_dir, "Shaders", "WaveBallot.hlsl");
        File.WriteAllText(path, File.ReadAllText(Path.Combine(Harness.RepoRoot, "corpus", "compute", "wave_ballot.hlsl")));
        var engine = new Engine();
        var task = new CompileShaders
        {
            BuildEngine = engine,
            Shaders = [new TaskItem(path)],
            OutputDirectory = "obj/oneshader",
            ProjectDirectory = _dir,
            DefaultNamespace = "T.Shaders",
            DefaultTargets = "SpirV;Msl;Wgsl",
        };
        Assert.True(task.Execute(), string.Join("; ", engine.Errors.Select(e => e.Message)));
        string file = Path.Combine(_dir, "obj/oneshader", "Shaders", "WaveBallot.hlsl.oneshader");
        var (sources, diagnostics) = Harness.Generate((file, File.ReadAllText(file)));
        Assert.Empty(diagnostics);
        string generated = Assert.Single(sources).Text;
        Assert.Contains("SubgroupOperations = (global::OneShader.Reflection.SubgroupOperations)", generated, StringComparison.Ordinal);

        var asm = Harness.Compile([generated], out _);
        var entry = asm.GetType("T.Shaders.WaveBallot")!.GetNestedType("CSMain")!;
        var constant = (SubgroupOperations)entry.GetField("SubgroupOperations")!.GetRawConstantValue()!;
        Assert.Equal(
            SubgroupOperations.Basic | SubgroupOperations.Vote | SubgroupOperations.Ballot | SubgroupOperations.Shuffle,
            constant);
        var layout = (OneShader.Layouts.EntryLayout)entry.GetProperty("Layout")!.GetValue(null)!;
        Assert.Equal(constant, layout.SubgroupOperations);
    }
}
