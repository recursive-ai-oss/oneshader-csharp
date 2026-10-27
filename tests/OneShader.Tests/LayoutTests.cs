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
/// Pipeline layout helpers: the Vulkan, Metal and WebGPU descriptions of corpus shaders agree with what reflection says,
/// stage visibility merges, and conflicts come back as values.
/// </summary>
public sealed class LayoutTests
{
    private static readonly CompileTarget[] All = [CompileTarget.SpirV, CompileTarget.Msl, CompileTarget.Wgsl];

    private static ShaderReflection Reflect(string rel, params CompileTarget[] targets)
    {
        var r = ShaderCompiler.Compile(
            File.ReadAllText(Path.Combine(CorpusFiles.Root(), rel)),
            Path.GetFileName(rel),
            new CompileOptions { Targets = targets.Length == 0 ? All : targets, IncludeDirectories = [Path.GetDirectoryName(Path.Combine(CorpusFiles.Root(), rel))!] },
            TestContext.Current.CancellationToken);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        return r.Reflection!;
    }

    private static EntryLayout Entry(ShaderReflection reflection, string name)
    {
        Assert.True(EntryLayout.TryCreate(reflection, name, out var layout, out var error), error?.Message);
        return layout!;
    }

    private static PipelineLayout Pipeline(ShaderReflection reflection, params string[] entries)
    {
        var layouts = entries.Select(e => Entry(reflection, e)).ToArray();
        Assert.True(PipelineLayout.TryCreate(layouts, out var pipeline, out var error), error?.Message);
        return pipeline!;
    }

    // Spec values written out here on purpose: the test must not share the table under test.
    private static uint ExpectedDescriptor(ResourceKind k) => k switch
    {
        ResourceKind.Sampler => 0,
        ResourceKind.SampledTexture => 2,
        ResourceKind.StorageTexture => 3,
        ResourceKind.UniformTexelBuffer => 4,
        ResourceKind.StorageTexelBuffer => 5,
        ResourceKind.ConstantBuffer => 6,
        _ => 7,
    };

    private static uint ExpectedStageBits(ShaderStages s) =>
        ((s & ShaderStages.Vertex) != 0 ? 0x1u : 0) | ((s & ShaderStages.Pixel) != 0 ? 0x10u : 0) | ((s & ShaderStages.Compute) != 0 ? 0x20u : 0);

    private static ShaderStages StageOf(ShaderReflection r, string entry) => r.EntryPoints.Single(e => e.Name == entry).Stage switch
    {
        ShaderStage.Vertex => ShaderStages.Vertex,
        ShaderStage.Pixel => ShaderStages.Pixel,
        _ => ShaderStages.Compute,
    };

    private static readonly string[] Gfx = ["VSMain", "PSMain"];
    private static readonly string[] Cs = ["CSMain"];

