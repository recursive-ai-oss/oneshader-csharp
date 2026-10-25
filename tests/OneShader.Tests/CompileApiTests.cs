using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;

namespace OneShader.Tests;

/// <summary>The public <see cref="ShaderCompiler.Compile"/> path: HLSL text in, SPIR-V blobs and reflection out.</summary>
public class CompileApiTests
{
    public static IEnumerable<object[]> GraphicsFiles() => CorpusFiles.Graphics().Select(p => new object[] { p });

    private static CompileResult CompileFile(string path) =>
        ShaderCompiler.Compile(File.ReadAllText(path), Path.GetFileName(path), new CompileOptions { IncludeDirectories = [Path.GetDirectoryName(path)!] }, TestContext.Current.CancellationToken);

    [Theory]
    [MemberData(nameof(GraphicsFiles))]
    public void CorpusCompilesToValidDeterministicSpirv(string path)
    {
        string? val = Oracles.Find("spirv-val");
        Assert.SkipUnless(val is not null, "spirv-val is not installed");
        var r = CompileFile(path);
        Assert.Empty(r.Diagnostics);
        Assert.NotNull(r.Reflection);
        Assert.Equal(r.Reflection!.EntryPoints.Count, r.Blobs.Count);

        var again = CompileFile(path);
        Assert.Equal(r.Blobs.Count, again.Blobs.Count);
        string dir = Path.Combine(Path.GetTempPath(), "osh-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            for (int i = 0; i < r.Blobs.Count; i++)
            {
                Assert.Equal(CompileTarget.SpirV, r.Blobs[i].Target);
                Assert.Equal(r.Blobs[i].ToArray(), again.Blobs[i].ToArray());
                string file = Path.Combine(dir, r.Blobs[i].EntryPoint + ".spv");
                File.WriteAllBytes(file, r.Blobs[i].ToArray());
                var (code, output) = Oracles.Run(val!, ["--target-env", "vulkan1.2", file], dir);
                Assert.True(code == 0, Path.GetFileName(path) + " " + r.Blobs[i].EntryPoint + ": " + output);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ErrorsComeBackAsDiagnosticsOnTheRightLineAndColumn()
    {
        var r = ShaderCompiler.Compile("float4 f()\n{\n    return missing;\n}\n", "bad.hlsl", null, TestContext.Current.CancellationToken);
        Assert.False(r.Success);
        Assert.Empty(r.Blobs);
        Assert.Null(r.Reflection);
        var d = Assert.Single(r.Diagnostics);
        Assert.Equal("OSH0300", d.Code);
        Assert.Equal(("bad.hlsl", 3, 12), (d.Span.Path, d.Span.Line, d.Span.Column));
    }

    [Fact]
    public void UnavailableTargetsAreErrors()
    {
        var r = ShaderCompiler.Compile(
            "[shader(\"compute\")][numthreads(1,1,1)] void main() {}\n",
            "t.hlsl",
            new CompileOptions { Targets = [CompileTarget.SpirV, CompileTarget.Msl] },
            TestContext.Current.CancellationToken);
        Assert.False(r.Success);
        Assert.Contains(r.Diagnostics, d => d.Code == ApiDiagnosticCodes.TargetNotAvailable);
    }

    [Fact]
    public void WgslBlobIsTextAndDeterministic()
    {
        const string src = "[shader(\"compute\")][numthreads(4,1,1)] void main() {}\n";
        var opts = new CompileOptions { Targets = [CompileTarget.SpirV, CompileTarget.Wgsl] };
        var a = ShaderCompiler.Compile(src, "t.hlsl", opts, TestContext.Current.CancellationToken);
        var b = ShaderCompiler.Compile(src, "t.hlsl", opts, TestContext.Current.CancellationToken);
        Assert.True(a.Success, string.Join("\n", a.Diagnostics));
        var w = a.Find("main", CompileTarget.Wgsl);
        Assert.NotNull(w);
        Assert.Contains("@compute @workgroup_size(4, 1, 1)", w!.AsText());
        Assert.Equal(w.ToArray(), b.Find("main", CompileTarget.Wgsl)!.ToArray());
    }

    [Fact]
    public void CancellationIsHonoured()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => ShaderCompiler.Compile("float4 f() { return 0; }", "t.hlsl", null, cts.Token));
    }
}
