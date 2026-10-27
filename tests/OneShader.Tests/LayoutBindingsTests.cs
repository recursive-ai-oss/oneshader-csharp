using System;
using System.Collections.Generic;
using System.Linq;
using OneShader.Layouts;
using OneShader.Layouts.Metal;
using OneShader.Layouts.Vulkan;
using OneShader.Layouts.WebGpu;
using OneShader.Reflection;
using Xunit;

namespace OneShader.Tests;

/// <summary>Union leniency for texture dimensions and storage access, target-neutral bindings, and split vertex streams.</summary>
public sealed class LayoutBindingsTests
{
    private static readonly CompileTarget[] All = [CompileTarget.SpirV, CompileTarget.Msl, CompileTarget.Wgsl];

    private static EntryLayout Entry(string source, string file, string entry, bool spirvOnly = false)
    {
        var r = ShaderCompiler.Compile(source, file, new CompileOptions { Targets = spirvOnly ? [CompileTarget.SpirV] : All }, TestContext.Current.CancellationToken);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        Assert.True(EntryLayout.TryCreate(r.Reflection!, entry, out var layout, out var error), error?.Message);
        return layout!;
    }

    private const string Tex2D = """
        [[vk::binding(0, 0)]] Texture2D<float4> Albedo;
        [[vk::binding(1, 0)]] SamplerState Samp;
        [shader("pixel")] float4 PsFlat(float2 uv : TEXCOORD0) : SV_Target { return Albedo.Sample(Samp, uv); }
        """;

    private const string TexCube = """
        [[vk::binding(0, 0)]] TextureCube<float4> Env;
        [[vk::binding(1, 0)]] SamplerState Samp;
        [shader("pixel")] float4 PsSky(float3 d : TEXCOORD0) : SV_Target { return Env.Sample(Samp, d); }
        """;

    private static EntryLayout[] Textures() => [Entry(Tex2D, "flat.hlsl", "PsFlat"), Entry(TexCube, "sky.hlsl", "PsSky")];

