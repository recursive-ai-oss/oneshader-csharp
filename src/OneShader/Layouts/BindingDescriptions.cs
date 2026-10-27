using System;
using System.Collections.Generic;
using OneShader.Reflection;
using OneShader.Types;

namespace OneShader.Layouts;

/// <summary>
/// What may differ between the declarations that share one slot of a union layout
/// (<see cref="PipelineLayout.TryCreateUnion(ReadOnlySpan{EntryLayout}, UnionOptions?, out PipelineLayout?, out LayoutError?)"/>).
/// Everything is off by default, which gives the strict rules of the overload without options.
/// </summary>
public sealed class UnionOptions
{
    /// <summary>
    /// Textures (or storage textures) at one slot may differ in dimension, for example a <c>Texture2D</c> in one entry point and a
    /// <c>TextureCube</c> in another. Kind, count, texel type and storage format must still agree. Vulkan has one descriptor type for all of
    /// them and Metal binds by index, so only WebGPU is affected: its <c>viewDimension</c> must match the shader, so
    /// <see cref="PipelineLayout.TryToWebGpu"/> refuses such a slot unless <c>WebGpuLayoutOptions.TextureDimensions</c> picks the dimension
    /// (pipelines whose shader declares another dimension then need a layout with their own pick). <see cref="BindingDescription.Dimensions"/>
    /// lists every dimension the slot saw. Default: false.
    /// </summary>
    public bool IgnoreTextureDimension { get; init; }

    /// <summary>
    /// A read-only buffer (<c>StructuredBuffer</c>, <c>ByteAddressBuffer</c>) and a read-write buffer of the same family
    /// (<c>RWStructuredBuffer</c>/append/consume, <c>RWByteAddressBuffer</c>) may share a slot; the slot becomes read-write. Vulkan has one
    /// descriptor type for both. WebGPU's <c>storage</c> binding is only valid for shaders that declare the buffer read-write, so
    /// <see cref="PipelineLayout.TryToWebGpu"/> refuses such a slot unless <c>WebGpuLayoutOptions.ReadOnlyStorageBuffers</c> names it
    /// (declaring the buffer read-write in every entry point is the other fix). Default: false.
    /// </summary>
    public bool MergeStorageAccess { get; init; }
}

/// <summary>The kind of a binding, target-neutral. Read-only and read-write storage are different kinds because WebGPU tells them apart.</summary>
public enum BindingType
{
    /// <summary>A constant (uniform) buffer.</summary>
    UniformBuffer = 0,

    /// <summary>A <c>StructuredBuffer</c> or <c>ByteAddressBuffer</c>.</summary>
    ReadOnlyStorageBuffer = 1,

    /// <summary>A <c>RWStructuredBuffer</c>, append/consume buffer or <c>RWByteAddressBuffer</c>.</summary>
    StorageBuffer = 2,

    /// <summary>A sampled texture (<c>Texture2D</c> and the other read-only textures).</summary>
    SampledTexture = 3,

    /// <summary>A read-write texture (<c>RWTexture2D</c> and friends).</summary>
    StorageTexture = 4,

    /// <summary>A sampler; <see cref="BindingDescription.IsComparison"/> says whether it is a <c>SamplerComparisonState</c>.</summary>
    Sampler = 5,

    /// <summary>A read-only typed buffer, <c>Buffer&lt;T&gt;</c>.</summary>
    UniformTexelBuffer = 6,

    /// <summary>A read-write typed buffer, <c>RWBuffer&lt;T&gt;</c>.</summary>
    StorageTexelBuffer = 7,
}

/// <summary>The dimension of a texture binding.</summary>
public enum TextureDimension
{
    /// <summary>Not a texture.</summary>
    None = 0,

    /// <summary>One-dimensional.</summary>
    D1 = 1,

    /// <summary>One-dimensional array.</summary>
    D1Array = 2,

    /// <summary>Two-dimensional.</summary>
    D2 = 3,

    /// <summary>Two-dimensional array.</summary>
    D2Array = 4,

    /// <summary>Two-dimensional multisampled.</summary>
    D2Multisampled = 5,

    /// <summary>Two-dimensional multisampled array.</summary>
    D2MultisampledArray = 6,

    /// <summary>Three-dimensional.</summary>
    D3 = 7,

    /// <summary>Cube.</summary>
    Cube = 8,

    /// <summary>Cube array.</summary>
    CubeArray = 9,
}

/// <summary>The component class textures return, which decides the WebGPU sample type.</summary>
public enum TextureSampleKind
{
    /// <summary>Not a texture.</summary>
    None = 0,

    /// <summary>Floating point (or normalized) texels.</summary>
    Float = 1,

    /// <summary>Signed integer texels.</summary>
    Sint = 2,

    /// <summary>Unsigned integer texels.</summary>
    Uint = 3,

    /// <summary>A depth texture; only when named in <see cref="BindingOptions.DepthTextures"/>, since HLSL does not distinguish it.</summary>
    Depth = 4,
}

