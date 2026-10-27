using System;
using System.Collections.Generic;
using OneShader.Reflection;
using OneShader.Types;

namespace OneShader.Layouts.WebGpu
{
    /// <summary>The stages a binding is visible to: the bits of <c>GPUShaderStage</c>.</summary>
    [Flags]
    public enum ShaderStageVisibility : uint
    {
        /// <summary>No stage.</summary>
        None = 0,

        /// <summary><c>GPUShaderStage.VERTEX</c>.</summary>
        Vertex = 1,

        /// <summary><c>GPUShaderStage.FRAGMENT</c>.</summary>
        Fragment = 2,

        /// <summary><c>GPUShaderStage.COMPUTE</c>.</summary>
        Compute = 4,
    }

    /// <summary>Which member of <c>GPUBindGroupLayoutEntry</c> an entry sets.</summary>
    public enum BindingKind
    {
        /// <summary>The <c>buffer</c> member.</summary>
        Buffer = 0,

        /// <summary>The <c>sampler</c> member.</summary>
        Sampler = 1,

        /// <summary>The <c>texture</c> member.</summary>
        Texture = 2,

        /// <summary>The <c>storageTexture</c> member.</summary>
        StorageTexture = 3,
    }

    /// <summary><c>GPUBufferBindingType</c>.</summary>
    public enum BufferBindingType
    {
        /// <summary><c>"uniform"</c>.</summary>
        Uniform = 0,

        /// <summary><c>"storage"</c>.</summary>
        Storage = 1,

        /// <summary><c>"read-only-storage"</c>.</summary>
        ReadOnlyStorage = 2,
    }

    /// <summary><c>GPUSamplerBindingType</c>.</summary>
    public enum SamplerBindingType
    {
        /// <summary><c>"filtering"</c>.</summary>
        Filtering = 0,

        /// <summary><c>"non-filtering"</c>.</summary>
        NonFiltering = 1,

        /// <summary><c>"comparison"</c>.</summary>
        Comparison = 2,
    }

    /// <summary><c>GPUTextureSampleType</c>.</summary>
    public enum TextureSampleType
    {
        /// <summary><c>"float"</c>.</summary>
        Float = 0,

        /// <summary><c>"unfilterable-float"</c>.</summary>
        UnfilterableFloat = 1,

        /// <summary><c>"depth"</c>.</summary>
        Depth = 2,

        /// <summary><c>"sint"</c>.</summary>
        Sint = 3,

        /// <summary><c>"uint"</c>.</summary>
        Uint = 4,
    }

    /// <summary><c>GPUTextureViewDimension</c>.</summary>
    public enum TextureViewDimension
    {
        /// <summary><c>"1d"</c>.</summary>
        D1 = 0,

        /// <summary><c>"2d"</c>.</summary>
        D2 = 1,

        /// <summary><c>"2d-array"</c>.</summary>
        D2Array = 2,

        /// <summary><c>"cube"</c>.</summary>
        Cube = 3,

        /// <summary><c>"cube-array"</c>.</summary>
        CubeArray = 4,

        /// <summary><c>"3d"</c>.</summary>
        D3 = 5,
    }

    /// <summary><c>GPUStorageTextureAccess</c>.</summary>
    public enum StorageTextureAccess
    {
        /// <summary><c>"write-only"</c>.</summary>
        WriteOnly = 0,

        /// <summary><c>"read-only"</c>.</summary>
        ReadOnly = 1,

        /// <summary><c>"read-write"</c>.</summary>
        ReadWrite = 2,
    }

    /// <summary>The WebGPU spellings of the enums, as the JavaScript API takes them.</summary>
    public static class WebGpuStrings
    {
        /// <summary>The <c>GPUBufferBindingType</c> string.</summary>
        /// <param name="value">The value.</param>
        /// <returns>The string.</returns>
        public static string ToWebGpuString(this BufferBindingType value) => value switch
        {
            BufferBindingType.Storage => "storage",
            BufferBindingType.ReadOnlyStorage => "read-only-storage",
            _ => "uniform",
        };

