using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OneShader.Layouts;
using OneShader.Layouts.Metal;
using OneShader.Layouts.Vulkan;
using OneShader.Layouts.WebGpu;
using OneShader.Reflection;
using Xunit;

namespace OneShader.Tests;

/// <summary>
/// Pipeline layouts shared by many entry points, widened stage visibility, vertex attribute overrides and dynamic-offset buffers.
/// </summary>
public sealed class LayoutUnionTests
{
    private static readonly CompileTarget[] All = [CompileTarget.SpirV, CompileTarget.Msl, CompileTarget.Wgsl];

    private static ShaderReflection ReflectSource(string source, string name, params CompileTarget[] targets)
    {
        var r = ShaderCompiler.Compile(source, name, new CompileOptions { Targets = targets.Length == 0 ? All : targets }, TestContext.Current.CancellationToken);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        return r.Reflection!;
    }

    private static ShaderReflection Reflect(string rel, params CompileTarget[] targets)
    {
        string path = Path.Combine(CorpusFiles.Root(), rel);
        var r = ShaderCompiler.Compile(
            File.ReadAllText(path),
            Path.GetFileName(rel),
            new CompileOptions { Targets = targets.Length == 0 ? All : targets, IncludeDirectories = [Path.GetDirectoryName(path)!] },
            TestContext.Current.CancellationToken);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        return r.Reflection!;
    }

    private static EntryLayout Entry(ShaderReflection reflection, string name)
    {
        Assert.True(EntryLayout.TryCreate(reflection, name, out var layout, out var error), error?.Message);
        return layout!;
    }

    private static PipelineLayout Union(params EntryLayout[] entries)
    {
        Assert.True(PipelineLayout.TryCreateUnion(entries, out var layout, out var error), error?.Message);
        return layout!;
    }

    private static EntryLayout[] AllEntries(ShaderReflection r) => r.EntryPoints.Select(e => Entry(r, e.Name)).ToArray();

    // ---- union ----

    [Fact]
    public void UnionOfCorpusFilesWithSeveralEntriesPerStageMergesSlotsAndStages()
    {
        var quad = Reflect("2d/renderer/quad.hlsl", CompileTarget.SpirV);
        var tile = Reflect("2d/renderer/tile.hlsl", CompileTarget.SpirV);
        var entries = AllEntries(quad).Concat(AllEntries(tile)).ToArray();
        Assert.True(entries.Count(e => e.Stage == ShaderStage.Pixel) >= 5);
        Assert.True(entries.Count(e => e.Stage == ShaderStage.Vertex) >= 2);

        // TryCreate refuses several entry points per stage; the union takes them.
        Assert.False(PipelineLayout.TryCreate(entries, out _, out _));
        var union = Union(entries);
        Assert.Equal(entries.Length, union.Entries.Count);

        // Every resource of every entry point is in its slot, visible to that entry point's stage.
        foreach (var e in entries)
        {
            foreach (var r in e.Resources)
            {
                var b = union.Groups[(int)r.Set].Entries.Single(x => x.Resource.Binding == r.Binding).Resource;
                Assert.Equal(r.Kind, b.Kind);
                Assert.Equal(e.StageFlag, b.Stages & e.StageFlag);
            }
        }

        // The sprite texture is read by pixel entry points only; the frame constants by the vertex ones.
        var vk = union.ToVulkan();
        var tex = vk.SetLayouts[1].Bindings.Single(b => b.Binding == 0);
        Assert.Equal(ShaderStageFlags.Fragment, tex.StageFlags);
        var frame = vk.SetLayouts[0].Bindings.Single(b => b.Binding == 0);
        Assert.Equal(ShaderStageFlags.Vertex, frame.StageFlags & ShaderStageFlags.Vertex);
    }

