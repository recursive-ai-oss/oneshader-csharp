using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OneShader.Diagnostics;
using Xunit;

namespace OneShader.Tests;

/// <summary>Shared helpers for tests that compile the whole corpus to MSL.</summary>
internal static class MslCorpus
{
    public static IEnumerable<string> AllShaders()
    {
        string root = CorpusFiles.Root();
        foreach (var sub in new[] { "2d", "2d/renderer", "3d", "compute", "lang", "probes" })
        {
            foreach (var f in Directory.GetFiles(Path.Combine(root, sub), "*.hlsl").OrderBy(p => p, StringComparer.Ordinal))
            {
                yield return f;
            }
        }
    }

    /// <summary>Compiles a corpus file for MSL. Probe files name their entry point on their second line (<c>// probe: ENTRY ps|vs|cs</c>).</summary>
    public static CompileResult Compile(string path, bool optimize = true)
    {
        var entries = new List<EntryPointRequest>();
        string text = File.ReadAllText(path);
        var lines = text.Split('\n');
        if (lines.Length > 1 && lines[1].StartsWith("// probe:", StringComparison.Ordinal))
        {
            var head = lines[1]["// probe:".Length..].Trim().Split(' ');
            var stage = head[1] switch { "vs" => ShaderStage.Vertex, "ps" => ShaderStage.Pixel, _ => ShaderStage.Compute };
            entries.Add(new EntryPointRequest(head[0], stage));
        }

        return ShaderCompiler.Compile(text, path, new CompileOptions { Targets = [CompileTarget.SpirV, CompileTarget.Msl], Optimize = optimize, EntryPoints = entries });
    }

    /// <summary>Directory the emitted MSL is written to when ONESHADER_MSL_OUT is set (for the Metal compiler check).</summary>
    public static string? DumpDirectory => Environment.GetEnvironmentVariable("ONESHADER_MSL_OUT") is { Length: > 0 } d ? d : null;

    /// <summary>Corpus files the MSL backend refuses on purpose, with the construct and the diagnostic it must report.</summary>
    public static IReadOnlyDictionary<string, (string Code, string Text)> Documented { get; } = new Dictionary<string, (string, string)>
    {
        ["cull_distance.hlsl"] = ("OSH5102", "cull distance"),
    };

    public static bool IsMslCode(Diagnostic d) => d.Code.StartsWith("OSH51", StringComparison.Ordinal);
}

public class MslBackendTests
{
    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void CorpusEmitsMslWithoutOptimization(string path)
    {
        // Unoptimized IR keeps every local in memory and every call, which exercises the parts of the backend the optimizer normally removes.
        var r = MslCorpus.Compile(path, optimize: false);
        string name = Path.GetFileNameWithoutExtension(path);
        var errors = r.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0 && !errors.Any(MslCorpus.IsMslCode))
        {
            Assert.Skip("front end: " + errors[0]);
        }

        if (MslCorpus.Documented.TryGetValue(Path.GetFileName(path), out _))
        {
            return;
        }

        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(d => d.ToString())));
        if (MslCorpus.DumpDirectory is { } dir)
        {
            dir = Path.Combine(dir, "O0");
            Directory.CreateDirectory(dir);
            foreach (var b in r.Blobs.Where(b => b.Target == CompileTarget.Msl))
            {
                File.WriteAllText(Path.Combine(dir, name + "." + b.EntryPoint + ".metal"), b.AsText());
            }
        }
    }

    public static IEnumerable<object[]> CorpusFiles() => MslCorpus.AllShaders().Select(p => new object[] { p });

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void CorpusEmitsMsl(string path)
    {
        var r = MslCorpus.Compile(path);
        string name = Path.GetFileNameWithoutExtension(path);
        var errors = r.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0 && !errors.Any(MslCorpus.IsMslCode))
        {
            // The front end rejects the file (a documented gap or a deliberate error probe); there is nothing for the backend to do.
            Assert.Skip("front end: " + errors[0]);
        }

        if (MslCorpus.Documented.TryGetValue(Path.GetFileName(path), out var expected))
        {
            Assert.Contains(errors, d => d.Code == expected.Code && d.Message.Contains(expected.Text, StringComparison.Ordinal));
            return;
        }

        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(d => d.ToString())));
        Assert.NotEmpty(r.Blobs);
        if (MslCorpus.DumpDirectory is { } dir)
        {
            Directory.CreateDirectory(dir);
            foreach (var b in r.Blobs.Where(b => b.Target == CompileTarget.Msl))
            {
                File.WriteAllText(Path.Combine(dir, name + "." + b.EntryPoint + ".metal"), b.AsText());
            }
        }
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void ResourceIndicesMatchReflectionAndOutputIsDeterministic(string path)
    {
        var r = MslCorpus.Compile(path);
        if (!r.Success || r.Reflection is null || MslCorpus.Documented.ContainsKey(Path.GetFileName(path)))
        {
            return;
        }

        var again = MslCorpus.Compile(path);
        foreach (var b in r.Blobs.Where(b => b.Target == CompileTarget.Msl))
        {
            Assert.Equal(b.AsText(), again.Find(b.EntryPoint, CompileTarget.Msl)!.AsText());
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(b.AsText(), @"(\w+) \[\[(buffer|texture|sampler)\((\d+)\)\]\]"))
            {
                var res = r.Reflection.Resources.FirstOrDefault(x => x.Name == m.Groups[1].Value);
                if (res is null)
                {
                    continue; // stage_in and similar
                }

                var slot = res.Slots.First(x => x.Target == CompileTarget.Msl);
                Assert.Equal(uint.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture), slot.Binding);
            }

            // No reserved double underscores, dots or angle brackets leak from IR names.
            string text = b.AsText().Replace("__builtin_offsetof", "", StringComparison.Ordinal);
            Assert.DoesNotContain("__", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DoublesAreRefusedWithADiagnostic()
    {
        var r = ShaderCompiler.Compile("[shader(\"compute\")][numthreads(1,1,1)] void CSMain() { double d = 1.0; }", "d.hlsl", new CompileOptions { Targets = [CompileTarget.Msl], Optimize = false }, TestContext.Current.CancellationToken);
        Assert.Contains(r.Diagnostics, d => d.Code == "OSH5102" && d.Message.Contains("64-bit", StringComparison.Ordinal));
    }

    [Fact]
    public void EntryNamesAvoidReservedWords()
    {
        Assert.Equal("main0", Msl.MslBackend.EntryFunctionName("main"));
        Assert.Equal("VSMain", Msl.MslBackend.EntryFunctionName("VSMain"));
    }
}
