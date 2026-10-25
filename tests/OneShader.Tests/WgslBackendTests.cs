using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OneShader.Diagnostics;
using Xunit;

namespace OneShader.Tests;

/// <summary>The WGSL target: every corpus entry point either validates in naga or gets a documented diagnostic.</summary>
public sealed partial class WgslBackendTests
{
    internal static IEnumerable<string> AllFiles() =>
        CorpusFiles.Graphics().Concat(CorpusFiles.Shaders("3d")).Concat(CorpusFiles.Shaders("compute")).Concat(CorpusFiles.Shaders("probes"));

    public static IEnumerable<object[]> CorpusFileNames() => AllFiles().Select(p => new object[] { p });

    /// <summary>Probe files name their entry point on the second line (<c>// probe: ENTRY ps|vs|cs</c>); other files mark theirs with <c>[shader(...)]</c>.</summary>
    internal static IReadOnlyList<EntryPointRequest> ProbeEntryPoints(string path)
    {
        var lines = File.ReadLines(path).Take(2).ToArray();
        if (lines.Length < 2 || !lines[1].StartsWith("// probe:", StringComparison.Ordinal))
        {
            return [];
        }

        string[] head = lines[1].Substring("// probe:".Length).Trim().Split(' ');
        return [new EntryPointRequest(head[0], head[1] switch { "vs" => ShaderStage.Vertex, "ps" => ShaderStage.Pixel, _ => ShaderStage.Compute })];
    }

    internal static CompileOptions OptionsFor(string path, bool optimize = true, params CompileTarget[] targets) =>
        new() { Targets = targets.Length == 0 ? [CompileTarget.Wgsl] : targets, IncludeDirectories = [Path.GetDirectoryName(path)!], Optimize = optimize, EntryPoints = ProbeEntryPoints(path) };

    internal static CompileResult CompileWgsl(string path, bool optimize = true) =>
        ShaderCompiler.Compile(File.ReadAllText(path), Path.GetFileName(path), OptionsFor(path, optimize), TestContext.Current.CancellationToken);

    internal static CompileResult CompileWgslText(string source, string name = "t.hlsl") =>
        ShaderCompiler.Compile(source, name, new CompileOptions { Targets = [CompileTarget.Wgsl] }, TestContext.Current.CancellationToken);

    /// <summary>Corpus files whose WGSL output is a deliberate diagnostic, with the diagnostic code and a reason. See docs/wgsl-backend.md.</summary>
    internal static readonly Dictionary<string, (string Code, string Reason)> Expected = new(StringComparer.Ordinal)
    {
        ["stage_io.hlsl"] = ("OSH5202", "SV_ClipDistance needs an optional WebGPU feature and CalculateLevelOfDetail has no WGSL equivalent"),
        ["clip_distance.hlsl"] = ("OSH5202", "SV_ClipDistance needs an optional WebGPU feature"),
        ["clip_distance_array.hlsl"] = ("OSH5202", "SV_ClipDistance needs an optional WebGPU feature"),
        ["cull_distance.hlsl"] = ("OSH5202", "SV_CullDistance needs an optional WebGPU feature"),
        ["lod_query.hlsl"] = ("OSH5202", "CalculateLevelOfDetail has no WGSL equivalent"),
    };

    public static IEnumerable<object[]> CorpusFilesAndModes() =>
        AllFiles().SelectMany(p => new[] { new object[] { p, true }, new object[] { p, false } });

    [Theory]
    [MemberData(nameof(CorpusFilesAndModes))]
    public void CorpusEntryPointsValidateInNagaOrAreDeliberateDiagnostics(string path, bool optimize)
    {
        string? naga = Oracles.Find("naga");
        Assert.SkipUnless(naga is not null, "naga is not installed");
        string name = Path.GetFileName(path);
        var r = CompileWgsl(path, optimize);
        if (!r.Success && r.Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error || !d.Code.StartsWith("OSH52", StringComparison.Ordinal)))
        {
            // The front end rejected the file (a probe of a front-end gap or error); no backend ran.
            return;
        }

        if (Expected.TryGetValue(name, out var expected))
        {
            Assert.False(r.Success, name + " is listed as unsupported but compiled; remove it from the list");
            Assert.Contains(r.Diagnostics, d => d.Code == expected.Code);
            return;
        }

        Assert.True(r.Success, name + ": " + string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        Assert.NotEmpty(r.Blobs);
        string dir = Path.Combine(Path.GetTempPath(), "osh-wgsl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var b in r.Blobs)
            {
                string file = Path.Combine(dir, b.EntryPoint + ".wgsl");
                File.WriteAllText(file, b.AsText());
                if (Environment.GetEnvironmentVariable("ONESHADER_WGSL_DUMP") is { Length: > 0 } dump)
                {
                    Directory.CreateDirectory(dump);
                    File.WriteAllText(Path.Combine(dump, Path.GetFileNameWithoutExtension(path) + "." + b.EntryPoint + ".wgsl"), b.AsText());
                }

                var (code, output) = Oracles.Run(naga!, [file], dir);
                Assert.True(code == 0, name + " " + b.EntryPoint + ":\n" + output + "\n" + b.AsText());
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StageIoWithoutTheUnsupportedPartsValidates(bool optimize)
    {
        string? naga = Oracles.Find("naga");
        Assert.SkipUnless(naga is not null, "naga is not installed");
        string path = Path.Combine(CorpusFiles.Root(), "3d", "stage_io.hlsl");
        string source = File.ReadAllText(path)
            .Replace("    float Clip[2] : SV_ClipDistance;\n", "")
            .Replace("    output.Clip[0] = 0.9 - uv.x;\n", "")
            .Replace("    output.Clip[1] = 0.95 - uv.y;\n", "")
            .Replace("Photo.CalculateLevelOfDetail(PhotoSampler, input.Uv * 3.0)", "input.Uv.x");
        var r = ShaderCompiler.Compile(source, "stage_io.hlsl", new CompileOptions { Targets = [CompileTarget.Wgsl], Optimize = optimize }, TestContext.Current.CancellationToken);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        ValidateWithNaga(naga!, r);
    }

    internal static void ValidateWithNaga(string naga, CompileResult r)
    {
        string dir = Path.Combine(Path.GetTempPath(), "osh-wgsl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var b in r.Blobs)
            {
                string file = Path.Combine(dir, b.EntryPoint + ".wgsl");
                File.WriteAllText(file, b.AsText());
                var (code, output) = Oracles.Run(naga, [file], dir);
                Assert.True(code == 0, b.EntryPoint + ":\n" + output + "\n" + b.AsText());
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