    [Fact]
    public void UnionOfEntriesFromOneFileWorksOnEveryTarget()
    {
        var quad = Reflect("2d/renderer/quad.hlsl");
        var entries = AllEntries(quad);
        Assert.Equal(3, entries.Count(e => e.Stage == ShaderStage.Pixel));
        var union = Union(entries);

        Assert.True(union.TryToMetal(out var metal, out var metalError), metalError?.Message);
        Assert.NotNull(metal!.ForStage(ShaderStage.Pixel));
        var psArgs = metal.ForStage(ShaderStage.Pixel)!.Arguments;
        foreach (var e in entries.Where(x => x.Stage == ShaderStage.Pixel))
        {
            foreach (var r in e.Resources)
            {
                Assert.Contains(psArgs, a => a.Set == r.Set && a.Binding == r.Binding && a.Index == r.MetalIndex);
            }
        }

        Assert.True(union.TryToWebGpu(out var web, out var webError), webError?.Message);
        Assert.Equal(union.Groups.Count, web!.Groups.Count);
        Assert.Equal(ShaderStageVisibility.Fragment, web.Groups[1].Entries.Single(x => x.Binding == 0).Visibility);
    }

    [Fact]
    public void UnionAcrossFilesWithDifferentMetalIndicesIsRefusedOnlyForMetal()
    {
        // Metal numbers resources per file, so entry points of two files that share a stage can disagree; Vulkan and WebGPU do not care.
        var a = ReflectSource("""
            [[vk::binding(0, 0)]] cbuffer C { float4 x; };
            [[vk::binding(1, 0)]] Texture2D T;
            [[vk::binding(2, 0)]] SamplerState S;
            [shader("pixel")] float4 PsA(float4 p : SV_Position) : SV_Target { return x + T.Sample(S, p.xy); }
            """, "a.hlsl");
        var b = ReflectSource("""
            [[vk::binding(5, 0)]] cbuffer D { float4 y; };
            [[vk::binding(1, 0)]] Texture2D T;
            [[vk::binding(2, 0)]] SamplerState S;
            [[vk::binding(0, 0)]] cbuffer C { float4 x; };
            [shader("pixel")] float4 PsB(float4 p : SV_Position) : SV_Target { return y * x * T.Sample(S, p.xy); }
            """, "b.hlsl");
        var union = Union(Entry(a, "PsA"), Entry(b, "PsB"));
        Assert.Single(union.ToVulkan().SetLayouts);
        Assert.True(union.TryToWebGpu(out _, out var webError), webError?.Message);
        Assert.False(union.TryToMetal(out var metal, out var error));
        Assert.Null(metal);
        Assert.Equal(LayoutErrorKind.ResourceConflict, error!.Kind);
        Assert.Contains("Set 0 binding 0", error.Message);
        Assert.Contains("'PsA'", error.Message);
        Assert.Contains("'PsB'", error.Message);
        Assert.Contains("Metal index", error.Message);
    }

    [Fact]
    public void ConflictingSlotNamesBothEntryPointsAndTheSlot()
    {
        var a = ReflectSource("""
            [[vk::binding(3, 1)]] Texture2D Albedo;
            [[vk::binding(4, 1)]] SamplerState S;
            [shader("pixel")] float4 PsTex(float4 p : SV_Position) : SV_Target { return Albedo.Sample(S, p.xy); }
            """, "a.hlsl", CompileTarget.SpirV);
        var b = ReflectSource("""
            [[vk::binding(3, 1)]] cbuffer Params { float4 tint; };
            [shader("pixel")] float4 PsTint(float4 p : SV_Position) : SV_Target { return tint; }
            """, "b.hlsl", CompileTarget.SpirV);
        var c = ReflectSource("""
            [shader("compute")] [numthreads(1, 1, 1)] void CsNothing() {}
            """, "c.hlsl", CompileTarget.SpirV);
        Assert.False(PipelineLayout.TryCreateUnion([Entry(c, "CsNothing"), Entry(a, "PsTex"), Entry(b, "PsTint")], out var layout, out var error));
        Assert.Null(layout);
        Assert.Equal(LayoutErrorKind.ResourceConflict, error!.Kind);
        Assert.Contains("Set 1 binding 3", error.Message);
        Assert.Contains("'PsTex'", error.Message);
        Assert.Contains("'PsTint'", error.Message);
        Assert.Contains("Albedo", error.Message);
        Assert.Contains("Params", error.Message);
    }