    public static TheoryData<string, string[]> Cases() => new()
    {
        { "2d/sprite.hlsl", Gfx },
        { "2d/ui_rect.hlsl", Gfx },
        { "3d/push_lit.hlsl", Gfx },
        { "3d/pbr_forward.hlsl", Gfx },
        { "lang/resource_arrays.hlsl", Cs },
        { "compute/histogram.hlsl", Cs },
        { "compute/append_consume.hlsl", Cs },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void VulkanDescriptionEqualsReflection(string rel, string[] entries)
    {
        var reflection = Reflect(rel, CompileTarget.SpirV);
        var info = Pipeline(reflection, entries).ToVulkan();

        // Every resource an entry point uses, at its SPIR-V slot, with the union of the stages of the entries that use it.
        var expected = new Dictionary<(uint Set, uint Binding), (uint Type, uint Count, uint Stages)>();
        void Add(string name, uint set, uint binding, ResourceKind kind, uint count, ShaderStages stage)
        {
            var key = (set, binding);
            expected.TryGetValue(key, out var old);
            expected[key] = (ExpectedDescriptor(kind), Math.Max(count, 1), old.Stages | ExpectedStageBits(stage));
        }

        foreach (string e in entries)
        {
            foreach (string used in reflection.EntryPoints.Single(x => x.Name == e).UsedResources)
            {
                var r = reflection.Resources.Single(x => x.Name == used);
                var slot = r.Slots.Single(s => s.Target == CompileTarget.SpirV);
                Add(r.Name, slot.Set, slot.Binding, r.Kind, r.Count, StageOf(reflection, e));
                foreach (var c in r.CounterSlots.Where(s => s.Target == CompileTarget.SpirV))
                {
                    Add(r.Name + ".counter", c.Set, c.Binding, ResourceKind.StorageBuffer, 1, StageOf(reflection, e));
                }
            }
        }

        var actual = new Dictionary<(uint, uint), (uint, uint, uint)>();
        for (int set = 0; set < info.SetLayouts.Count; set++)
        {
            Assert.Equal((uint)set, info.SetLayouts[set].Set);
            uint previous = 0;
            bool first = true;
            foreach (var b in info.SetLayouts[set].Bindings)
            {
                Assert.True(first || b.Binding > previous, "bindings are sorted and unique");
                first = false;
                previous = b.Binding;
                actual[((uint)set, b.Binding)] = ((uint)b.DescriptorType, b.DescriptorCount, (uint)b.StageFlags);
            }
        }

        Assert.Equal(expected.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)), actual.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)));

        // Push constants: one range at offset 0 of the reflected size for the stages that read it.
        var pushes = reflection.PushConstants;
        if (pushes.Count == 0)
        {
            Assert.Empty(info.PushConstantRanges);
        }
        else
        {
            var range = Assert.Single(info.PushConstantRanges);
            Assert.Equal(0u, range.Offset);
            Assert.Equal(pushes[0].Layout.Size, range.Size);
            Assert.Equal(entries.Where(e => reflection.EntryPoints.Single(x => x.Name == e).PushConstant is not null).Aggregate(0u, (m, e) => m | ExpectedStageBits(StageOf(reflection, e))), (uint)range.StageFlags);
        }
    }

    // Append and consume buffers have no Metal form, and resource arrays no WebGPU form; the other cases cover them.
    public static TheoryData<string, string[]> MetalCases() => new()
    {
        { "2d/sprite.hlsl", Gfx },
        { "2d/ui_rect.hlsl", Gfx },
        { "3d/push_lit.hlsl", Gfx },
        { "3d/pbr_forward.hlsl", Gfx },
        { "lang/resource_arrays.hlsl", Cs },
        { "compute/histogram.hlsl", Cs },
    };

    [Theory]
    [MemberData(nameof(MetalCases))]
    public void MetalDescriptionEqualsReflection(string rel, string[] entries)
    {
        var reflection = Reflect(rel, CompileTarget.SpirV, CompileTarget.Msl);
        var pipeline = Pipeline(reflection, entries);
        Assert.True(pipeline.TryToMetal(out var metal, out var error), error?.Message);
        Assert.Equal(entries.Length, metal!.Stages.Count);
        foreach (string e in entries)
        {
            var entry = reflection.EntryPoints.Single(x => x.Name == e);
            var stage = metal.ForStage(entry.Stage)!;
            var expected = new List<(string, uint, uint)>();
            foreach (string used in entry.UsedResources)
            {
                var r = reflection.Resources.Single(x => x.Name == used);
                var msl = r.Slots.Single(s => s.Target == CompileTarget.Msl).Binding;
                var vk = r.Slots.Single(s => s.Target == CompileTarget.SpirV);
                expected.Add((r.Name, vk.Binding, msl));
                if (r.CounterSlots.Count > 0)
                {
                    expected.Add((r.Name + ".counter", r.CounterSlots.Single(s => s.Target == CompileTarget.SpirV).Binding, r.CounterSlots.Single(s => s.Target == CompileTarget.Msl).Binding));
                }
            }

            Assert.Equal(
                expected.OrderBy(x => x.Item1, StringComparer.Ordinal).ToArray(),
                stage.Arguments.Select(a => (a.Name, a.Binding, a.Index)).OrderBy(x => x.Name, StringComparer.Ordinal).ToArray());

            // Indices count within a class, so no two arguments of a class and stage share one.
            foreach (var group in stage.Arguments.GroupBy(a => a.Kind))
            {
                var taken = new HashSet<uint>();
                foreach (var a in group)
                {
                    for (uint i = 0; i < a.Count; i++)
                    {
                        Assert.True(taken.Add(a.Index + i), a.Name + " overlaps another " + group.Key);
                    }
                }
            }

            if (entry.PushConstant is { } pushName)
            {
                var p = reflection.PushConstants.Single(x => x.Name == pushName);
                Assert.Equal(new MetalPushConstant(p.Name, p.Slots.Single(s => s.Target == CompileTarget.Msl).Binding, p.Layout.Size), stage.PushConstant);
            }
            else
            {
                Assert.Null(stage.PushConstant);
            }
        }
    }

    [Theory]
    [InlineData("2d/sprite.hlsl", new[] { "VSMain", "PSMain" })]
    [InlineData("2d/ui_rect.hlsl", new[] { "VSMain", "PSMain" })]
    [InlineData("3d/push_lit.hlsl", new[] { "VSMain", "PSMain" })]
    public void WebGpuDescriptionEqualsReflection(string rel, string[] entries)
    {
        var reflection = Reflect(rel, CompileTarget.SpirV, CompileTarget.Wgsl);
        var pipeline = Pipeline(reflection, entries);
        Assert.True(pipeline.TryToWebGpu(out var web, out var error), error?.Message);
        foreach (string e in entries)
        {
            var entry = reflection.EntryPoints.Single(x => x.Name == e);
            uint vis = entry.Stage switch { ShaderStage.Vertex => 1u, ShaderStage.Pixel => 2u, _ => 4u };
            foreach (string used in entry.UsedResources)
            {
                var r = reflection.Resources.Single(x => x.Name == used);
                var slot = r.Slots.Single(s => s.Target == CompileTarget.Wgsl);
                var found = web!.Groups[(int)slot.Set].Entries.Single(x => x.Binding == slot.Binding);
                Assert.Equal(r.Name, found.Name);
                Assert.NotEqual(0u, (uint)found.Visibility & vis);
                var expectedKind = r.Kind switch
                {
                    ResourceKind.Sampler => BindingKind.Sampler,
                    ResourceKind.SampledTexture => BindingKind.Texture,
                    ResourceKind.StorageTexture => BindingKind.StorageTexture,
                    _ => BindingKind.Buffer,
                };
                Assert.Equal(expectedKind, found.Kind);
            }
        }

        // Push constants: a uniform buffer at the reflected group and binding, sized up to 16 bytes.
        foreach (var p in reflection.PushConstants)
        {
            var slot = p.Slots.Single(s => s.Target == CompileTarget.Wgsl);
            var emu = Assert.Single(web!.PushConstants);
            Assert.Equal((slot.Set, slot.Binding, p.Layout.Size, (p.Layout.Size + 15) / 16 * 16), (emu.Group, emu.Binding, emu.Size, emu.BufferSize));
            var inGroup = web.Groups[(int)slot.Set].Entries.Single(x => x.Binding == slot.Binding);
            Assert.True(inGroup.IsPushConstant);
            Assert.Equal(BufferBindingType.Uniform, inGroup.BufferType);
            Assert.Equal((ulong)emu.BufferSize, inGroup.MinBindingSize);
        }
    }


    [Theory]
    [InlineData("2d/sprite.hlsl", false)]
    [InlineData("2d/ui_rect.hlsl", false)]
    [InlineData("3d/push_lit.hlsl", false)]
    [InlineData("3d/pbr_forward.hlsl", true)]
    [InlineData("compute/histogram.hlsl", true)]
    public void WebGpuLayoutMatchesWhatTheWgslDeclares(string rel, bool needsHostChoices)
    {
        string path = Path.Combine(CorpusFiles.Root(), rel);
        var result = ShaderCompiler.Compile(
            File.ReadAllText(path),
            Path.GetFileName(path),
            new CompileOptions { Targets = [CompileTarget.SpirV, CompileTarget.Wgsl], IncludeDirectories = [Path.GetDirectoryName(path)!] },
            TestContext.Current.CancellationToken);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var entries = result.Reflection!.EntryPoints.Select(e => e.Name).ToArray();
        var pipeline = Pipeline(result.Reflection, entries);

        // What each entry point's WGSL declares: group, binding, address space and type.
        var declared = new List<(string Entry, uint Group, uint Binding, string Space, string Name, string Type)>();
        foreach (string e in entries)
        {
            string wgsl = result.Find(e, CompileTarget.Wgsl)!.AsText();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(wgsl, @"@group\((\d+)\)\s*@binding\((\d+)\)\s*var(?:<([^>]*)>)?\s+(\w+)\s*:\s*([^;]+);"))
            {
                declared.Add((e, uint.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), uint.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), m.Groups[3].Value.Replace(" ", "", StringComparison.Ordinal), m.Groups[4].Value, m.Groups[5].Value.Trim()));
            }
        }

        // Host choices the shader source implies but reflection does not carry.
        var options = new WebGpuLayoutOptions
        {
            DepthTextures = declared.Where(d => d.Type.StartsWith("texture_depth", StringComparison.Ordinal)).Select(d => d.Name).ToArray(),
            ReadOnlyStorageBuffers = declared.Where(d => d.Space == "storage,read").Select(d => d.Name).ToArray(),
        };
        if (!needsHostChoices)
        {
            options = new WebGpuLayoutOptions();
        }

        var web = Convert(pipeline, options);
        foreach (var d in declared)
        {
            var found = web.Groups[(int)d.Group].Entries.SingleOrDefault(x => x.Binding == d.Binding);
            Assert.True(found is not null, d.Name + " is declared at group " + d.Group + " binding " + d.Binding + " but the layout has no such entry");
            if (d.Space == "uniform")
            {
                Assert.Equal((BindingKind.Buffer, BufferBindingType.Uniform), (found!.Kind, found.BufferType));
            }
            else if (d.Space.StartsWith("storage", StringComparison.Ordinal))
            {
                Assert.Equal((BindingKind.Buffer, d.Space == "storage,read" ? BufferBindingType.ReadOnlyStorage : BufferBindingType.Storage), (found!.Kind, found.BufferType));
            }
            else if (d.Type.StartsWith("sampler", StringComparison.Ordinal))
            {
                Assert.Equal((BindingKind.Sampler, d.Type == "sampler_comparison" ? SamplerBindingType.Comparison : SamplerBindingType.Filtering), (found!.Kind, found.SamplerType));
            }
            else if (d.Type.StartsWith("texture_depth", StringComparison.Ordinal))
            {
                Assert.Equal((BindingKind.Texture, TextureSampleType.Depth), (found!.Kind, found.SampleType));
            }
            else if (d.Type.StartsWith("texture_storage", StringComparison.Ordinal))
            {
                Assert.Equal(BindingKind.StorageTexture, found!.Kind);
            }
            else
            {
                Assert.Equal(BindingKind.Texture, found!.Kind);
                Assert.Equal(d.Type.Contains("<u32>", StringComparison.Ordinal) ? TextureSampleType.Uint : d.Type.Contains("<i32>", StringComparison.Ordinal) ? TextureSampleType.Sint : TextureSampleType.Float, found.SampleType);
            }

            // The entry's stage must be visible.
            uint stageBit = result.Reflection.EntryPoints.Single(x => x.Name == d.Entry).Stage switch { ShaderStage.Vertex => 1u, ShaderStage.Pixel => 2u, _ => 4u };
            Assert.NotEqual(0u, (uint)found.Visibility & stageBit);
        }
    }

    [Fact]
    public void WebGpuTextureAndSamplerDetails()
    {
        var web = Convert(Pipeline(Reflect("2d/sprite.hlsl"), "VSMain", "PSMain"));
        var tex = web.Groups.SelectMany(g => g.Entries).Single(x => x.Kind == BindingKind.Texture);
        Assert.Equal("float", tex.SampleType.ToWebGpuString());
        Assert.Equal("2d", tex.ViewDimension.ToWebGpuString());
        Assert.False(tex.Multisampled);
        Assert.Equal(ShaderStageVisibility.Fragment, tex.Visibility);
        var smp = web.Groups.SelectMany(g => g.Entries).Single(x => x.Kind == BindingKind.Sampler);
        Assert.Equal("filtering", smp.SamplerType.ToWebGpuString());
    }

    private static WebGpuPipelineLayout Convert(PipelineLayout p, WebGpuLayoutOptions? options = null)
    {
        Assert.True(p.TryToWebGpu(out var web, out var error, options), error?.Message);
        return web!;
    }

    [Fact]
    public void WebGpuReportsWhatItCannotExpressAndTakesHostChoices()
    {
        // A resource array has no WebGPU form.
        var arrays = Pipeline(Reflect("lang/resource_arrays.hlsl", CompileTarget.SpirV), "CSMain");
        Assert.False(arrays.TryToWebGpu(out var none, out var error));
        Assert.Null(none);
        Assert.Equal(LayoutErrorKind.Unsupported, error!.Kind);
        Assert.Contains("binding arrays", error.Message, StringComparison.Ordinal);

        // A storage texture needs its format from [[vk::image_format]], and its access from the host (default write-only).
        const string source = """
            [[vk::image_format("rgba8")]] RWTexture2D<float4> Out : register(u0);
            [[vk::image_format("r32f")]] RWTexture2D<float> Acc : register(u1);
            [shader("compute")] [numthreads(8, 8, 1)] void CSMain(uint3 id : SV_DispatchThreadID) { Out[id.xy] = Acc[id.xy].xxxx; }
            """;
        var result = ShaderCompiler.Compile(source, "storage.hlsl", new CompileOptions { Targets = All }, TestContext.Current.CancellationToken);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Equal("rgba8", result.Reflection!.Resources.Single(r => r.Name == "Out").ImageFormat);
        var storage = Pipeline(result.Reflection, "CSMain");
        var web = Convert(storage, new WebGpuLayoutOptions { StorageAccess = new Dictionary<string, StorageTextureAccess> { ["Acc"] = StorageTextureAccess.ReadWrite } });
        var entries = web.Groups.SelectMany(g => g.Entries).ToDictionary(x => x.Name);
        Assert.Equal(("rgba8unorm", "write-only", "2d"), (entries["Out"].StorageFormat, entries["Out"].Access.ToWebGpuString(), entries["Out"].ViewDimension.ToWebGpuString()));
        Assert.Equal(("r32float", "read-write"), (entries["Acc"].StorageFormat, entries["Acc"].Access.ToWebGpuString()));
    }

    [Fact]
    public void VertexFormatsMapToEveryApi()
    {
        // (format, VkFormat, MTLVertexFormat, GPUVertexFormat) from the three specifications.
        (VertexFormat, uint, uint, string)[] table =
        [
            (VertexFormat.Float32, 100, 28, "float32"),
            (VertexFormat.Float32x2, 103, 29, "float32x2"),
            (VertexFormat.Float32x3, 106, 30, "float32x3"),
            (VertexFormat.Float32x4, 109, 31, "float32x4"),
            (VertexFormat.Sint32, 99, 32, "sint32"),
            (VertexFormat.Sint32x2, 102, 33, "sint32x2"),
            (VertexFormat.Sint32x3, 105, 34, "sint32x3"),
            (VertexFormat.Sint32x4, 108, 35, "sint32x4"),
            (VertexFormat.Uint32, 98, 36, "uint32"),
            (VertexFormat.Uint32x2, 101, 37, "uint32x2"),
            (VertexFormat.Uint32x3, 104, 38, "uint32x3"),
            (VertexFormat.Uint32x4, 107, 39, "uint32x4"),
            (VertexFormat.Float16x2, 83, 25, "float16x2"),
            (VertexFormat.Float16x4, 97, 27, "float16x4"),
            (VertexFormat.Unorm8x4, 37, 9, "unorm8x4"),
            (VertexFormat.Snorm8x4, 38, 12, "snorm8x4"),
            (VertexFormat.Uint8x4, 41, 3, "uint8x4"),
            (VertexFormat.Sint8x4, 42, 6, "sint8x4"),
            (VertexFormat.Unorm16x2, 77, 19, "unorm16x2"),
            (VertexFormat.Unorm16x4, 91, 21, "unorm16x4"),
            (VertexFormat.Snorm16x2, 78, 22, "snorm16x2"),
            (VertexFormat.Snorm16x4, 92, 24, "snorm16x4"),
            (VertexFormat.Uint16x2, 81, 13, "uint16x2"),
            (VertexFormat.Uint16x4, 95, 15, "uint16x4"),
            (VertexFormat.Sint16x2, 82, 16, "sint16x2"),
            (VertexFormat.Sint16x4, 96, 18, "sint16x4"),
        ];
        Assert.Equal(Enum.GetValues<VertexFormat>().Length, table.Length);
        foreach (var (f, vk, mtl, web) in table)
        {
            Assert.Equal(vk, (uint)VertexLayout.VulkanFormat(f));
            Assert.Equal(mtl, (uint)VertexLayout.MetalFormat(f));
            Assert.Equal(web, VertexLayout.WebGpuFormat(f));
        }
    }

    [Fact]
    public void VertexLayoutsFollowReflectionOnEveryApi()
    {
        var reflection = Reflect("2d/sprite.hlsl");
        var entry = reflection.EntryPoints.Single(e => e.Name == "VSMain");
        var layout = Entry(reflection, "VSMain").Vertex!;
        var inputs = entry.Inputs.Where(i => i.Location is not null && i.Format is not null).ToArray();
        Assert.Equal(entry.VertexStride, layout.Stride);
        Assert.Equal(inputs.Select(i => new VertexAttribute(i.Location!.Value, i.Format!.Value, i.Offset)), layout.Attributes.ToArray());

        var vk = layout.ToVulkan(VertexStepMode.Instance, 2);
        var binding = Assert.Single(vk.Bindings);
        Assert.Equal((2u, entry.VertexStride, VertexInputRate.Instance), (binding.Binding, binding.Stride, binding.InputRate));
        Assert.Equal(inputs.Select(i => (i.Location!.Value, 2u, i.Offset)), vk.Attributes.Select(a => (a.Location, a.Binding, a.Offset)));

        var mtl = layout.ToMetal(7, VertexStepMode.Instance);
        Assert.Equal((7u, entry.VertexStride, VertexStepFunction.PerInstance), (mtl.BufferIndex, mtl.Stride, mtl.StepFunction));
        Assert.Equal(inputs.Select(i => (i.Location!.Value, i.Offset, 7u)), mtl.Attributes.Select(a => (a.AttributeIndex, a.Offset, a.BufferIndex)));

        var web = layout.ToWebGpu();
        Assert.Equal("vertex", web.StepMode.ToWebGpuString());
        Assert.Equal(inputs.Select(i => (i.Location!.Value, i.Offset)), web.Attributes.Select(a => (a.ShaderLocation, a.Offset)));
    }

    [Fact]
    public void StageVisibilityMergesAcrossEntryPoints()
    {
        var reflection = Reflect("3d/pbr_forward.hlsl");
        var vs = Entry(reflection, "VSMain");
        var ps = Entry(reflection, "PSMain");
        var merged = Pipeline(reflection, "VSMain", "PSMain");
        var shared = vs.Resources.Select(r => r.Name).Intersect(ps.Resources.Select(r => r.Name)).ToArray();
        Assert.NotEmpty(shared);
        foreach (var b in merged.Groups.SelectMany(g => g.Entries))
        {
            bool inVs = vs.Resources.Any(r => r.Name == b.Resource.Name);
            bool inPs = ps.Resources.Any(r => r.Name == b.Resource.Name);
            var expected = (inVs ? ShaderStages.Vertex : 0) | (inPs ? ShaderStages.Pixel : 0);
            Assert.Equal(expected, b.Resource.Stages);
        }

        // The entry layouts themselves carry only their own stage.
        Assert.All(vs.Resources, r => Assert.Equal(ShaderStages.Vertex, r.Stages));
        Assert.All(ps.Resources, r => Assert.Equal(ShaderStages.Pixel, r.Stages));
    }

    [Fact]
    public void PushConstantsMergeStagesAndKeepPerStageMetalIndices()
    {
        var reflection = Reflect("3d/push_lit.hlsl");
        var merged = Pipeline(reflection, "VSMain", "PSMain");
        var block = Assert.Single(merged.PushConstants);
        Assert.Equal(("Draw", 112u, ShaderStages.Vertex | ShaderStages.Pixel), (block.Name, block.Size, block.Stages));
        Assert.Equal(block.MetalIndexFor(ShaderStage.Vertex), block.MetalIndexFor(ShaderStage.Pixel));
        Assert.Equal(LayoutSlots.None, block.MetalIndexFor(ShaderStage.Compute));
        Assert.Equal(0u, block.MetalIndexFor(ShaderStage.Vertex));
    }

    [Fact]
    public void ConflictsAreValuesNotExceptions()
    {
        const string a = "Texture2D<float4> T : register(t0); SamplerState S : register(s1);\n[shader(\"vertex\")] float4 VSMain(uint i : SV_VertexID) : SV_Position { return T.Load(int3(0, 0, 0)); }";
        const string b = "StructuredBuffer<float4> T : register(t0); SamplerState S : register(s1);\n[shader(\"pixel\")] float4 PSMain() : SV_Target { return T[0]; }";
        var ra = ShaderCompiler.Compile(a, "a.hlsl", null, TestContext.Current.CancellationToken).Reflection!;
        var rb = ShaderCompiler.Compile(b, "b.hlsl", null, TestContext.Current.CancellationToken).Reflection!;
        var va = Entry(ra, "VSMain");
        var pb = Entry(rb, "PSMain");

        Assert.False(PipelineLayout.TryCreate([va, pb], out var layout, out var error));
        Assert.Null(layout);
        Assert.Equal(LayoutErrorKind.ResourceConflict, error!.Kind);
        Assert.Contains("set 0 binding 0", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[[vk::binding]]", error.Message, StringComparison.Ordinal);

        Assert.False(PipelineLayout.TryCreate([va, va], out _, out error));
        Assert.Equal(LayoutErrorKind.DuplicateStage, error!.Kind);
        Assert.False(PipelineLayout.TryCreate([], out _, out error));
        Assert.Equal(LayoutErrorKind.NoEntryPoints, error!.Kind);
        Assert.False(EntryLayout.TryCreate(ra, "Nope", out _, out error));
        Assert.Equal(LayoutErrorKind.UnknownEntryPoint, error!.Kind);
        Assert.Throws<InvalidOperationException>(() => PipelineLayout.Create(va, va));
    }

    [Fact]
    public void MissingTargetsAreReported()
    {
        var spirvOnly = Pipeline(Reflect("2d/sprite.hlsl", CompileTarget.SpirV), "VSMain", "PSMain");
        Assert.False(spirvOnly.TryToMetal(out _, out var error));
        Assert.Equal(LayoutErrorKind.MissingTarget, error!.Kind);
        Assert.Contains("Msl", error.Message, StringComparison.Ordinal);
        Assert.NotEmpty(spirvOnly.ToVulkan().SetLayouts);

        var pushSpirvOnly = Pipeline(Reflect("3d/push_lit.hlsl", CompileTarget.SpirV), "VSMain", "PSMain");
        Assert.False(pushSpirvOnly.TryToWebGpu(out _, out error));
        Assert.Equal(LayoutErrorKind.MissingTarget, error!.Kind);
    }

    [Fact]
    public void MetalOnlyBuildsKeepTextureAndBufferIndexZeroApart()
    {
        // With only Metal selected a buffer, a texture and a sampler may all be number 0; they must not be mistaken for one binding.
        var metalOnly = Pipeline(Reflect("2d/sprite.hlsl", CompileTarget.Msl), "VSMain", "PSMain");
        Assert.True(metalOnly.TryToMetal(out var metal, out var error), error?.Message);
        var ps = metal!.ForStage(ShaderStage.Pixel)!;
        Assert.Contains(ps.Arguments, a => a.Kind == ArgumentKind.Texture && a.Index == 0);
        Assert.Contains(ps.Arguments, a => a.Kind == ArgumentKind.Sampler && a.Index == 0);
    }

    [Fact]
    public void ResultsAreDeterministic()
    {
        var a = Pipeline(Reflect("3d/pbr_forward.hlsl"), "VSMain", "PSMain").ToVulkan();
        var b = Pipeline(Reflect("3d/pbr_forward.hlsl"), "VSMain", "PSMain").ToVulkan();
        Assert.Equal(
            a.SetLayouts.SelectMany(s => s.Bindings).ToArray(),
            b.SetLayouts.SelectMany(s => s.Bindings).ToArray());
    }

    [Fact]
    public void DynamicBufferOptionSwitchesTheDescriptorType()
    {
        var p = Pipeline(Reflect("2d/sprite.hlsl"), "VSMain", "PSMain");
        string cbuffer = p.Groups.SelectMany(g => g.Entries).First(e => e.Resource.Kind == ResourceKind.ConstantBuffer).Resource.Name;
        var info = p.ToVulkan(new VulkanLayoutOptions { DynamicBuffers = [cbuffer] });
        Assert.Contains(info.SetLayouts.SelectMany(s => s.Bindings), b => b.Name == cbuffer && b.DescriptorType == DescriptorType.UniformBufferDynamic);
    }
}