/// <summary>Host choices for <see cref="PipelineLayout.ToBindings"/>. Nothing is set by default.</summary>
public sealed class BindingOptions
{
    /// <summary>Names of float textures that hold depth: their <see cref="BindingDescription.SampleKind"/> is <see cref="TextureSampleKind.Depth"/>.</summary>
    public IReadOnlyCollection<string> DepthTextures { get; init; } = [];

    /// <summary>Names of constant or storage buffers bound with a dynamic offset.</summary>
    public IReadOnlyCollection<string> DynamicBuffers { get; init; } = [];

    /// <summary>The same by <c>(set, binding)</c>.</summary>
    public IReadOnlyCollection<(uint Set, uint Binding)> DynamicBindings { get; init; } = [];

    /// <summary>Extra stages OR-ed into the visibility of every binding of a set, by set number.</summary>
    public IReadOnlyDictionary<uint, ShaderStages> WidenSets { get; init; } = new Dictionary<uint, ShaderStages>();

    /// <summary>Extra stages for single bindings, by <c>(set, binding)</c>.</summary>
    public IReadOnlyDictionary<(uint Set, uint Binding), ShaderStages> WidenBindings { get; init; } = new Dictionary<(uint Set, uint Binding), ShaderStages>();
}

/// <summary>
/// One binding of a pipeline layout described without any graphics API's vocabulary: what a host needs to create a descriptor set layout,
/// a bind group layout or a Metal argument table. Unlike the Vulkan description it tells read-only from read-write storage, and it keeps the
/// texture dimension, sample type and comparison flag.
/// </summary>
/// <param name="Set">Descriptor set (Vulkan) / bind group (WebGPU).</param>
/// <param name="Binding">Binding number in the set.</param>
/// <param name="Name">The HLSL name (of the first entry point that declared the slot, in a union).</param>
/// <param name="Type">The binding kind.</param>
/// <param name="Count">Array size; 1 for a single resource; 0 for an unbounded array or a descriptor heap (see <paramref name="IsUnbounded"/>).</param>
/// <param name="Stages">The stages that use it, including widening from the options.</param>
/// <param name="IsDynamic">True when bound with a dynamic offset.</param>
/// <param name="Dimension">Textures: the dimension (the first one when <see cref="Dimensions"/> has several); otherwise <see cref="TextureDimension.None"/>.</param>
/// <param name="Dimensions">Textures: every dimension the entry points declare at this slot, in the order met; several only after <see cref="UnionOptions.IgnoreTextureDimension"/>.</param>
/// <param name="SampleKind">Textures: the texel class; otherwise <see cref="TextureSampleKind.None"/>.</param>
/// <param name="IsComparison">Samplers: true for <c>SamplerComparisonState</c>.</param>
/// <param name="StorageFormat">Storage textures: the <c>[[vk::image_format]]</c> name; empty if none.</param>
/// <param name="MetalIndex">The Metal argument index in the vertex stage's, or the first stage's that uses it, table; <see cref="LayoutSlots.None"/> if Metal was not built. Per-stage indices come from <see cref="PipelineLayout.TryToMetal"/>.</param>
/// <param name="IsUnbounded">True for an unbounded resource array or a descriptor heap: the host picks the size (<see cref="Count"/> is 0).</param>
/// <param name="Heap">Which SM 6.6 descriptor heap this binding is; <see cref="DescriptorHeaps.None"/> for a declared resource.</param>
/// <param name="HeapKinds">A heap: every kind of resource the shaders read from it (a binding holding several kinds is a mutable-descriptor binding in Vulkan); otherwise empty.</param>
/// <param name="PartiallyBound">True for an unbounded binding: entries that no shader invocation reads may be left unwritten.</param>
/// <param name="HasVariableCount">True for an unbounded binding that is the highest binding of its set: only the last binding of a set can take its size from the allocation (Vulkan variable descriptor count).</param>
public sealed record BindingDescription(
    uint Set,
    uint Binding,
    string Name,
    BindingType Type,
    uint Count,
    ShaderStages Stages,
    bool IsDynamic,
    TextureDimension Dimension,
    IReadOnlyList<TextureDimension> Dimensions,
    TextureSampleKind SampleKind,
    bool IsComparison,
    string StorageFormat,
    uint MetalIndex,
    bool IsUnbounded = false,
    DescriptorHeaps Heap = DescriptorHeaps.None,
    IReadOnlyList<BindingType>? HeapKinds = null,
    bool PartiallyBound = false,
    bool HasVariableCount = false);