    [Fact]
    public void TexelTypeDisagreementIsAConflict()
    {
        var a = ReflectSource("""
            [[vk::binding(0, 0)]] Texture2D<float4> T;
            [shader("compute")] [numthreads(1, 1, 1)] void CsA(uint3 id : SV_DispatchThreadID) { float4 v = T.Load(int3(id)); }
            """, "a.hlsl", CompileTarget.SpirV);
        var b = ReflectSource("""
            [[vk::binding(0, 0)]] Texture2D<uint4> T;
            [shader("compute")] [numthreads(1, 1, 1)] void CsB(uint3 id : SV_DispatchThreadID) { uint4 v = T.Load(int3(id)); }
            """, "b.hlsl", CompileTarget.SpirV);
        Assert.False(PipelineLayout.TryCreateUnion([Entry(a, "CsA"), Entry(b, "CsB")], out _, out var error));
        Assert.Contains("'CsA'", error!.Message);
        Assert.Contains("'CsB'", error.Message);
    }

    private const string PushA = """
        struct P { float4x4 m; float4 tint; };
        [[vk::push_constant]] ConstantBuffer<P> Pc;
        [shader("vertex")] float4 VsA(float4 pos : POSITION) : SV_Position { return mul(Pc.m, pos); }
        """;

    [Fact]
    public void PushConstantBlocksOfCompatibleStructsMergeIntoOneCoveringRange()
    {
        var a = ReflectSource(PushA, "a.hlsl", CompileTarget.SpirV);
        var b = ReflectSource("""
            struct Q { float4x4 m; float4 tint; float4 extra; };
            [[vk::push_constant]] ConstantBuffer<Q> Pc2;
            [shader("pixel")] float4 PsB(float4 p : SV_Position) : SV_Target { return Pc2.tint + Pc2.extra; }
            """, "b.hlsl", CompileTarget.SpirV);
        var c = ReflectSource("""
            struct R { float4x4 m; };
            [[vk::push_constant]] ConstantBuffer<R> Pc3;
            [shader("pixel")] float4 PsC(float4 p : SV_Position) : SV_Target { return Pc3.m[0]; }
            """, "c.hlsl", CompileTarget.SpirV);
        var union = Union(Entry(a, "VsA"), Entry(b, "PsB"), Entry(c, "PsC"));
        var block = Assert.Single(union.PushConstants);
        Assert.Equal(96u, block.Size);
        Assert.Equal(ShaderStages.Vertex | ShaderStages.Pixel, block.Stages);
        var range = Assert.Single(union.ToVulkan().PushConstantRanges);
        Assert.Equal((ShaderStageFlags.Vertex | ShaderStageFlags.Fragment, 0u, 96u), (range.StageFlags, range.Offset, range.Size));
    }

    [Fact]
    public void PushConstantBlocksThatDisagreeWhereTheyOverlapAreReported()
    {
        var a = ReflectSource(PushA, "a.hlsl", CompileTarget.SpirV);
        var b = ReflectSource("""
            struct Q { float4 first; float4x4 m; };
            [[vk::push_constant]] ConstantBuffer<Q> Pc2;
            [shader("pixel")] float4 PsB(float4 p : SV_Position) : SV_Target { return Pc2.first + Pc2.m[0]; }
            """, "b.hlsl", CompileTarget.SpirV);
        Assert.False(PipelineLayout.TryCreateUnion([Entry(a, "VsA"), Entry(b, "PsB")], out _, out var error));
        Assert.Equal(LayoutErrorKind.PushConstantConflict, error!.Kind);
        Assert.Contains("'VsA'", error.Message);
        Assert.Contains("'PsB'", error.Message);
        Assert.Contains("offset 0", error.Message);
    }

    [Fact]
    public void PushConstantBlocksWithoutMemberLayoutThatDifferAreReported()
    {
        var a = ReflectSource(PushA, "a.hlsl", CompileTarget.SpirV);
        var b = ReflectSource("""
            struct Q { float4x4 m; float4 tint; float4 extra; };
            [[vk::push_constant]] ConstantBuffer<Q> Pc2;
            [shader("pixel")] float4 PsB(float4 p : SV_Position) : SV_Target { return Pc2.tint + Pc2.extra; }
            """, "b.hlsl", CompileTarget.SpirV);
        // A generated entry layout has no member layout: same name and size is shared, anything else cannot be checked.
        var ea = Entry(a, "VsA").WithPushConstantType(null);
        var eb = Entry(b, "PsB").WithPushConstantType(null);
        Assert.False(PipelineLayout.TryCreateUnion([ea, eb], out _, out var error));
        Assert.Equal(LayoutErrorKind.PushConstantConflict, error!.Kind);
        Assert.Contains("member layout", error.Message);
    }