        /// <summary>The <c>GPUSamplerBindingType</c> string.</summary>
        /// <param name="value">The value.</param>
        /// <returns>The string.</returns>
        public static string ToWebGpuString(this SamplerBindingType value) => value switch
        {
            SamplerBindingType.NonFiltering => "non-filtering",
            SamplerBindingType.Comparison => "comparison",
            _ => "filtering",
        };

        /// <summary>The <c>GPUTextureSampleType</c> string.</summary>
        /// <param name="value">The value.</param>
        /// <returns>The string.</returns>
        public static string ToWebGpuString(this TextureSampleType value) => value switch
        {
            TextureSampleType.UnfilterableFloat => "unfilterable-float",
            TextureSampleType.Depth => "depth",
            TextureSampleType.Sint => "sint",
            TextureSampleType.Uint => "uint",
            _ => "float",
        };

        /// <summary>The <c>GPUTextureViewDimension</c> string.</summary>
        /// <param name="value">The value.</param>
        /// <returns>The string.</returns>
        public static string ToWebGpuString(this TextureViewDimension value) => value switch
        {
            TextureViewDimension.D1 => "1d",
            TextureViewDimension.D2Array => "2d-array",
            TextureViewDimension.Cube => "cube",
            TextureViewDimension.CubeArray => "cube-array",
            TextureViewDimension.D3 => "3d",
            _ => "2d",
        };

        /// <summary>The <c>GPUStorageTextureAccess</c> string.</summary>
        /// <param name="value">The value.</param>
        /// <returns>The string.</returns>
        public static string ToWebGpuString(this StorageTextureAccess value) => value switch
        {
            StorageTextureAccess.ReadOnly => "read-only",
            StorageTextureAccess.ReadWrite => "read-write",
            _ => "write-only",
        };

        /// <summary>The <c>GPUVertexStepMode</c> string.</summary>
        /// <param name="value">The value.</param>
        /// <returns>The string.</returns>
        public static string ToWebGpuString(this VertexStepMode value) => value == VertexStepMode.Instance ? "instance" : "vertex";
    }

    /// <summary>
    /// One <c>GPUBindGroupLayoutEntry</c>. Only the members for <see cref="Kind"/> are meaningful; the others hold their defaults.
    /// </summary>
    /// <param name="Name">The HLSL name; not part of WebGPU.</param>
    /// <param name="Binding">The binding number.</param>
    /// <param name="Visibility">The stages that use it.</param>
    /// <param name="Kind">Which member is set.</param>
    /// <param name="BufferType">Buffers: the binding type.</param>
    /// <param name="MinBindingSize">Buffers: <c>minBindingSize</c>; 0 means unspecified, except for the emulated push-constant block.</param>
    /// <param name="SamplerType">Samplers: the binding type.</param>
    /// <param name="SampleType">Textures: the sample type.</param>
    /// <param name="ViewDimension">Textures and storage textures: the view dimension.</param>
    /// <param name="Multisampled">Textures: true for a multisampled texture.</param>
    /// <param name="Access">Storage textures: the access.</param>
    /// <param name="StorageFormat">Storage textures: the <c>GPUTextureFormat</c> string, for example <c>rgba8unorm</c>.</param>
    /// <param name="IsPushConstant">True for the uniform buffer that stands in for a push-constant block.</param>
    public sealed record BindGroupLayoutEntryInfo(
        string Name,
        uint Binding,
        ShaderStageVisibility Visibility,
        BindingKind Kind,
        BufferBindingType BufferType = BufferBindingType.Uniform,
        ulong MinBindingSize = 0,
        SamplerBindingType SamplerType = SamplerBindingType.Filtering,
        TextureSampleType SampleType = TextureSampleType.Float,
        TextureViewDimension ViewDimension = TextureViewDimension.D2,
        bool Multisampled = false,
        StorageTextureAccess Access = StorageTextureAccess.WriteOnly,
        string StorageFormat = "",
        bool IsPushConstant = false)
    {
        /// <summary>Buffers: <c>hasDynamicOffset</c>; the host passes one offset for it at every <c>setBindGroup</c>, in binding order.</summary>
        public bool HasDynamicOffset { get; init; }
    }