    [Fact]
    public void UnionOverTextureAndCubeIsRefusedUnlessDimensionIsIgnored()
    {
        var entries = Textures();
        Assert.False(PipelineLayout.TryCreateUnion(entries, out _, out var error));
        Assert.Equal(LayoutErrorKind.ResourceConflict, error!.Kind);
        Assert.Contains("set 0 binding 0", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IgnoreTextureDimension", error.Message);

        Assert.True(PipelineLayout.TryCreateUnion(entries, new UnionOptions { IgnoreTextureDimension = true }, out var union, out error), error?.Message);
        var vk = union!.ToVulkan();
        Assert.Equal(DescriptorType.SampledImage, vk.SetLayouts[0].Bindings[0].DescriptorType);
        Assert.True(union.TryToMetal(out var metal, out error), error?.Message);
        Assert.Equal(ArgumentKind.Texture, metal!.ForStage(ShaderStage.Pixel)!.Arguments.Single(a => a.Binding == 0).Kind);
    }

    [Fact]
    public void IgnoringDimensionStillRequiresKindCountAndSampleType()
    {
        var options = new UnionOptions { IgnoreTextureDimension = true };
        var ints = Entry("""
            [[vk::binding(0, 0)]] Texture2D<uint4> Ids;
            [[vk::binding(1, 0)]] SamplerState Samp;
            [shader("pixel")] uint4 PsIds(float2 uv : TEXCOORD0) : SV_Target { return Ids.Load(int3(0, 0, 0)); }
            """, "ids.hlsl", "PsIds");
        Assert.False(PipelineLayout.TryCreateUnion([Textures()[0], ints], options, out _, out var error));
        Assert.Equal(LayoutErrorKind.ResourceConflict, error!.Kind);

        var buffer = Entry("""
            [[vk::binding(0, 0)]] StructuredBuffer<float4> Data;
            [shader("pixel")] float4 PsBuf(float2 uv : TEXCOORD0) : SV_Target { return Data[0]; }
            """, "buf.hlsl", "PsBuf", spirvOnly: true); // Metal-only numbering would make buffer 0 and texture 0 look like different slots
        Assert.False(PipelineLayout.TryCreateUnion([Textures()[0], buffer], options, out _, out error));
        Assert.Equal(LayoutErrorKind.ResourceConflict, error!.Kind);
    }

    [Fact]
    public void WebGpuNeedsAPickForMixedDimensionsAndChecksIt()
    {
        Assert.True(PipelineLayout.TryCreateUnion(Textures(), new UnionOptions { IgnoreTextureDimension = true }, out var union, out _));
        Assert.False(union!.TryToWebGpu(out _, out var error));
        Assert.Equal(LayoutErrorKind.Unsupported, error!.Kind);
        Assert.Contains("TextureDimensions", error.Message);
        Assert.Contains("Texture2D", error.Message);
        Assert.Contains("TextureCube", error.Message);

        foreach (var (pick, expected) in new[] { (TextureViewDimension.Cube, TextureViewDimension.Cube), (TextureViewDimension.D2, TextureViewDimension.D2) })
        {
            Assert.True(union.TryToWebGpu(out var web, out error, new WebGpuLayoutOptions { TextureDimensions = new Dictionary<(uint, uint), TextureViewDimension> { [(0, 0)] = pick } }), error?.Message);
            var tex = web!.Groups[0].Entries.Single(e => e.Kind == BindingKind.Texture);
            Assert.Equal(expected, tex.ViewDimension);
        }

        Assert.False(union.TryToWebGpu(out _, out error, new WebGpuLayoutOptions { TextureDimensions = new Dictionary<(uint, uint), TextureViewDimension> { [(0, 0)] = TextureViewDimension.D3 } }));
        Assert.Contains("matches none", error!.Message);

        // A slot all entry points agree on needs no pick.
        Assert.True(PipelineLayout.TryCreateUnion([Textures()[0], Entry(Tex2D, "flat2.hlsl", "PsFlat")], new UnionOptions { IgnoreTextureDimension = true }, out var same, out _));
        Assert.True(same!.TryToWebGpu(out _, out error), error?.Message);
    }

    private const string Reads = """
        [[vk::binding(0, 0)]] StructuredBuffer<float4> Items;
        [[vk::binding(1, 0)]] ByteAddressBuffer Raw;
        [shader("compute")] [numthreads(1, 1, 1)] void CsRead(uint3 id : SV_DispatchThreadID) { float4 v = Items[0]; uint r = Raw.Load(0); }
        """;

    private const string Writes = """
        [[vk::binding(0, 0)]] RWStructuredBuffer<float4> Items;
        [[vk::binding(1, 0)]] RWByteAddressBuffer Raw;
        [shader("compute")] [numthreads(1, 1, 1)] void CsWrite(uint3 id : SV_DispatchThreadID) { Items[0] = 1; Raw.Store(0, 1u); }
        """;

    [Fact]
    public void ReadOnlyAndReadWriteBuffersMergeToReadWriteOnlyOnRequest()
    {
        var entries = new[] { Entry(Reads, "r.hlsl", "CsRead"), Entry(Writes, "w.hlsl", "CsWrite") };
        Assert.False(PipelineLayout.TryCreateUnion(entries, out _, out var error));
        Assert.Contains("MergeStorageAccess", error!.Message);

        foreach (var order in new[] { entries, entries.Reverse().ToArray() })
        {
            Assert.True(PipelineLayout.TryCreateUnion(order, new UnionOptions { MergeStorageAccess = true }, out var union, out error), error?.Message);
            Assert.All(union!.ToBindings(), b => Assert.Equal(BindingType.StorageBuffer, b.Type));
            Assert.All(union.ToVulkan().SetLayouts[0].Bindings, b => Assert.Equal(DescriptorType.StorageBuffer, b.DescriptorType));
            Assert.Equal(ShaderStages.Compute, union.ToBindings()[0].Stages);

            // WebGPU's read-write binding does not fit the read-only shader.
            Assert.False(union.TryToWebGpu(out _, out error));
            Assert.Equal(LayoutErrorKind.Unsupported, error!.Kind);
            Assert.Contains("ReadOnlyStorageBuffers", error.Message);
            Assert.True(union.TryToWebGpu(out var web, out error, new WebGpuLayoutOptions { ReadOnlyStorageBuffers = ["Items", "Raw"] }), error?.Message);
            Assert.All(web!.Groups[0].Entries, e => Assert.Equal(BufferBindingType.ReadOnlyStorage, e.BufferType));
        }

        // A structured buffer and a byte-address buffer are different data even when merging is allowed.
        var raw = Entry("""
            [[vk::binding(0, 0)]] RWByteAddressBuffer Items;
            [shader("compute")] [numthreads(1, 1, 1)] void CsRaw(uint3 id : SV_DispatchThreadID) { Items.Store(0, 1u); }
            """, "raw.hlsl", "CsRaw");
        Assert.False(PipelineLayout.TryCreateUnion([entries[0], raw], new UnionOptions { MergeStorageAccess = true }, out _, out error));
        Assert.Equal(LayoutErrorKind.ResourceConflict, error!.Kind);
    }

    [Fact]
    public void ToBindingsDescribesAUnionWithoutAPIVocabulary()
    {
        var common = """
            [[vk::binding(0, 0)]] StructuredBuffer<float4> Items;
            [[vk::binding(1, 0)]] RWStructuredBuffer<uint> Counts;
            [[vk::binding(2, 0)]] Texture2D<float4> Tex;
            [[vk::binding(3, 0)]] SamplerComparisonState Shadow;
            [[vk::binding(4, 0)]] SamplerState Linear;
            [[vk::binding(5, 0)]] cbuffer Frame { float4 Tint; };
            [shader("compute")] [numthreads(1, 1, 1)] void CsA(uint3 id : SV_DispatchThreadID)
            {
                Counts[0] = asuint(Items[0].x) + asuint(Tex.SampleCmp(Shadow, float2(0, 0), 0.5)) + asuint(Tex.SampleLevel(Linear, float2(0, 0), 0).x) + asuint(Tint.x);
            }
            """;
        var cube = """
            [[vk::binding(2, 0)]] TextureCube<float4> Tex;
            [[vk::binding(4, 0)]] SamplerState Linear;
            [[vk::binding(1, 0)]] RWStructuredBuffer<uint> Counts;
            [shader("compute")] [numthreads(1, 1, 1)] void CsB(uint3 id : SV_DispatchThreadID) { Counts[0] = asuint(Tex.SampleLevel(Linear, float3(0, 0, 1), 0).x); }
            """;
        var readsCounts = """
            [[vk::binding(1, 0)]] StructuredBuffer<uint> Counts;
            [shader("compute")] [numthreads(1, 1, 1)] void CsC(uint3 id : SV_DispatchThreadID) { uint c = Counts[0]; }
            """;
        var entries = new[] { Entry(common, "a.hlsl", "CsA"), Entry(cube, "b.hlsl", "CsB"), Entry(readsCounts, "c.hlsl", "CsC") };
        Assert.True(PipelineLayout.TryCreateUnion(entries, new UnionOptions { IgnoreTextureDimension = true, MergeStorageAccess = true }, out var union, out var error), error?.Message);

        var b = union!.ToBindings(new BindingOptions { DepthTextures = ["Tex"], DynamicBuffers = ["Frame"], WidenSets = new Dictionary<uint, ShaderStages> { [0] = ShaderStages.Pixel } });
        Assert.Equal(6, b.Count);
        Assert.Equal([0u, 1u, 2u, 3u, 4u, 5u], b.Select(x => x.Binding).ToArray());

        Assert.Equal(BindingType.ReadOnlyStorageBuffer, b[0].Type);
        Assert.Equal("Items", b[0].Name);

        Assert.Equal(BindingType.StorageBuffer, b[1].Type);     // RW in two entry points, read-only in a third
        Assert.Equal(1u, b[1].Count);

        Assert.Equal(BindingType.SampledTexture, b[2].Type);
        Assert.Equal(TextureDimension.D2, b[2].Dimension);
        Assert.Equal([TextureDimension.D2, TextureDimension.Cube], b[2].Dimensions);
        Assert.Equal(TextureSampleKind.Depth, b[2].SampleKind);

        Assert.Equal(BindingType.Sampler, b[3].Type);
        Assert.True(b[3].IsComparison);
        Assert.Equal(BindingType.Sampler, b[4].Type);
        Assert.False(b[4].IsComparison);
        Assert.Equal(TextureDimension.None, b[4].Dimension);
        Assert.Equal(TextureSampleKind.None, b[4].SampleKind);

        Assert.Equal(BindingType.UniformBuffer, b[5].Type);
        Assert.True(b[5].IsDynamic);
        Assert.False(b[0].IsDynamic);
        Assert.All(b, x => Assert.Equal(ShaderStages.Compute | ShaderStages.Pixel, x.Stages));

        // Defaults: float texels, no dynamic offsets, stages as used.
        var plain = union.ToBindings();
        Assert.Equal(TextureSampleKind.Float, plain[2].SampleKind);
        Assert.All(plain, x => Assert.False(x.IsDynamic));
        Assert.All(plain, x => Assert.Equal(ShaderStages.Compute, x.Stages));
        Assert.Equal(plain.Select(x => x.MetalIndex).ToArray(), union.ToBindings().Select(x => x.MetalIndex).ToArray());
    }

    [Fact]
    public void ToBindingsReportsTextureSampleKindAndStorageFormat()
    {
        var e = Entry("""
            [[vk::binding(0, 0)]] Texture2D<int4> Ints;
            [[vk::binding(1, 0)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> Out;
            [shader("compute")] [numthreads(1, 1, 1)] void CsT(uint3 id : SV_DispatchThreadID) { Out[id.xy] = (float4)Ints.Load(int3(0, 0, 0)); }
            """, "t.hlsl", "CsT");
        var b = PipelineLayout.Create(e).ToBindings();
        Assert.Equal(TextureSampleKind.Sint, b[0].SampleKind);
        Assert.Equal(BindingType.StorageTexture, b[1].Type);
        Assert.Equal("rgba8", b[1].StorageFormat);
        Assert.Equal(TextureDimension.D2, b[1].Dimension);
    }

    // ---- vertex streams ----

    private const string InstancedVs = """
        struct VsIn
        {
            [[vk::location(0)]] float3 Pos : POSITION;
            [[vk::location(1)]] float2 Uv : TEXCOORD0;
            [[vk::location(2)]] float4 Row0 : INST0;
            [[vk::location(3)]] float4 Row1 : INST1;
            [[vk::location(4)]] float4 Tint : INST2;
        };
        [shader("vertex")] float4 VsInst(VsIn i) : SV_Position { return float4(i.Pos, 1) * i.Row0.x + i.Row1 + i.Tint + i.Uv.xyxy; }
        """;

    private static VertexLayout InstancedLayout() => Entry(InstancedVs, "inst.hlsl", "VsInst").Vertex!;

    [Fact]
    public void TwoStreamsSplitOffsetsAndReachAllThreeAPIs()
    {
        var layout = InstancedLayout();
        Assert.Equal(68u, layout.Stride);
        Assert.True(layout.TrySplit([new VertexStream(2, 64, VertexStepMode.Instance), new VertexStream(0, 32)], out var split, out var error), error?.Message);

        var s = split!.Streams;
        Assert.Equal(2, s.Count);
        Assert.Equal(0u, s[0].FirstLocation);
        Assert.Equal([(0u, 0u), (1u, 12u)], s[0].Attributes.Select(a => (a.Location, a.Offset)).ToArray());
        Assert.Equal([(2u, 0u), (3u, 16u), (4u, 32u)], s[1].Attributes.Select(a => (a.Location, a.Offset)).ToArray());

        var vk = split.ToVulkan(1);
        Assert.Equal([new VertexInputBindingInfo(1, 32, VertexInputRate.Vertex), new VertexInputBindingInfo(2, 64, VertexInputRate.Instance)], vk.Bindings.ToArray());
        Assert.Equal(new VertexInputAttributeInfo(3, 2, Format.R32G32B32A32Sfloat, 16), vk.Attributes.Single(a => a.Location == 3));
        Assert.Equal(new VertexInputAttributeInfo(1, 1, Format.R32G32Sfloat, 12), vk.Attributes.Single(a => a.Location == 1));

        var metal = split.ToMetal(out error);
        Assert.NotNull(metal);
        Assert.Equal([30u, 29u], metal!.Select(m => m.BufferIndex).ToArray());
        Assert.Equal([VertexStepFunction.PerVertex, VertexStepFunction.PerInstance], metal.Select(m => m.StepFunction).ToArray());
        Assert.Equal(new MetalVertexAttribute(4, MetalVertexFormat.Float4, 32, 29), metal[1].Attributes[2]);
        Assert.Equal([32u, 64u], metal.Select(m => m.Stride).ToArray());

        var web = split.ToWebGpu();
        Assert.Equal(["vertex", "instance"], web.Select(w => w.StepMode.ToWebGpuString()).ToArray());
        Assert.Equal(new VertexAttributeInfo("float32x4", 32, 4), web[1].Attributes[2]);
        Assert.Equal(64u, web[1].ArrayStride);

        Assert.Null(split.ToMetal(out error, 0));
        Assert.Equal(LayoutErrorKind.InvalidVertexInput, error!.Kind);
        Assert.NotNull(split.ToMetal(out _, 1));
    }

    [Fact]
    public void SplitAppliesAttributeOverridesRelativeToTheStream()
    {
        var layout = InstancedLayout();
        var overrides = new VertexLayoutOverrides
        {
            Attributes = new Dictionary<uint, VertexAttributeOverride>
            {
                [4] = new(VertexFormat.Unorm8x4, 48),      // tint stored as 4 bytes at the end of the instance record
                [1] = new(VertexFormat.Float16x2, 16),
            },
        };
        Assert.True(layout.TrySplit([new VertexStream(0, 24), new VertexStream(2, 52, VertexStepMode.Instance)], out var split, out var error, overrides), error?.Message);
        Assert.Equal(new VertexAttribute(1, VertexFormat.Float16x2, 16), split!.Streams[0].Attributes[1]);
        Assert.Equal(new VertexAttribute(4, VertexFormat.Unorm8x4, 48), split.Streams[1].Attributes[2]);
        Assert.Equal("unorm8x4", split.ToWebGpu()[1].Attributes[2].Format);
    }

    [Fact]
    public void InvalidSplitsAreErrorsWithLocations()
    {
        var layout = InstancedLayout();

        // Overlap: the override puts Uv (loc 1) on top of Pos.
        Assert.False(layout.TrySplit([new VertexStream(0, 32), new VertexStream(2, 64)], out _, out var error,
            new VertexLayoutOverrides { Attributes = new Dictionary<uint, VertexAttributeOverride> { [1] = new(null, 8) } }));
        Assert.Equal(LayoutErrorKind.InvalidVertexInput, error!.Kind);
        Assert.Contains("overlap", error.Message);
        Assert.Contains("0 and 1", error.Message);

        // Stride too small for the last attribute (tint ends at 48).
        Assert.False(layout.TrySplit([new VertexStream(0, 32), new VertexStream(2, 44)], out _, out error));
        Assert.Contains("Location 4", error!.Message);
        Assert.Contains("stride of 44", error.Message);

        // A location below the first stream.
        Assert.False(layout.TrySplit([new VertexStream(1, 32), new VertexStream(2, 64)], out _, out error));
        Assert.Contains("Location 0 is in no vertex stream", error!.Message);

        // Duplicate start, bad stride, no streams, per-layout stride override, unknown location.
        Assert.False(layout.TrySplit([new VertexStream(0, 32), new VertexStream(0, 64)], out _, out error));
        Assert.Contains("start at location 0", error!.Message);
        Assert.False(layout.TrySplit([new VertexStream(0, 30)], out _, out error));
        Assert.Contains("multiple of 4", error!.Message);
        Assert.False(layout.TrySplit([], out _, out error));
        Assert.False(layout.TrySplit([new VertexStream(0, 100)], out _, out error, new VertexLayoutOverrides { Stride = 100 }));
        Assert.Contains("Stride", error!.Message);
        Assert.False(layout.TrySplit([new VertexStream(0, 100)], out _, out error, new VertexLayoutOverrides { Attributes = new Dictionary<uint, VertexAttributeOverride> { [9] = new(VertexFormat.Float32, null) } }));
        Assert.Contains("location 9", error!.Message);

        // Format class mismatch is reported as for TryWithOverrides.
        Assert.False(layout.TrySplit([new VertexStream(0, 100)], out _, out error, new VertexLayoutOverrides { Attributes = new Dictionary<uint, VertexAttributeOverride> { [0] = new(VertexFormat.Uint32x3, null) } }));
        Assert.Contains("read as float", error!.Message);
    }

    [Fact]
    public void StreamTheEntryPointDoesNotReadStaysSoSlotsAreStable()
    {
        var layout = Entry("""
            struct VsIn { [[vk::location(0)]] float3 Pos : POSITION; };
            [shader("vertex")] float4 VsDepth(VsIn i) : SV_Position { return float4(i.Pos, 1); }
            """, "depth.hlsl", "VsDepth").Vertex!;
        Assert.True(layout.TrySplit([new VertexStream(0, 12), new VertexStream(2, 64, VertexStepMode.Instance)], out var split, out var error), error?.Message);
        Assert.Equal(2, split!.ToVulkan().Bindings.Count);
        Assert.Empty(split.Streams[1].Attributes);
        Assert.Empty(split.ToWebGpu()[1].Attributes);
    }
}