    [Fact]
    public void UnionOfNothingIsAnError()
    {
        Assert.False(PipelineLayout.TryCreateUnion([], out _, out var error));
        Assert.Equal(LayoutErrorKind.NoEntryPoints, error!.Kind);
    }

    // ---- widening ----

    [Fact]
    public void WideningAddsStagesToSetsBindingsAndPushConstants()
    {
        var quad = Reflect("2d/renderer/quad.hlsl");
        var ps = Union(Entry(quad, "PSBlur"));
        var plain = ps.ToVulkan();
        Assert.All(plain.SetLayouts.SelectMany(s => s.Bindings), b => Assert.Equal(ShaderStageFlags.Fragment, b.StageFlags));

        var widened = ps.ToVulkan(new VulkanLayoutOptions
        {
            WidenSets = new Dictionary<uint, ShaderStages> { [0] = ShaderStages.Vertex | ShaderStages.Pixel },
            WidenBindings = new Dictionary<(uint, uint), ShaderStages> { [(2, 0)] = ShaderStages.Compute },
        });
        Assert.All(widened.SetLayouts[0].Bindings, b => Assert.Equal(ShaderStageFlags.Vertex | ShaderStageFlags.Fragment, b.StageFlags));
        Assert.All(widened.SetLayouts[1].Bindings, b => Assert.Equal(ShaderStageFlags.Fragment, b.StageFlags));
        Assert.Equal(ShaderStageFlags.Fragment | ShaderStageFlags.Compute, widened.SetLayouts[2].Bindings.Single().StageFlags);

        var web = Convert(ps, new WebGpuLayoutOptions
        {
            WidenGroups = new Dictionary<uint, ShaderStages> { [0] = ShaderStages.Vertex | ShaderStages.Pixel },
            WidenBindings = new Dictionary<(uint, uint), ShaderStages> { [(2, 0)] = ShaderStages.Compute },
        });
        Assert.All(web.Groups[0].Entries, e => Assert.Equal(ShaderStageVisibility.Vertex | ShaderStageVisibility.Fragment, e.Visibility));
        Assert.Equal(ShaderStageVisibility.Fragment | ShaderStageVisibility.Compute, web.Groups[2].Entries.Single().Visibility);

        // The unwidened conversions are untouched by the options existing.
        Assert.Equal(plain.SetLayouts.SelectMany(s => s.Bindings).ToArray(), ps.ToVulkan(new VulkanLayoutOptions()).SetLayouts.SelectMany(s => s.Bindings).ToArray());
    }

    [Fact]
    public void WideningPushConstantRanges()
    {
        var push = Reflect("3d/push_lit.hlsl");
        var ps = Union(Entry(push, "PSMain"));
        Assert.Equal(ShaderStageFlags.Fragment, ps.ToVulkan().PushConstantRanges.Single().StageFlags);
        var range = ps.ToVulkan(new VulkanLayoutOptions { WidenPushConstants = ShaderStages.Vertex }).PushConstantRanges.Single();
        Assert.Equal(ShaderStageFlags.Vertex | ShaderStageFlags.Fragment, range.StageFlags);
        var web = Convert(ps, new WebGpuLayoutOptions { WidenPushConstants = ShaderStages.Vertex });
        Assert.Equal(ShaderStageVisibility.Vertex | ShaderStageVisibility.Fragment, Assert.Single(web.PushConstants).Visibility);
    }

    // ---- vertex overrides ----

    private static EntryLayout QuadVertex() => Entry(Reflect("2d/renderer/quad.hlsl"), "VSMain");

    private static VertexLayoutOverrides ColorAsBytes() => new()
    {
        Stride = 48,
        Attributes = new Dictionary<uint, VertexAttributeOverride> { [4] = new(VertexFormat.Unorm8x4, 44) },
    };