    /// <summary>One <c>GPUBindGroupLayoutDescriptor</c>.</summary>
    /// <param name="Group">The group index.</param>
    /// <param name="Entries">The entries, sorted by binding.</param>
    public sealed record BindGroupLayoutInfo(uint Group, IReadOnlyList<BindGroupLayoutEntryInfo> Entries);

    /// <summary>The uniform buffer that stands in for a push-constant block, which WebGPU does not have: write the pushed bytes into it before each draw.</summary>
    /// <param name="Name">The HLSL variable name.</param>
    /// <param name="Group">The bind group.</param>
    /// <param name="Binding">The binding.</param>
    /// <param name="Size">Bytes of the block.</param>
    /// <param name="BufferSize">The smallest buffer to bind: <paramref name="Size"/> rounded up to 16.</param>
    /// <param name="Visibility">The stages that read it.</param>
    public readonly record struct PushConstantEmulation(string Name, uint Group, uint Binding, uint Size, uint BufferSize, ShaderStageVisibility Visibility);

    /// <summary>The WebGPU description of a pipeline layout.</summary>
    /// <param name="Groups">One bind group layout per group from 0; the emulated push-constant buffer is an entry in its group.</param>
    /// <param name="PushConstants">The emulated push-constant blocks (usually none or one).</param>
    /// <param name="RequiredFeatures">WebGPU features the device must be created with (<c>requiredFeatures</c>): <c>subgroups</c> when the shaders use wave operations, <c>shader-f16</c> when they use 16-bit floats.</param>
    public sealed record WebGpuPipelineLayout(IReadOnlyList<BindGroupLayoutInfo> Groups, IReadOnlyList<PushConstantEmulation> PushConstants, IReadOnlyList<string>? RequiredFeatures = null);

    /// <summary>One <c>GPUVertexAttribute</c>.</summary>
    /// <param name="Format">The <c>GPUVertexFormat</c> string.</param>
    /// <param name="Offset">Byte offset.</param>
    /// <param name="ShaderLocation">The input location.</param>
    public readonly record struct VertexAttributeInfo(string Format, uint Offset, uint ShaderLocation);

    /// <summary>One <c>GPUVertexBufferLayout</c>.</summary>
    /// <param name="ArrayStride">Bytes between elements.</param>
    /// <param name="StepMode">Per vertex or per instance.</param>
    /// <param name="Attributes">The attributes.</param>
    public sealed record VertexBufferLayoutInfo(uint ArrayStride, VertexStepMode StepMode, IReadOnlyList<VertexAttributeInfo> Attributes);

    /// <summary>
    /// Choices a host makes that the shader source does not show. Reflection does not record whether a texture is used with a comparison
    /// sampler (a depth texture), how a storage texture is accessed, whether a read-write buffer is ever written, or which samplers may be non-filtering; name them here.
    /// </summary>
    public sealed class WebGpuLayoutOptions
    {
        /// <summary>Names of textures that hold depth and are sampled with a comparison sampler (<c>texture_depth_2d</c>): sample type <c>depth</c>.</summary>
        public IReadOnlyCollection<string> DepthTextures { get; init; } = [];

        /// <summary>Names of float textures bound to non-filterable formats: sample type <c>unfilterable-float</c>. Multisampled float textures are always unfilterable.</summary>
        public IReadOnlyCollection<string> UnfilterableTextures { get; init; } = [];

        /// <summary>Names of samplers used with unfilterable textures: binding type <c>non-filtering</c>.</summary>
        public IReadOnlyCollection<string> NonFilteringSamplers { get; init; } = [];

        /// <summary>
        /// Names of read-write structured or byte-address buffers that the shader never writes (WGSL declares those <c>read</c>, which needs
        /// the <c>read-only-storage</c> binding type instead of <c>storage</c>). Default: none.
        /// </summary>
        public IReadOnlyCollection<string> ReadOnlyStorageBuffers { get; init; } = [];

        /// <summary>
        /// Access for storage textures by name; default <c>write-only</c>. A texture the shader reads must be listed here as
        /// <c>read-only</c> or (for r32float, r32uint, r32sint) <c>read-write</c>.
        /// </summary>
        public IReadOnlyDictionary<string, StorageTextureAccess> StorageAccess { get; init; } = new Dictionary<string, StorageTextureAccess>();

