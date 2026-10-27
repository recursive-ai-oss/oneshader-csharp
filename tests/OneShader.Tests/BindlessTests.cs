using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using OneShader.Diagnostics;
using OneShader.Layouts;
using OneShader.Layouts.Metal;
using OneShader.Layouts.Vulkan;
using OneShader.Reflection;
using OneShader.Types;
using Xunit;

namespace OneShader.Tests;

/// <summary>Unbounded resource arrays, SM 6.6 descriptor heaps and non-uniform indexing, on every target and in the layout helpers.</summary>
public partial class BindlessTests
{
    private const string Textures =
        "[[vk::binding(0, 1)]] Texture2D<float4> Tex[] : register(t0, space1);\n" +
        "[[vk::binding(0, 2)]] SamplerState Smp[] : register(s0, space2);\n" +
        "struct PS { float4 pos : SV_Position; nointerpolation uint id : ID; float2 uv : UV; };\n" +
        "[shader(\"pixel\")] float4 PSMain(PS i) : SV_Target { return Tex[NonUniformResourceIndex(i.id)].Sample(Smp[i.id & 1], i.uv); }\n";

    private const string Heap =
        "[[vk::binding(0, 1)]] RWStructuredBuffer<float4> Out;\n" +
        "[shader(\"compute\")] [numthreads(8,1,1)] void CSMain(uint3 id : SV_DispatchThreadID)\n" +
        "{\n" +
        "  Texture2D<float4> t = ResourceDescriptorHeap[id.x];\n" +
        "  SamplerState s = SamplerDescriptorHeap[id.x & 3];\n" +
        "  StructuredBuffer<uint> b = ResourceDescriptorHeap[NonUniformResourceIndex(id.x + 4)];\n" +
        "  Out[id.x] = t.SampleLevel(s, float2(0, 0), 0) + b[0];\n" +
        "}\n";

    private static CompileResult Compile(string text, params CompileTarget[] targets)
    {
        var options = targets.Length == 0 ? new CompileOptions() : new CompileOptions { Targets = targets };
        return ShaderCompiler.Compile(text, "t.hlsl", options, TestContext.Current.CancellationToken);
    }