    [Fact]
    public void VertexOverrideFlowsIntoEveryApi()
    {
        var vs = QuadVertex();
        Assert.Equal(VertexFormat.Float32x4, vs.Vertex!.Attributes.ToArray().Single(a => a.Location == 4).Format);
        Assert.True(vs.TryWithVertexOverrides(ColorAsBytes(), out var over, out var error), error?.Message);

        var attrs = over!.Vertex!.Attributes.ToArray();
        Assert.Equal(new VertexAttribute(4, VertexFormat.Unorm8x4, 44), attrs.Single(a => a.Location == 4));
        Assert.Equal(48u, over.Vertex.Stride);
        // The other attributes keep what the shader gave them, and the original is unchanged.
        Assert.Equal(vs.Vertex.Attributes.ToArray().Where(a => a.Location != 4), attrs.Where(a => a.Location != 4));
        Assert.Equal(VertexFormat.Float32x4, vs.Vertex.Attributes.ToArray().Single(a => a.Location == 4).Format);

        var pipeline = Union(over);
        var vk = pipeline.EntryFor(ShaderStage.Vertex)!.Vertex!.ToVulkan(VertexStepMode.Instance);
        Assert.Equal(new VertexInputBindingInfo(0, 48, VertexInputRate.Instance), vk.Bindings.Single());
        Assert.Equal(new VertexInputAttributeInfo(4, 0, (Layouts.Vulkan.Format)37, 44), vk.Attributes.Single(a => a.Location == 4));

        var mtl = over.Vertex.ToMetal();
        Assert.Equal(48u, mtl.Stride);
        Assert.Equal(new MetalVertexAttribute(4, MetalVertexFormat.UChar4Normalized, 44, 30), mtl.Attributes.Single(a => a.AttributeIndex == 4));

        var web = over.Vertex.ToWebGpu();
        Assert.Equal(48u, web.ArrayStride);
        Assert.Equal(new VertexAttributeInfo("unorm8x4", 44, 4), web.Attributes.Single(a => a.ShaderLocation == 4));
    }

    [Fact]
    public void VertexOverrideRejectsFormatsThatDoNotFitTheShaderType()
    {
        var vs = QuadVertex();
        VertexLayoutOverrides One(uint location, VertexFormat format, uint offset, uint? stride = 64) =>
            new() { Stride = stride, Attributes = new Dictionary<uint, VertexAttributeOverride> { [location] = new(format, offset) } };

        // A float4 input cannot read integer data, and the reverse.
        Assert.False(vs.TryWithVertexOverrides(One(4, VertexFormat.Uint8x4, 0), out _, out var e1));
        Assert.Equal(LayoutErrorKind.InvalidVertexInput, e1!.Kind);
        Assert.Contains("'VSMain'", e1.Message);
        Assert.Contains("Location 4", e1.Message);
        Assert.Contains("float", e1.Message);
        Assert.False(vs.TryWithVertexOverrides(One(4, VertexFormat.Sint32x4, 0), out _, out _));

        // The tile shader's uint input accepts uint formats only.
        var tile = Entry(Reflect("2d/renderer/tile.hlsl"), "VSMain");
        Assert.False(tile.TryWithVertexOverrides(One(5, VertexFormat.Unorm8x4, 0), out _, out var e2));
        Assert.Contains("uint", e2!.Message);
        Assert.True(tile.TryWithVertexOverrides(One(5, VertexFormat.Uint8x4, 60), out var ok, out var e3), e3?.Message);
        Assert.Equal(VertexFormat.Uint8x4, ok!.Vertex!.Attributes.ToArray().Single(a => a.Location == 5).Format);

        // A different component count is fine (missing components are filled, extra ones dropped).
        Assert.True(vs.TryWithVertexOverrides(One(4, VertexFormat.Float16x2, 0), out _, out _));

        // Layout rules: inside the stride, aligned, a known location, a sane stride.
        Assert.False(vs.TryWithVertexOverrides(One(4, VertexFormat.Float32x4, 52), out _, out var e4));
        Assert.Contains("past the stride", e4!.Message);
        Assert.False(vs.TryWithVertexOverrides(One(4, VertexFormat.Float32x4, 2), out _, out var e5));
        Assert.Contains("multiple of 4", e5!.Message);
        Assert.False(vs.TryWithVertexOverrides(One(9, VertexFormat.Float32x4, 0), out _, out var e6));
        Assert.Contains("location 9", e6!.Message);
        Assert.False(vs.TryWithVertexOverrides(new VertexLayoutOverrides { Stride = 10 }, out _, out _));
        Assert.False(vs.TryWithVertexOverrides(new VertexLayoutOverrides { Stride = 8 }, out _, out var e7));
        Assert.Contains("past the stride", e7!.Message);
        Assert.False(vs.TryWithVertexOverrides(One(4, (VertexFormat)99, 0), out _, out _));

        // An entry point without vertex input.
        var ps = Entry(Reflect("2d/renderer/quad.hlsl"), "PSBlur");
        Assert.False(ps.TryWithVertexOverrides(new VertexLayoutOverrides(), out _, out var e8));
        Assert.Contains("'PSBlur'", e8!.Message);
    }