        /// <summary>Names of constant or storage buffers bound with a dynamic offset (<c>hasDynamicOffset</c>). Default: none.</summary>
        public IReadOnlyCollection<string> DynamicBuffers { get; init; } = [];

        /// <summary>
        /// The same by slot: <c>(group, binding)</c> pairs of buffers to bind with a dynamic offset. Either this, <see cref="DynamicBuffers"/> or
        /// <see cref="ResourceLayout.DynamicOffset"/> is enough. Slots that name no buffer are ignored. Default: none.
        /// </summary>
        public IReadOnlyCollection<(uint Group, uint Binding)> DynamicBindings { get; init; } = [];

        /// <summary>
        /// Extra stages OR-ed into the visibility of every entry of a bind group, by group index, so one bind group layout fits pipelines whose
        /// shaders use different stages. Default: none.
        /// </summary>
        public IReadOnlyDictionary<uint, ShaderStages> WidenGroups { get; init; } = new Dictionary<uint, ShaderStages>();

        /// <summary>The same for single entries, by <c>(group, binding)</c>; combined with <see cref="WidenGroups"/>. Default: none.</summary>
        public IReadOnlyDictionary<(uint Group, uint Binding), ShaderStages> WidenBindings { get; init; } = new Dictionary<(uint Group, uint Binding), ShaderStages>();

        /// <summary>Extra stages OR-ed into the visibility of the emulated push-constant buffer. Default: none.</summary>
        public ShaderStages WidenPushConstants { get; init; }
    }
}

namespace OneShader.Layouts
{
    using OneShader.Layouts.WebGpu;

    public sealed partial class PipelineLayout
    {
        /// <summary>
        /// The WebGPU bind group layouts. Push constants become a uniform buffer entry at the group and binding the compiler reserved.
        /// Fails with <see cref="LayoutErrorKind.Unsupported"/> for what WebGPU cannot express (resource arrays, typed buffers, 1D
        /// array textures, storage textures without a format) and <see cref="LayoutErrorKind.MissingTarget"/> when WGSL was not built.
        /// </summary>
        /// <param name="layout">The description on success.</param>
        /// <param name="error">What is wrong and what to change.</param>
        /// <param name="options">Host choices; defaults when null.</param>
        /// <returns>True on success.</returns>
        public bool TryToWebGpu(out WebGpuPipelineLayout? layout, out LayoutError? error, WebGpuLayoutOptions? options = null)
        {
            layout = null;
            options ??= new WebGpuLayoutOptions();
            var groups = new List<List<BindGroupLayoutEntryInfo>>();
            foreach (var g in Groups)
            {
                var list = new List<BindGroupLayoutEntryInfo>();
                foreach (var b in g.Entries)
                {
                    var r = b.Resource;
                    if (!TryEntry(r, options, out var entry, out error))
                    {
                        return false;
                    }

                    list.Add(entry!);
                }

                groups.Add(list);
            }

            var pushes = new List<PushConstantEmulation>();
            foreach (var p in PushConstants)
            {
                if (p.WgslConflict is { } wgslConflict)
                {
                    error = new LayoutError(LayoutErrorKind.PushConstantConflict, wgslConflict);
                    return false;
                }

                if (p.WgslGroup == LayoutSlots.None)
                {
                    error = new LayoutError(LayoutErrorKind.MissingTarget, "The push-constant block '" + p.Name + "' has no WebGPU slot: add Wgsl to OneShaderTargets (or CompileOptions.Targets) and build again.");
                    return false;
                }

                while (groups.Count <= p.WgslGroup)
                {
                    groups.Add([]);
                }

                foreach (var e in groups[(int)p.WgslGroup])
                {
                    if (e.Binding == p.WgslBinding)
                    {
                        error = new LayoutError(LayoutErrorKind.PushConstantConflict, "The push-constant block '" + p.Name + "' and '" + e.Name + "' both use group " + Num(p.WgslGroup) + " binding " + Num(p.WgslBinding) + ". Compile the entry points from one file or renumber the resource.");
                        return false;
                    }
                }

                uint buffer = (p.Size + 15) & ~15u;
                var vis = Visibility(p.Stages | options.WidenPushConstants);
                groups[(int)p.WgslGroup].Add(new BindGroupLayoutEntryInfo(p.Name, p.WgslBinding, vis, BindingKind.Buffer, BufferBindingType.Uniform, buffer, IsPushConstant: true));
                pushes.Add(new PushConstantEmulation(p.Name, p.WgslGroup, p.WgslBinding, p.Size, buffer, vis));
            }

            var result = new BindGroupLayoutInfo[groups.Count];
            for (int i = 0; i < result.Length; i++)
            {
                groups[i].Sort(static (a, b) => a.Binding.CompareTo(b.Binding));
                result[i] = new BindGroupLayoutInfo((uint)i, groups[i].ToArray());
            }

            layout = new WebGpuPipelineLayout(result, pushes.ToArray(), WebGpuFeatureNames(SubgroupOperations, RequiredFeatures));
            error = null;
            return true;
        }