public sealed partial class PipelineLayout
{
    /// <summary>
    /// The bindings of the layout as target-neutral data, sorted by set and binding. Read-only versus read-write storage comes from the HLSL
    /// resource type (<c>StructuredBuffer</c> against <c>RWStructuredBuffer</c>, <c>ByteAddressBuffer</c> against its RW form); in a union
    /// built with <see cref="UnionOptions.MergeStorageAccess"/> a slot used both ways is read-write. Never fails.
    /// </summary>
    /// <param name="options">Host choices; defaults when null.</param>
    /// <returns>One description per binding.</returns>
    public IReadOnlyList<BindingDescription> ToBindings(BindingOptions? options = null)
    {
        var result = new List<BindingDescription>();
        foreach (var group in Groups)
        {
            uint highest = 0;
            foreach (var b in group.Entries)
            {
                highest = Math.Max(highest, b.Resource.Binding);
            }

            foreach (var b in group.Entries)
            {
                var r = b.Resource;
                bool dynamic = r.DynamicOffset;
                bool buffer = r.Kind is ResourceKind.ConstantBuffer or ResourceKind.ReadOnlyStorageBuffer or ResourceKind.StorageBuffer;
                var stages = r.Stages;
                if (options is not null)
                {
                    foreach (string d in options.DynamicBuffers)
                    {
                        dynamic |= d == r.Name;
                    }

                    foreach (var d in options.DynamicBindings)
                    {
                        dynamic |= d.Set == r.Set && d.Binding == r.Binding;
                    }

                    stages |= WidenedStages(r.Set, r.Binding, options.WidenSets, options.WidenBindings);
                }

                var dims = new List<TextureDimension>();
                var sample = TextureSampleKind.None;
                if (r.Kind is ResourceKind.SampledTexture or ResourceKind.StorageTexture)
                {
                    foreach (var t in b.Types)
                    {
                        var d = DimensionOf(t);
                        if (!dims.Contains(d))
                        {
                            dims.Add(d);
                        }
                    }

                    sample = r.Texel switch
                    {
                        ScalarKind.Int => TextureSampleKind.Sint,
                        ScalarKind.UInt => TextureSampleKind.Uint,
                        _ => options is not null && Contains(options.DepthTextures, r.Name) ? TextureSampleKind.Depth : TextureSampleKind.Float,
                    };
                }

                uint metal = LayoutSlots.None;
                foreach (var s in new[] { ShaderStage.Vertex, ShaderStage.Pixel, ShaderStage.Compute })
                {
                    if (metal == LayoutSlots.None)
                    {
                        metal = b.MetalIndexFor(s);
                    }
                }

                result.Add(new BindingDescription(
                    r.Set,
                    r.Binding,
                    r.Name,
                    BindingTypeOf(r.Kind),
                    r.IsUnbounded ? 0 : Math.Max(r.Count, 1),
                    stages,
                    dynamic && buffer,
                    dims.Count > 0 ? dims[0] : TextureDimension.None,
                    dims.ToArray(),
                    sample,
                    r.Kind == ResourceKind.Sampler && r.Comparison,
                    r.Kind == ResourceKind.StorageTexture ? r.ImageFormat : "",
                    metal,
                    r.IsUnbounded,
                    r.Heap,
                    HeapTypes(r),
                    r.IsUnbounded,
                    r.IsUnbounded && r.Binding == highest));
            }
        }

        return result;
    }

    private static IReadOnlyList<BindingType> HeapTypes(ResourceLayout r)
    {
        if (r.Heap == DescriptorHeaps.None)
        {
            return [];
        }

        var kinds = new List<BindingType>(r.HeapKinds.Count);
        foreach (var k in r.HeapKinds)
        {
            kinds.Add(BindingTypeOf(k));
        }

        return kinds;
    }

    private static BindingType BindingTypeOf(ResourceKind kind) => kind switch
    {
        ResourceKind.ConstantBuffer => BindingType.UniformBuffer,
        ResourceKind.ReadOnlyStorageBuffer => BindingType.ReadOnlyStorageBuffer,
        ResourceKind.StorageBuffer => BindingType.StorageBuffer,
        ResourceKind.SampledTexture => BindingType.SampledTexture,
        ResourceKind.StorageTexture => BindingType.StorageTexture,
        ResourceKind.Sampler => BindingType.Sampler,
        ResourceKind.UniformTexelBuffer => BindingType.UniformTexelBuffer,
        _ => BindingType.StorageTexelBuffer,
    };

    private static TextureDimension DimensionOf(ResourceTypeKind t) => t switch
    {
        ResourceTypeKind.Texture1D or ResourceTypeKind.RWTexture1D => TextureDimension.D1,
        ResourceTypeKind.Texture1DArray or ResourceTypeKind.RWTexture1DArray => TextureDimension.D1Array,
        ResourceTypeKind.Texture2D or ResourceTypeKind.RWTexture2D => TextureDimension.D2,
        ResourceTypeKind.Texture2DArray or ResourceTypeKind.RWTexture2DArray => TextureDimension.D2Array,
        ResourceTypeKind.Texture2DMS => TextureDimension.D2Multisampled,
        ResourceTypeKind.Texture2DMSArray => TextureDimension.D2MultisampledArray,
        ResourceTypeKind.Texture3D or ResourceTypeKind.RWTexture3D => TextureDimension.D3,
        ResourceTypeKind.TextureCube => TextureDimension.Cube,
        ResourceTypeKind.TextureCubeArray => TextureDimension.CubeArray,
        _ => TextureDimension.None,
    };
}