    private static string[] Errors(CompileResult r) =>
        r.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Span.Line + "," + d.Span.Column + " " + d.Code + ": " + d.Message).ToArray();

    // ---- reflection ----

    [Fact]
    public void AnUnboundedArrayIsReflectedWithCountZero()
    {
        var r = Compile(Textures, CompileTarget.SpirV, CompileTarget.Msl);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        var tex = r.Reflection!.Resources.Single(x => x.Name == "Tex");
        Assert.Equal(0u, tex.Count);
        Assert.True(tex.IsUnbounded);
        Assert.Equal(DescriptorHeaps.None, tex.Heap);
        Assert.Equal((1u, 0u), (tex.Slots[0].Set, tex.Slots[0].Binding));
        var entry = r.Reflection.EntryPoints.Single();
        Assert.Equal(DeviceFeatures.RuntimeDescriptorArray | DeviceFeatures.NonUniformIndexing, entry.RequiredFeatures);
        Assert.Equal(DescriptorHeaps.None, entry.DescriptorHeaps);
    }

    [Fact]
    public void HeapsAreOneResourcePerHeapWithTheKindsReadFromThem()
    {
        var r = Compile(Heap, CompileTarget.SpirV, CompileTarget.Msl);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        var model = r.Reflection!;
        Assert.Equal(["Out", "ResourceDescriptorHeap", "SamplerDescriptorHeap"], model.Resources.Select(x => x.Name).ToArray());
        var resources = model.Resources.Single(x => x.Name == "ResourceDescriptorHeap");
        Assert.Equal(DescriptorHeaps.Resource, resources.Heap);
        Assert.True(resources.IsUnbounded);
        Assert.Equal([ResourceKind.SampledTexture, ResourceKind.ReadOnlyStorageBuffer], resources.HeapKinds);
        // DXC: the first binding of set 0 that nothing else uses, then the next one for the samplers.
        Assert.Equal((0u, 0u), (resources.Slots[0].Set, resources.Slots[0].Binding));
        var samplers = model.Resources.Single(x => x.Name == "SamplerDescriptorHeap");
        Assert.Equal((0u, 1u), (samplers.Slots[0].Set, samplers.Slots[0].Binding));
        Assert.Equal([ResourceKind.Sampler], samplers.HeapKinds);
        // Metal: one argument buffer each, after the ordinary buffer.
        Assert.Equal([0u, 1u, 2u], model.Resources.Select(x => x.Slots.Single(s => s.Target == CompileTarget.Msl).Binding).ToArray());
        var entry = model.EntryPoints.Single();
        Assert.Equal(DescriptorHeaps.Resource | DescriptorHeaps.Sampler, entry.DescriptorHeaps);
        Assert.Equal(DeviceFeatures.RuntimeDescriptorArray | DeviceFeatures.NonUniformIndexing | DeviceFeatures.MutableDescriptorType, entry.RequiredFeatures);
        Assert.Equal(["Out", "ResourceDescriptorHeap", "SamplerDescriptorHeap"], entry.UsedResources.ToArray());
    }

    [Fact]
    public void ReflectionFileRoundTripsHeapsAndUnboundedArrays()
    {
        foreach (string text in new[] { Textures, Heap })
        {
            var model = Compile(text).Reflection!;
            string written = ReflectionFormat.Write(model);
            Assert.True(ReflectionFormat.TryRead(written, out var back, out string? error), error);
            Assert.Equal(written, ReflectionFormat.Write(back!));
        }

        string file = ReflectionFormat.Write(Compile(Heap).Reflection!);
        Assert.Contains("heap=Resource heapkinds=SampledTexture,ReadOnlyStorageBuffer", file, StringComparison.Ordinal);
        Assert.Contains("heaps bits=3", file, StringComparison.Ordinal);
    }

    [Fact]
    public void ShadersWithoutBindlessResourcesKeepTheirReflectionBytes()
    {
        string file = ReflectionFormat.Write(Compile("Texture2D<float4> T[2];\nSamplerState S;\n[shader(\"pixel\")] float4 PSMain() : SV_Target { return T[1].Sample(S, 0.5); }\n").Reflection!);
        Assert.DoesNotContain("heap", file, StringComparison.Ordinal);
        Assert.DoesNotContain("features", file, StringComparison.Ordinal);
    }

    // ---- front end ----

    [Fact]
    public void HeapNeedsAResourceTypeToReadAs()
    {
        var e = Errors(Compile("[shader(\"compute\")] [numthreads(1,1,1)] void CSMain() { float4 c = ResourceDescriptorHeap[0].Load(0); }\n"));
        Assert.Single(e);
        Assert.StartsWith("1,", e[0], StringComparison.Ordinal);
        Assert.Contains("OSH0336", e[0], StringComparison.Ordinal);
        Assert.Contains("Texture2D<float4> t = ResourceDescriptorHeap[i]", e[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ASamplerCannotComeFromTheResourceHeap()
    {
        var e = Errors(Compile("[shader(\"compute\")] [numthreads(1,1,1)] void CSMain() { SamplerState s = ResourceDescriptorHeap[0]; }\n"));
        const string source = "[shader(\"compute\")] [numthreads(1,1,1)] void CSMain() { SamplerState s = ResourceDescriptorHeap[0]; }\n";
        int column = source.IndexOf("ResourceDescriptorHeap", StringComparison.Ordinal) + 1;
        Assert.Equal(["1," + column.ToString(System.Globalization.CultureInfo.InvariantCulture) + " OSH0336: Cannot use ResourceDescriptorHeap[...] as 'SamplerState': it holds a texture or buffer type. Samplers live in SamplerDescriptorHeap; use that for sampler variables."], e);
    }

    [Fact]
    public void ATextureCannotComeFromTheSamplerHeap()
    {
        var e = Errors(Compile("[shader(\"compute\")] [numthreads(1,1,1)] void CSMain() { Texture2D<float4> t = SamplerDescriptorHeap[0]; }\n"));
        Assert.Single(e);
        Assert.Contains("OSH0336", e[0], StringComparison.Ordinal);
        Assert.Contains("it holds a sampler", e[0].Replace("a SamplerState or SamplerComparisonState", "a sampler"), StringComparison.Ordinal);
    }

    [Fact]
    public void AppendBuffersCannotComeFromTheHeap()
    {
        var e = Errors(Compile("[shader(\"compute\")] [numthreads(1,1,1)] void CSMain() { AppendStructuredBuffer<float> a = ResourceDescriptorHeap[0]; }\n"));
        Assert.Single(e);
        Assert.Contains("OSH0350", e[0], StringComparison.Ordinal);
        Assert.Contains("hidden counter", e[0], StringComparison.Ordinal);
    }

    [Fact]
    public void HeapIndexMustBeAnInteger()
    {
        var e = Errors(Compile("[shader(\"compute\")] [numthreads(1,1,1)] void CSMain() { Texture2D<float4> t = ResourceDescriptorHeap[1.5]; }\n"));
        Assert.Single(e);
        Assert.Contains("must be an integer", e[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AUserVariableNamedLikeTheHeapWins()
    {
        var r = Compile("StructuredBuffer<uint> ResourceDescriptorHeap[4];\nRWStructuredBuffer<uint> O;\n[numthreads(1,1,1)] void CSMain() { O[0] = ResourceDescriptorHeap[2][0]; }\n");
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        Assert.Equal(4u, r.Reflection!.Resources.Single(x => x.Name == "ResourceDescriptorHeap").Count);
    }

    [Fact]
    public void HeapBindingsFollowTheOtherResources()
    {
        // Binding 0 of set 0 is taken, so the heap moves to 1 (DXC's rule: the first unused binding).
        var r = Compile("[[vk::binding(0, 0)]] StructuredBuffer<uint> Taken;\n[[vk::binding(1, 0)]] RWStructuredBuffer<uint> Out;\n" +
            "[shader(\"compute\")] [numthreads(1,1,1)] void CSMain() { StructuredBuffer<uint> b = ResourceDescriptorHeap[3]; Out[0] = b[0] + Taken[0]; }\n");
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        var heap = r.Reflection!.Resources.Single(x => x.Heap == DescriptorHeaps.Resource);
        Assert.Equal((0u, 2u), (heap.Slots[0].Set, heap.Slots[0].Binding));
    }

    [Fact]
    public void OnlyTheSamplerHeapTakesTheFirstFreeBinding()
    {
        var r = Compile("RWStructuredBuffer<float4> Out : register(u0);\n[numthreads(1,1,1)] void CSMain() { SamplerState s = SamplerDescriptorHeap[1]; Texture2D<float4> t = ResourceDescriptorHeap[0]; Out[0] = t.SampleLevel(s, 0.5, 0); }\n");
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        Assert.Equal(["Out", "ResourceDescriptorHeap", "SamplerDescriptorHeap"], r.Reflection!.Resources.Select(x => x.Name).ToArray());
        Assert.Equal([0u, 1u, 2u], r.Reflection.Resources.Select(x => x.Slots[0].Binding).ToArray());
    }

    [Fact]
    public void HeapBindingsCanBeChosen()
    {
        var options = new CompileOptions { ResourceHeapBinding = new HeapBinding(2, 7), SamplerHeapBinding = new HeapBinding(2, 9) };
        var r = ShaderCompiler.Compile(Heap, "t.hlsl", options, TestContext.Current.CancellationToken);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        var slots = r.Reflection!.Resources.Where(x => x.Heap != DescriptorHeaps.None).Select(x => (x.Slots[0].Set, x.Slots[0].Binding)).ToArray();
        Assert.Equal([(2u, 7u), (2u, 9u)], slots);
    }

    [Fact]
    public void HeapBindingThatCollidesIsAnError()
    {
        var options = new CompileOptions { ResourceHeapBinding = new HeapBinding(1, 0) };
        var e = Errors(ShaderCompiler.Compile(Heap, "t.hlsl", options, TestContext.Current.CancellationToken));
        Assert.Single(e);
        Assert.Contains("OSH0330", e[0], StringComparison.Ordinal);
        Assert.Contains("-fvk-bind-resource-heap", e[0], StringComparison.Ordinal);
    }

    [Fact]
    public void OutputIsDeterministic()
    {
        foreach (string text in new[] { Textures, Heap })
        {
            var a = Compile(text, CompileTarget.SpirV, CompileTarget.Msl);
            var b = Compile(text, CompileTarget.SpirV, CompileTarget.Msl);
            Assert.Equal(a.Blobs.Count, b.Blobs.Count);
            for (int i = 0; i < a.Blobs.Count; i++)
            {
                Assert.True(a.Blobs[i].Data.SequenceEqual(b.Blobs[i].Data), a.Blobs[i].Target + " blob " + i);
            }

            Assert.Equal(ReflectionFormat.Write(a.Reflection!), ReflectionFormat.Write(b.Reflection!));
        }
    }

    // ---- WGSL and Metal ----

    [Fact]
    public void WgslNamesTheLineOfTheFirstBindlessUse()
    {
        var e = Errors(Compile(Heap, CompileTarget.Wgsl));
        Assert.Equal(2, e.Count(x => x.Contains("OSH5202", StringComparison.Ordinal)));
                Assert.Equal("4,25", Regex.Match(e.First(x => x.Contains("ResourceDescriptorHeap is not", StringComparison.Ordinal)), @"^\d+,\d+").Value);
        Assert.Equal("5,20", Regex.Match(e.First(x => x.Contains("SamplerDescriptorHeap is not", StringComparison.Ordinal)), @"^\d+,\d+").Value);
    }

    [Fact]
    public void WgslRefusesAnUnboundedArrayAtItsDeclaration()
    {
        var e = Errors(Compile(Textures, CompileTarget.Wgsl));
        Assert.Equal(2, e.Length);
        Assert.StartsWith("1,41 OSH5202: The unbounded resource array 'Tex'", e[0], StringComparison.Ordinal);
        Assert.StartsWith("2,36 OSH5202: The unbounded resource array 'Smp'", e[1], StringComparison.Ordinal);
    }

    [Fact]
    public void WgslIsOnlyAnErrorWhenItIsATarget()
    {
        Assert.True(Compile(Heap, CompileTarget.SpirV, CompileTarget.Msl).Success);
        Assert.True(Compile(Textures).Success);
    }

    [Fact]
    public void MslBindsEachBindlessArrayAsOneArgumentBuffer()
    {
        var r = Compile(Textures, CompileTarget.Msl);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        string msl = r.Find("PSMain", CompileTarget.Msl)!.AsText();
        Assert.Contains("struct Tex_entry\n{\n    texture2d<float, access::sample> v;\n};", msl, StringComparison.Ordinal);
        Assert.Contains("constant Tex_entry* Tex [[buffer(0)]]", msl, StringComparison.Ordinal);
        Assert.Contains("constant Smp_entry* Smp [[buffer(1)]]", msl, StringComparison.Ordinal);
        Assert.Contains("Tex[", msl, StringComparison.Ordinal);
        Assert.Equal([0u, 1u], r.Reflection!.Resources.Select(x => x.Slots.Single().Binding).ToArray());
    }

    [Fact]
    public void MslViewsOneHeapAsEachResourceTypeItIsReadAs()
    {
        string msl = Compile(Heap, CompileTarget.Msl).Find("CSMain", CompileTarget.Msl)!.AsText();
        Assert.Contains("constant ResourceDescriptorHeap_entry* ResourceDescriptorHeap [[buffer(1)]]", msl, StringComparison.Ordinal);
        Assert.Contains("constant SamplerDescriptorHeap_entry* SamplerDescriptorHeap [[buffer(2)]]", msl, StringComparison.Ordinal);
        Assert.Contains("reinterpret_cast<constant ResourceDescriptorHeap_0_entry*>(ResourceDescriptorHeap)", msl, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryShadersKeepTheirMslAndWgslBytes()
    {
        const string text = "Texture2D<float4> T[3];\nSamplerState S;\nStructuredBuffer<uint> B;\n[shader(\"pixel\")] float4 PSMain(float2 uv : UV) : SV_Target { return T[int(uv.x) & 1].Sample(S, uv) + B[0]; }\n";
        var r = Compile(text, CompileTarget.Msl);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        string msl = r.Find("PSMain", CompileTarget.Msl)!.AsText();
        Assert.Contains("array<texture2d<float, access::sample>, 3> T [[texture(0)]]", msl, StringComparison.Ordinal);
        Assert.DoesNotContain("_entry", msl, StringComparison.Ordinal);
    }

    // ---- layouts ----

    private static PipelineLayout PipelineOf(string text, params string[] entries)
    {
        var r = Compile(text, CompileTarget.SpirV, CompileTarget.Msl);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        var layouts = new List<EntryLayout>();
        foreach (string e in entries)
        {
            Assert.True(EntryLayout.TryCreate(r.Reflection!, e, out var l, out var error), error?.Message);
            layouts.Add(l!);
        }

        Assert.True(PipelineLayout.TryCreate(layouts.ToArray(), out var p, out var perror), perror?.Message);
        return p!;
    }

    [Fact]
    public void VulkanLayoutHasAVariableCountPartiallyBoundBinding()
    {
        var info = PipelineOf(Textures, "PSMain").ToVulkan();
        Assert.Equal(3, info.SetLayouts.Count);
        var tex = info.SetLayouts[1].Bindings.Single();
        Assert.Equal(DescriptorType.SampledImage, tex.DescriptorType);
        Assert.Equal(4096u, tex.DescriptorCount);
        Assert.True(tex.IsUnbounded);
        Assert.True(tex.IsVariableCount);
        Assert.Equal(DescriptorBindingFlags.PartiallyBound | DescriptorBindingFlags.VariableDescriptorCount, tex.BindingFlags);
        Assert.Equal(DescriptorSetLayoutCreateFlags.None, info.SetLayouts[1].CreateFlags);
        Assert.Equal(DescriptorType.Sampler, info.SetLayouts[2].Bindings.Single().DescriptorType);
        Assert.True(info.SetLayouts[1].HasBindingFlags);
        Assert.True(info.SetLayouts[1].HasVariableCount);
        Assert.False(info.SetLayouts[0].HasVariableCount);
        Assert.False(info.SetLayouts[0].HasBindingFlags);
        Assert.Equal(DeviceFeatures.RuntimeDescriptorArray | DeviceFeatures.NonUniformIndexing, info.RequiredFeatures);
    }

    [Fact]
    public void VulkanLayoutOptionsChooseCountsAndFlags()
    {
        var pipeline = PipelineOf(Textures, "PSMain");
        var info = pipeline.ToVulkan(new VulkanLayoutOptions
        {
            UnboundedMaxCount = 100,
            UnboundedCounts = new Dictionary<(uint, uint), uint> { [(2, 0)] = 16 },
            UpdateAfterBind = true,
            PartiallyBound = false,
            VariableDescriptorCount = false,
        });
        var tex = info.SetLayouts[1].Bindings.Single();
        Assert.Equal(100u, tex.DescriptorCount);
        Assert.Equal(DescriptorBindingFlags.UpdateAfterBind, tex.BindingFlags);
        Assert.Equal(16u, info.SetLayouts[2].Bindings.Single().DescriptorCount);
        Assert.Equal(DescriptorSetLayoutCreateFlags.UpdateAfterBindPool, info.SetLayouts[1].CreateFlags);
    }

    [Fact]
    public void OnlyTheHighestBindingOfASetIsVariableCount()
    {
        var info = PipelineOf(Heap, "CSMain").ToVulkan();
        var set0 = info.SetLayouts[0].Bindings;
        Assert.Equal(2, set0.Count);
        // ResourceDescriptorHeap is binding 0 and holds textures and buffers: a mutable descriptor of fixed maximum size.
        Assert.Equal(DescriptorType.Mutable, set0[0].DescriptorType);
        Assert.Equal([DescriptorType.SampledImage, DescriptorType.StorageBuffer], set0[0].MutableTypes.ToArray());
        Assert.False(set0[0].IsVariableCount);
        Assert.Equal(DescriptorBindingFlags.PartiallyBound, set0[0].BindingFlags);
        Assert.Equal(DescriptorType.Sampler, set0[1].DescriptorType);
        Assert.True(set0[1].IsVariableCount);
        Assert.Equal(DescriptorType.StorageBuffer, info.SetLayouts[1].Bindings.Single().DescriptorType);
        Assert.Equal(DescriptorBindingFlags.None, info.SetLayouts[1].Bindings.Single().BindingFlags);
        Assert.True(info.RequiredFeatures.HasFlag(DeviceFeatures.MutableDescriptorType));
    }

    [Fact]
    public void MetalLayoutIsOneArgumentBufferPerBindlessBinding()
    {
        var pipeline = PipelineOf(Heap, "CSMain");
        Assert.True(pipeline.TryToMetal(out var metal, out var error), error?.Message);
        var args = metal!.ForStage(ShaderStage.Compute)!.Arguments;
        Assert.Equal(3, args.Count);
        Assert.All(args, a => Assert.Equal(ArgumentKind.Buffer, a.Kind));
        var resources = args.Single(a => a.Name == "ResourceDescriptorHeap");
        Assert.True(resources.IsArgumentBuffer);
        Assert.Equal(1u, resources.Index);
        Assert.Equal(1u, resources.Count);
        Assert.Equal(8u, resources.DescriptorStride);
        Assert.Equal(DescriptorHeaps.Resource, resources.Heap);
        Assert.Equal([ResourceKind.SampledTexture, ResourceKind.ReadOnlyStorageBuffer], resources.ElementKinds.ToArray());
        Assert.Equal(MetalDescriptorEncoding.TextureId, MetalArgument.EncodingOf(ResourceKind.SampledTexture));
        Assert.Equal(MetalDescriptorEncoding.BufferAddress, MetalArgument.EncodingOf(ResourceKind.ReadOnlyStorageBuffer));
        Assert.Equal(MetalDescriptorEncoding.SamplerId, MetalArgument.EncodingOf(ResourceKind.Sampler));
        Assert.False(args.Single(a => a.Name == "Out").IsArgumentBuffer);
    }

    [Fact]
    public void WebGpuLayoutRefusesBindlessBindings()
    {
        var pipeline = PipelineOf(Textures, "PSMain");
        Assert.False(pipeline.TryToWebGpu(out var layout, out var error));
        Assert.Null(layout);
        Assert.Equal(LayoutErrorKind.Unsupported, error!.Kind);
        Assert.Contains("'Tex' is an unbounded resource array; WebGPU has no binding arrays", error.Message, StringComparison.Ordinal);
        var heap = PipelineOf(Heap, "CSMain");
        Assert.False(heap.TryToWebGpu(out _, out var heapError));
        Assert.Contains("descriptor heap", heapError!.Message, StringComparison.Ordinal);
    }

    // ---- DXC ----

    [GeneratedRegex(@"OpCapability (\w+)")]
    private static partial Regex CapabilityLine();

    [GeneratedRegex(@"OpExtension ""([^""]+)""")]
    private static partial Regex ExtensionLine();

    [GeneratedRegex(@"OpDecorate %(\w+) (DescriptorSet|Binding) (\d+)")]
    private static partial Regex SlotLine();

    [GeneratedRegex(@"OpDecorate (%\w+) NonUniform")]
    private static partial Regex NonUniformLine();

    private sealed record Facts(string[] Capabilities, string[] Extensions, string[] Slots, string[] NonUniformOps, int RuntimeArrays);

    private static Facts FactsOf(string disassembly)
    {
        var ops = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in NonUniformLine().Matches(disassembly))
        {
            var def = Regex.Match(disassembly, Regex.Escape(m.Groups[1].Value) + @" = (Op\w+)");
            ops.Add(def.Success ? def.Groups[1].Value : "?");
        }

        var slots = SlotLine().Matches(disassembly).Select(m => m.Groups[2].Value + " " + m.Groups[3].Value + " " + m.Groups[1].Value).ToList();
        // Compare the (set, binding) pairs, not the variable names: pair each variable's two decorations.
        var byName = SlotLine().Matches(disassembly).GroupBy(m => m.Groups[1].Value)
            .Select(g => g.OrderBy(m => m.Groups[2].Value, StringComparer.Ordinal).Select(m => m.Groups[3].Value).ToArray())
            .Select(a => "binding " + a[0] + " set " + a[1]).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
        _ = slots;
        return new Facts(
            CapabilityLine().Matches(disassembly).Select(m => m.Groups[1].Value).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            ExtensionLine().Matches(disassembly).Select(m => m.Groups[1].Value).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            byName,
            ops.ToArray(),
            Regex.Count(disassembly, "= OpTypeRuntimeArray"));
    }

    // Capabilities we declare that DXC does not: Vulkan needs the non-uniform buffer-indexing capability for a buffer array indexed with a
    // non-uniform value, and storage images without a format need the without-format capability (DXC picks a format from the element type).
    private static readonly string[] OursOnlyAllowed =
    [
        "StorageBufferArrayNonUniformIndexing", "UniformBufferArrayNonUniformIndexing", "StorageBufferArrayDynamicIndexing",
        "StorageImageReadWithoutFormat", "StorageImageWriteWithoutFormat",
    ];

    [Theory]
    [InlineData("lang/bindless_textures.hlsl", "PSMain", "ps_6_0")]
    [InlineData("lang/bindless_buffers.hlsl", "CSMain", "cs_6_0")]
    [InlineData("lang/bindless_heap.hlsl", "CSMain", "cs_6_6")]
    public void DescriptorIndexingMatchesDxc(string rel, string entry, string profile)
    {
        string? dxc = Oracles.Find("dxc");
        string? dis = Oracles.Find("spirv-dis");
        string? val = Oracles.Find("spirv-val");
        Assert.SkipUnless(dxc is not null && dis is not null && val is not null, "dxc, spirv-dis or spirv-val is not installed");
        string path = Path.Combine(CorpusFiles.Root(), rel);
        string dir = Path.Combine(Path.GetTempPath(), "osh-bindless-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string theirs = Path.Combine(dir, "ref.spv");
            var (code, output) = Oracles.Run(dxc!, ["-spirv", "-fspv-target-env=vulkan1.2", "-T", profile, "-E", entry, path, "-Fo", theirs], dir);
            Assert.True(code == 0, output);
            var r = ShaderCompiler.Compile(File.ReadAllText(path), path, new CompileOptions { EntryPoints = [new EntryPointRequest(entry, profile[0] == 'v' ? ShaderStage.Vertex : profile[0] == 'p' ? ShaderStage.Pixel : ShaderStage.Compute)] }, TestContext.Current.CancellationToken);
            Assert.True(r.Success, string.Join("\n", r.Diagnostics));
            string ours = Path.Combine(dir, "ours.spv");
            File.WriteAllBytes(ours, r.Find(entry, CompileTarget.SpirV)!.ToArray());
            var (validCode, validOutput) = Oracles.Run(val!, ["--target-env", "vulkan1.2", ours], dir);
            Assert.True(validCode == 0, validOutput);

            var a = FactsOf(Oracles.Run(dis!, [theirs], dir).Output);
            var b = FactsOf(Oracles.Run(dis!, [ours], dir).Output);
            Assert.Equal(a.Extensions, b.Extensions);
            Assert.Equal(a.Slots, b.Slots);
            Assert.Empty(a.Capabilities.Except(b.Capabilities));
            Assert.Empty(b.Capabilities.Except(a.Capabilities).Except(OursOnlyAllowed));
            // DXC also decorates arithmetic on values it loaded through a non-uniform descriptor; the decoration belongs on the descriptor
            // access, which is where both compilers put it.
            Assert.Empty(a.NonUniformOps.Except(b.NonUniformOps).Except(["OpFAdd", "OpIAdd"]));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void HeapBindingOptionsMatchDxc()
    {
        string? dxc = Oracles.Find("dxc");
        string? dis = Oracles.Find("spirv-dis");
        Assert.SkipUnless(dxc is not null && dis is not null, "dxc or spirv-dis is not installed");
        string path = Path.Combine(CorpusFiles.Root(), "lang/bindless_heap.hlsl");
        string dir = Path.Combine(Path.GetTempPath(), "osh-bindless-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string theirs = Path.Combine(dir, "ref.spv");
            var (code, output) = Oracles.Run(dxc!, ["-spirv", "-fspv-target-env=vulkan1.2", "-T", "cs_6_6", "-E", "CSMain", "-fvk-bind-resource-heap", "5", "3", "-fvk-bind-sampler-heap", "6", "3", path, "-Fo", theirs], dir);
            Assert.True(code == 0, output);
            var r = ShaderCompiler.Compile(File.ReadAllText(path), path, new CompileOptions { ResourceHeapBinding = new HeapBinding(3, 5), SamplerHeapBinding = new HeapBinding(3, 6) }, TestContext.Current.CancellationToken);
            Assert.True(r.Success, string.Join("\n", r.Diagnostics));
            string ours = Path.Combine(dir, "ours.spv");
            File.WriteAllBytes(ours, r.Find("CSMain", CompileTarget.SpirV)!.ToArray());
            var a = FactsOf(Oracles.Run(dis!, [theirs], dir).Output);
            var b = FactsOf(Oracles.Run(dis!, [ours], dir).Output);
            Assert.Equal(a.Slots, b.Slots);
            Assert.Contains("binding 5 set 3", b.Slots);
            Assert.Contains("binding 6 set 3", b.Slots);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---- binding descriptions and unions ----

    [Fact]
    public void ToBindingsDescribesUnboundedArrays()
    {
        var b = PipelineOf(Textures, "PSMain").ToBindings();
        var tex = b.Single(x => x.Name == "Tex");
        Assert.Equal(BindingType.SampledTexture, tex.Type);
        Assert.Equal(0u, tex.Count);
        Assert.True(tex.IsUnbounded);
        Assert.True(tex.PartiallyBound);
        Assert.True(tex.HasVariableCount);
        Assert.Equal(DescriptorHeaps.None, tex.Heap);
        Assert.Empty(tex.HeapKinds!);
        Assert.Equal(TextureDimension.D2, tex.Dimension);
        var smp = b.Single(x => x.Name == "Smp");
        Assert.Equal(BindingType.Sampler, smp.Type);
        Assert.True(smp.IsUnbounded);
    }

    [Fact]
    public void ToBindingsDescribesHeapsAndOnlyTheHighestBindingTakesAVariableCount()
    {
        var b = PipelineOf(Heap, "CSMain").ToBindings();
        var res = b.Single(x => x.Name == "ResourceDescriptorHeap");
        Assert.Equal(DescriptorHeaps.Resource, res.Heap);
        Assert.Equal([BindingType.SampledTexture, BindingType.ReadOnlyStorageBuffer], res.HeapKinds!.ToArray());
        Assert.True(res.IsUnbounded);
        Assert.Equal(0u, res.Count);
        Assert.True(res.PartiallyBound);
        Assert.False(res.HasVariableCount);
        var smp = b.Single(x => x.Name == "SamplerDescriptorHeap");
        Assert.Equal(DescriptorHeaps.Sampler, smp.Heap);
        Assert.True(smp.HasVariableCount);
        var plain = b.Single(x => x.Name == "Out");
        Assert.False(plain.IsUnbounded);
        Assert.False(plain.PartiallyBound);
        Assert.Equal(1u, plain.Count);
    }

    private static List<EntryLayout> EntryLayouts(string text, params string[] entries)
    {
        var r = Compile(text, CompileTarget.SpirV);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        var list = new List<EntryLayout>();
        foreach (string e in entries)
        {
            Assert.True(EntryLayout.TryCreate(r.Reflection!, e, out var l, out var error), error?.Message);
            list.Add(l!);
        }

        return list;
    }

    private const string UnionBody =
        "[shader(\"compute\")] [numthreads(1,1,1)] void A() { float4 c = T[NonUniformResourceIndex(1)].Load(0); }\n" +
        "[shader(\"compute\")] [numthreads(1,1,1)] void B() { float4 c = T[NonUniformResourceIndex(2)].Load(0); }\n";

    [Fact]
    public void UnionMergesEqualUnboundedSlots()
    {
        var entries = EntryLayouts("[[vk::binding(0, 0)]] Texture2D<float4> T[];\n" + UnionBody, "A", "B");
        Assert.True(PipelineLayout.TryCreateUnion(entries.ToArray(), out var union, out var error), error?.Message);
        var t = union!.ToBindings().Single();
        Assert.True(t.IsUnbounded);
        Assert.Equal(0u, t.Count);
        Assert.True(t.HasVariableCount);
        Assert.Equal(ShaderStages.Compute, t.Stages);
    }

    [Fact]
    public void UnionRefusesAnUnboundedSlotAgainstASizedOneEvenWithLenientOptions()
    {
        var unbounded = EntryLayouts("[[vk::binding(0, 0)]] Texture2D<float4> T[];\n" + UnionBody, "A")[0];
        var sized = EntryLayouts("[[vk::binding(0, 0)]] Texture2D<float4> T[4];\n" + UnionBody, "B")[0];
        var options = new UnionOptions { IgnoreTextureDimension = true, MergeStorageAccess = true };
        Assert.False(PipelineLayout.TryCreateUnion([unbounded, sized], options, out _, out var error));
        Assert.Equal(LayoutErrorKind.ResourceConflict, error!.Kind);
    }

    [Fact]
    public void UnionMergesHeapsAndRefusesHeapAgainstDeclaredResource()
    {
        const string a = "[shader(\"compute\")] [numthreads(1,1,1)] void A() { Texture2D<float4> t = ResourceDescriptorHeap[0]; float4 c = t.Load(0); }\n";
        const string b = "[shader(\"compute\")] [numthreads(1,1,1)] void B() { Texture2D<float4> t = ResourceDescriptorHeap[1]; float4 c = t.Load(0); }\n";
        var both = EntryLayouts(a + b, "A", "B");
        Assert.True(PipelineLayout.TryCreateUnion(both.ToArray(), out var union, out var error), error?.Message);
        var h = union!.ToBindings().Single();
        Assert.Equal(DescriptorHeaps.Resource, h.Heap);
        // A heap that is also read as a buffer in the other entry point has other kinds: not the same slot contents.
        const string c = "[shader(\"compute\")] [numthreads(1,1,1)] void C() { StructuredBuffer<uint> t = ResourceDescriptorHeap[1]; uint c = t[0]; }\n";
        var mixed = new[] { both[0], EntryLayouts(c, "C")[0] };
        Assert.False(PipelineLayout.TryCreateUnion(mixed, new UnionOptions { IgnoreTextureDimension = true, MergeStorageAccess = true }, out _, out error));
        Assert.Equal(LayoutErrorKind.ResourceConflict, error!.Kind);
    }
}