        private static string[] WebGpuFeatureNames(SubgroupOperations subgroups, DeviceFeatures features)
        {
            var names = new List<string>(2);
            if (subgroups != SubgroupOperations.None)
            {
                names.Add("subgroups");
            }

            if ((features & DeviceFeatures.Float16) != 0)
            {
                names.Add("shader-f16");
            }

            return names.ToArray();
        }

        private static ShaderStageVisibility Visibility(ShaderStages stages)
        {
            var v = ShaderStageVisibility.None;
            if ((stages & ShaderStages.Vertex) != 0)
            {
                v |= ShaderStageVisibility.Vertex;
            }

            if ((stages & ShaderStages.Pixel) != 0)
            {
                v |= ShaderStageVisibility.Fragment;
            }

            if ((stages & ShaderStages.Compute) != 0)
            {
                v |= ShaderStageVisibility.Compute;
            }

            return v;
        }

        private static bool Contains(IReadOnlyCollection<string> names, string name)
        {
            foreach (string n in names)
            {
                if (n == name)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryEntry(ResourceLayout r, WebGpuLayoutOptions options, out BindGroupLayoutEntryInfo? entry, out LayoutError? error)
        {
            entry = null;
            var vis = Visibility(r.Stages | WidenedStages(r.Set, r.Binding, options.WidenGroups, options.WidenBindings));
            if (r.IsUnbounded)
            {
                error = Unsupported("'" + r.Name + "' is " + (r.Heap != DescriptorHeaps.None ? "a descriptor heap" : "an unbounded resource array") + "; WebGPU has no binding arrays or descriptor indexing, so it cannot be bound. Build the shader for Vulkan or Metal only, or declare the resources separately.");
                return false;
            }

            if (r.Count != 1)
            {
                error = Unsupported("'" + r.Name + "' is a resource array; WebGPU has no binding arrays. Declare separate resources or use a texture array.");
                return false;
            }

            switch (r.Kind)
            {
                case ResourceKind.ConstantBuffer:
                    entry = new BindGroupLayoutEntryInfo(r.Name, r.Binding, vis, BindingKind.Buffer, BufferBindingType.Uniform);
                    break;
                case ResourceKind.ReadOnlyStorageBuffer:
                    entry = new BindGroupLayoutEntryInfo(r.Name, r.Binding, vis, BindingKind.Buffer, BufferBindingType.ReadOnlyStorage);
                    break;
                case ResourceKind.StorageBuffer:
                    entry = new BindGroupLayoutEntryInfo(r.Name, r.Binding, vis, BindingKind.Buffer, Contains(options.ReadOnlyStorageBuffers, r.Name) ? BufferBindingType.ReadOnlyStorage : BufferBindingType.Storage);
                    break;
                case ResourceKind.Sampler:
                {
                    var type = r.Comparison ? SamplerBindingType.Comparison : Contains(options.NonFilteringSamplers, r.Name) ? SamplerBindingType.NonFiltering : SamplerBindingType.Filtering;
                    entry = new BindGroupLayoutEntryInfo(r.Name, r.Binding, vis, BindingKind.Sampler, SamplerType: type);
                    break;
                }

                case ResourceKind.SampledTexture:
                {
                    bool ms = r.Type is ResourceTypeKind.Texture2DMS;
                    TextureViewDimension? dim = r.Type switch
                    {
                        ResourceTypeKind.Texture1D => TextureViewDimension.D1,
                        ResourceTypeKind.Texture2D or ResourceTypeKind.Texture2DMS => TextureViewDimension.D2,
                        ResourceTypeKind.Texture2DArray => TextureViewDimension.D2Array,
                        ResourceTypeKind.Texture3D => TextureViewDimension.D3,
                        ResourceTypeKind.TextureCube => TextureViewDimension.Cube,
                        ResourceTypeKind.TextureCubeArray => TextureViewDimension.CubeArray,
                        _ => null,
                    };
                    if (dim is null)
                    {
                        error = Unsupported("'" + r.Name + "' is a " + r.Type + "; WebGPU has no one-dimensional array or multisampled array textures. Use a 2D array texture.");
                        return false;
                    }

                    var sample = r.Texel switch
                    {
                        ScalarKind.Int => TextureSampleType.Sint,
                        ScalarKind.UInt => TextureSampleType.Uint,
                        _ => Contains(options.DepthTextures, r.Name) ? TextureSampleType.Depth
                            : ms || Contains(options.UnfilterableTextures, r.Name) ? TextureSampleType.UnfilterableFloat : TextureSampleType.Float,
                    };
                    entry = new BindGroupLayoutEntryInfo(r.Name, r.Binding, vis, BindingKind.Texture, SampleType: sample, ViewDimension: dim.Value, Multisampled: ms);
                    break;
                }

                case ResourceKind.StorageTexture:
                {
                    TextureViewDimension? dim = r.Type switch
                    {
                        ResourceTypeKind.RWTexture1D => TextureViewDimension.D1,
                        ResourceTypeKind.RWTexture2D => TextureViewDimension.D2,
                        ResourceTypeKind.RWTexture2DArray => TextureViewDimension.D2Array,
                        ResourceTypeKind.RWTexture3D => TextureViewDimension.D3,
                        _ => null,
                    };
                    if (dim is null)
                    {
                        error = Unsupported("'" + r.Name + "' is a " + r.Type + "; WebGPU storage textures are 1D, 2D, 2D array or 3D.");
                        return false;
                    }

                    string? format = StorageFormat(r.ImageFormat);
                    if (format is null)
                    {
                        error = Unsupported(r.ImageFormat.Length == 0
                            ? "The storage texture '" + r.Name + "' has no texel format; WebGPU needs one. Add [[vk::image_format(\"rgba8\")]] (or another format) to its declaration."
                            : "The storage format '" + r.ImageFormat + "' of '" + r.Name + "' is not a WebGPU storage texel format.");
                        return false;
                    }

                    options.StorageAccess.TryGetValue(r.Name, out var access);
                    entry = new BindGroupLayoutEntryInfo(r.Name, r.Binding, vis, BindingKind.StorageTexture, ViewDimension: dim.Value, Access: access, StorageFormat: format);
                    break;
                }

                default:
                    error = Unsupported("'" + r.Name + "' is a typed buffer (" + r.Kind + "); WebGPU has none. Use a StructuredBuffer.");
                    return false;
            }

            if (entry!.Kind == BindingKind.Buffer && IsDynamic(r, options))
            {
                entry = entry with { HasDynamicOffset = true };
            }

            error = null;
            return true;
        }

        private static bool IsDynamic(ResourceLayout r, WebGpuLayoutOptions options)
        {
            bool dynamic = r.DynamicOffset || Contains(options.DynamicBuffers, r.Name);
            foreach (var d in options.DynamicBindings)
            {
                dynamic |= d.Group == r.Set && d.Binding == r.Binding;
            }

            return dynamic;
        }

        private static LayoutError Unsupported(string message) => new(LayoutErrorKind.Unsupported, message);

        // WGSL 6.6 storage texel formats (the core set and the texture_formats_tier1 additions) by their HLSL image-format names.
        private static string? StorageFormat(string name) => name switch
        {
            "rgba8" => "rgba8unorm",
            "rgba8snorm" => "rgba8snorm",
            "rgba8ui" => "rgba8uint",
            "rgba8i" => "rgba8sint",
            "rgba16ui" => "rgba16uint",
            "rgba16i" => "rgba16sint",
            "rgba16f" => "rgba16float",
            "r32ui" => "r32uint",
            "r32i" => "r32sint",
            "r32f" => "r32float",
            "rg32ui" => "rg32uint",
            "rg32i" => "rg32sint",
            "rg32f" => "rg32float",
            "rgba32ui" => "rgba32uint",
            "rgba32i" => "rgba32sint",
            "rgba32f" => "rgba32float",
            "r8" => "r8unorm",
            "r8snorm" => "r8snorm",
            "r8ui" => "r8uint",
            "r8i" => "r8sint",
            "r16" => "r16unorm",
            "r16snorm" => "r16snorm",
            "r16ui" => "r16uint",
            "r16i" => "r16sint",
            "r16f" => "r16float",
            "rgba16" => "rgba16unorm",
            "rgba16snorm" => "rgba16snorm",
            "rg8" => "rg8unorm",
            "rg8snorm" => "rg8snorm",
            "rg8ui" => "rg8uint",
            "rg8i" => "rg8sint",
            "rg16" => "rg16unorm",
            "rg16snorm" => "rg16snorm",
            "rg16ui" => "rg16uint",
            "rg16i" => "rg16sint",
            "rg16f" => "rg16float",
            "rgb10a2" => "rgb10a2unorm",
            "rgb10a2ui" => "rgb10a2uint",
            "r11g11b10f" => "rg11b10ufloat",
            _ => null,
        };
    }

    public sealed partial class VertexLayout
    {
        /// <summary>The WebGPU vertex buffer layout for this layout in one interleaved buffer.</summary>
        /// <param name="step">Per vertex (default) or per instance data.</param>
        /// <returns>The layout.</returns>
        public VertexBufferLayoutInfo ToWebGpu(VertexStepMode step = VertexStepMode.Vertex)
        {
            var attributes = new VertexAttributeInfo[_attributes.Length];
            for (int i = 0; i < attributes.Length; i++)
            {
                attributes[i] = new VertexAttributeInfo(WebGpuFormat(_attributes[i].Format), _attributes[i].Offset, _attributes[i].Location);
            }

            return new VertexBufferLayoutInfo(Stride, step, attributes);
        }

        /// <summary>The <c>GPUVertexFormat</c> string of a vertex format.</summary>
        /// <param name="format">The vertex format.</param>
        /// <returns>The string, for example <c>float32x2</c> or <c>unorm8x4</c>.</returns>
        public static string WebGpuFormat(VertexFormat format) => format switch
        {
            VertexFormat.Float32 => "float32",
            VertexFormat.Float32x2 => "float32x2",
            VertexFormat.Float32x3 => "float32x3",
            VertexFormat.Float32x4 => "float32x4",
            VertexFormat.Sint32 => "sint32",
            VertexFormat.Sint32x2 => "sint32x2",
            VertexFormat.Sint32x3 => "sint32x3",
            VertexFormat.Sint32x4 => "sint32x4",
            VertexFormat.Uint32 => "uint32",
            VertexFormat.Uint32x2 => "uint32x2",
            VertexFormat.Uint32x3 => "uint32x3",
            VertexFormat.Uint32x4 => "uint32x4",
            VertexFormat.Float16x2 => "float16x2",
            VertexFormat.Float16x4 => "float16x4",
            VertexFormat.Unorm8x4 => "unorm8x4",
            VertexFormat.Snorm8x4 => "snorm8x4",
            VertexFormat.Uint8x4 => "uint8x4",
            VertexFormat.Sint8x4 => "sint8x4",
            VertexFormat.Unorm16x2 => "unorm16x2",
            VertexFormat.Unorm16x4 => "unorm16x4",
            VertexFormat.Snorm16x2 => "snorm16x2",
            VertexFormat.Snorm16x4 => "snorm16x4",
            VertexFormat.Uint16x2 => "uint16x2",
            VertexFormat.Uint16x4 => "uint16x4",
            VertexFormat.Sint16x2 => "sint16x2",
            VertexFormat.Sint16x4 => "sint16x4",
            _ => "float32",
        };
    }
}