    [Fact]
    public void NoOverridesLeaveTheLayoutEqual()
    {
        var vs = QuadVertex();
        Assert.True(vs.TryWithVertexOverrides(new VertexLayoutOverrides(), out var same, out _));
        Assert.Equal(vs.Vertex!.Stride, same!.Vertex!.Stride);
        Assert.Equal(vs.Vertex.Attributes.ToArray(), same.Vertex!.Attributes.ToArray());
        Assert.Equal(vs.Resources.ToArray(), same.Resources.ToArray());
    }

    // ---- dynamic offsets ----

    private static WebGpuPipelineLayout Convert(PipelineLayout p, WebGpuLayoutOptions? options = null)
    {
        Assert.True(p.TryToWebGpu(out var web, out var error, options), error?.Message);
        return web!;
    }

    [Fact]
    public void DynamicOffsetsByNameBySlotAndByFlag()
    {
        var quad = Reflect("2d/renderer/quad.hlsl");
        var pipeline = Union(Entry(quad, "VSMain"), Entry(quad, "PSBlur"));

        static DescriptorType At(PipelineLayoutInfo i, int set, uint binding) => i.SetLayouts[set].Bindings.Single(b => b.Binding == binding).DescriptorType;

        var none = pipeline.ToVulkan();
        Assert.Equal(DescriptorType.UniformBuffer, At(none, 0, 0));
        Assert.All(none.SetLayouts.SelectMany(s => s.Bindings), b => Assert.False(b.IsDynamic));

        var byName = pipeline.ToVulkan(new VulkanLayoutOptions { DynamicBuffers = ["Params"] });
        Assert.Equal(DescriptorType.UniformBufferDynamic, At(byName, 2, 0));
        Assert.Equal(DescriptorType.UniformBuffer, At(byName, 0, 0));
        Assert.True(byName.SetLayouts[2].Bindings.Single().IsDynamic);

        var bySlot = pipeline.ToVulkan(new VulkanLayoutOptions { DynamicBindings = [(2u, 0u), (1u, 0u)] });
        Assert.Equal(DescriptorType.UniformBufferDynamic, At(bySlot, 2, 0));
        Assert.Equal(DescriptorType.SampledImage, At(bySlot, 1, 0)); // a texture stays a texture
        Assert.Equal(byName.SetLayouts[2].Bindings.ToArray(), bySlot.SetLayouts[2].Bindings.ToArray());

        var webByName = Convert(pipeline, new WebGpuLayoutOptions { DynamicBuffers = ["Params"] });
        Assert.True(webByName.Groups[2].Entries.Single().HasDynamicOffset);
        Assert.False(webByName.Groups[0].Entries.Single().HasDynamicOffset);
        var webBySlot = Convert(pipeline, new WebGpuLayoutOptions { DynamicBindings = [(2u, 0u), (1u, 0u)] });
        Assert.True(webBySlot.Groups[2].Entries.Single().HasDynamicOffset);
        Assert.False(webBySlot.Groups[1].Entries.Single(x => x.Binding == 0).HasDynamicOffset);
        Assert.All(Convert(pipeline).Groups.SelectMany(g => g.Entries), e => Assert.False(e.HasDynamicOffset));

        // The flag on the resource itself, set by the host on a layout it built, survives a union and reaches both APIs.
        var ps = Entry(quad, "PSBlur");
        var flagged = ps.Resources.Select(r => r.Name == "Params" ? r with { DynamicOffset = true } : r).ToArray();
        var entry = new EntryLayout(ps.Name, ps.Stage, flagged);
        var other = Entry(quad, "PSGrade");
        var viaFlag = Union(entry, other);
        Assert.Equal(DescriptorType.UniformBufferDynamic, At(viaFlag.ToVulkan(), 2, 0));
        Assert.True(Convert(viaFlag).Groups[2].Entries.Single().HasDynamicOffset);
    }
}
