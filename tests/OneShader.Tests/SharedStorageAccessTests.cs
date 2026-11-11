using System;
using System.IO;
using System.Linq;
using OneShader.Layouts;
using OneShader.Layouts.WebGpu;
using OneShader.Reflection;
using Xunit;

namespace OneShader.Tests;

/// <summary>
/// A buffer's WGSL access mode follows its HLSL type in every entry point, and the full-subgroups requirement is reflected for exactly
/// the cooperative-matrix entry points.
/// </summary>
public sealed class SharedStorageAccessTests
{
    private static CompileResult CompileCorpus(string rel, params CompileTarget[] targets) =>
        ShaderCompiler.Compile(
            File.ReadAllText(Path.Combine(CorpusFiles.Root(), rel)),
            Path.GetFileName(rel),
            new CompileOptions { Targets = targets, Enable16BitTypes = true },
            TestContext.Current.CancellationToken);

    [Fact]
    public void ReadWriteBuffersAreReadWriteInEveryEntryPoint()
    {
        var r = CompileCorpus("compute/shared_rw_entries.hlsl", CompileTarget.Wgsl);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        Assert.Equal(2, r.Blobs.Count);
        foreach (var blob in r.Blobs)
        {
            string text = blob.AsText();
            Assert.Contains("@group(0) @binding(0) var<storage, read_write> Data:", text, StringComparison.Ordinal);
            Assert.Contains("@group(0) @binding(1) var<storage, read_write> Totals:", text, StringComparison.Ordinal);
            Assert.DoesNotContain("var<storage, read>", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReadOnlyHlslTypesStayRead()
    {
        var r = ShaderCompiler.Compile(
            "StructuredBuffer<uint> In;\nRWStructuredBuffer<uint> Out;\n[shader(\"compute\")] [numthreads(8,1,1)]\nvoid CSMain(uint3 i : SV_DispatchThreadID) { Out[i.x] = In[i.x]; }\n",
            "t.hlsl",
            new CompileOptions { Targets = [CompileTarget.Wgsl] },
            TestContext.Current.CancellationToken);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        string text = r.Blobs[0].AsText();
        Assert.Contains("var<storage, read> In:", text, StringComparison.Ordinal);
        Assert.Contains("var<storage, read_write> Out:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WebGpuLayoutIsTheSameForBothEntryPoints()
    {
        var r = CompileCorpus("compute/shared_rw_entries.hlsl", CompileTarget.SpirV, CompileTarget.Wgsl);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        WebGpuPipelineLayout[] layouts = new WebGpuPipelineLayout[2];
        string[] names = ["Scale", "Sum"];
        for (int i = 0; i < 2; i++)
        {
            Assert.True(EntryLayout.TryCreate(r.Reflection!, names[i], out var entry, out var e1), e1?.Message);
            Assert.True(PipelineLayout.Create(entry!).TryToWebGpu(out var web, out var e2), e2?.Message);
            layouts[i] = web!;
        }

        var a = layouts[0].Groups.SelectMany(g => g.Entries).ToArray();
        var b = layouts[1].Groups.SelectMany(g => g.Entries).ToArray();
        Assert.Equal(2, a.Length);
        Assert.Equal(a, b);
        Assert.All(a, x => Assert.Equal(BufferBindingType.Storage, x.BufferType));
    }

    [Theory]
    [InlineData("coop/coop_f16.hlsl")]
    [InlineData("coop/coop_bf16.hlsl")]
    [InlineData("coop/coop_layouts.hlsl")]
    public void CooperativeMatrixEntryPointsRequireFullSubgroups(string rel)
    {
        var r = CompileCorpus(rel, CompileTarget.SpirV);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        Assert.NotEmpty(r.Reflection!.EntryPoints);
        foreach (var e in r.Reflection.EntryPoints)
        {
            Assert.True(e.RequiredFeatures.HasFlag(DeviceFeatures.CooperativeMatrix));
            Assert.True(e.RequiredFeatures.HasFlag(DeviceFeatures.FullSubgroups));
        }

        Assert.True(ReflectionFormat.TryRead(ReflectionFormat.Write(r.Reflection), out var back, out string? err), err);
        Assert.True(back!.EntryPoints[0].RequiredFeatures.HasFlag(DeviceFeatures.FullSubgroups));
        string name = r.Reflection.EntryPoints[0].Name;
        Assert.True(EntryLayout.TryCreate(r.Reflection, name, out var entry, out var error), error?.Message);
        Assert.True(entry!.RequiredFeatures.HasFlag(DeviceFeatures.FullSubgroups));
        Assert.True(PipelineLayout.Create(entry).ToVulkan().RequiredFeatures.HasFlag(DeviceFeatures.FullSubgroups));
    }

    [Theory]
    [InlineData("compute/prefix_sum.hlsl")]
    [InlineData("compute/wave_reduce.hlsl")]
    [InlineData("compute/shared_rw_entries.hlsl")]
    public void OtherEntryPointsDoNot(string rel)
    {
        var r = CompileCorpus(rel, CompileTarget.SpirV);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        Assert.All(r.Reflection!.EntryPoints, e => Assert.False(e.RequiredFeatures.HasFlag(DeviceFeatures.FullSubgroups)));
    }
}
