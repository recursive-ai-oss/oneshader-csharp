using System;
using System.Collections.Generic;
using OneShader.Reflection;

namespace OneShader.Layouts.Vulkan
{
    /// <summary>A <c>VkDescriptorType</c>, with the numeric values of the Vulkan specification.</summary>
    public enum DescriptorType : uint
    {
        /// <summary><c>VK_DESCRIPTOR_TYPE_SAMPLER</c>.</summary>
        Sampler = 0,

        /// <summary><c>VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER</c>; not produced from HLSL, listed for hosts that combine a texture and sampler themselves.</summary>
        CombinedImageSampler = 1,

        /// <summary><c>VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE</c>.</summary>
        SampledImage = 2,

        /// <summary><c>VK_DESCRIPTOR_TYPE_STORAGE_IMAGE</c>.</summary>
        StorageImage = 3,

        /// <summary><c>VK_DESCRIPTOR_TYPE_UNIFORM_TEXEL_BUFFER</c>.</summary>
        UniformTexelBuffer = 4,

        /// <summary><c>VK_DESCRIPTOR_TYPE_STORAGE_TEXEL_BUFFER</c>.</summary>
        StorageTexelBuffer = 5,

        /// <summary><c>VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER</c>.</summary>
        UniformBuffer = 6,

        /// <summary><c>VK_DESCRIPTOR_TYPE_STORAGE_BUFFER</c>.</summary>
        StorageBuffer = 7,

        /// <summary><c>VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER_DYNAMIC</c>.</summary>
        UniformBufferDynamic = 8,

        /// <summary><c>VK_DESCRIPTOR_TYPE_STORAGE_BUFFER_DYNAMIC</c>.</summary>
        StorageBufferDynamic = 9,

        /// <summary>
        /// <c>VK_DESCRIPTOR_TYPE_MUTABLE_EXT</c> (<c>VK_EXT_mutable_descriptor_type</c>): a binding that holds several descriptor types, listed in
        /// <see cref="DescriptorSetLayoutBindingInfo.MutableTypes"/>. Produced for a descriptor heap that shaders read as more than one kind of resource.
        /// </summary>
        Mutable = 1000351000,
    }

    /// <summary>A <c>VkDescriptorBindingFlagBits</c> mask, with the numeric values of the Vulkan specification.</summary>
    [Flags]
    public enum DescriptorBindingFlags : uint
    {
        /// <summary>No flags.</summary>
        None = 0,

        /// <summary><c>VK_DESCRIPTOR_BINDING_UPDATE_AFTER_BIND_BIT</c>: descriptors may be written after the set is bound; needs <c>descriptorBindingXxxUpdateAfterBind</c> for the descriptor type.</summary>
        UpdateAfterBind = 1,

        /// <summary><c>VK_DESCRIPTOR_BINDING_UPDATE_UNUSED_WHILE_PENDING_BIT</c>.</summary>
        UpdateUnusedWhilePending = 2,

        /// <summary><c>VK_DESCRIPTOR_BINDING_PARTIALLY_BOUND_BIT</c>: elements the shader does not touch need not hold a valid descriptor (<c>descriptorBindingPartiallyBound</c>).</summary>
        PartiallyBound = 4,

        /// <summary><c>VK_DESCRIPTOR_BINDING_VARIABLE_DESCRIPTOR_COUNT_BIT</c>: the count is chosen when the set is allocated (<c>descriptorBindingVariableDescriptorCount</c>); only the highest binding of a set can have it.</summary>
        VariableDescriptorCount = 8,
    }

    /// <summary>A <c>VkDescriptorSetLayoutCreateFlagBits</c> mask, with the numeric values of the Vulkan specification.</summary>
    [Flags]
    public enum DescriptorSetLayoutCreateFlags : uint
    {
        /// <summary>No flags.</summary>
        None = 0,

        /// <summary><c>VK_DESCRIPTOR_SET_LAYOUT_CREATE_UPDATE_AFTER_BIND_POOL_BIT</c>: sets of this layout come from a pool created with <c>UPDATE_AFTER_BIND</c>.</summary>
        UpdateAfterBindPool = 2,
    }

    /// <summary>A <c>VkShaderStageFlagBits</c> mask, with the numeric values of the Vulkan specification.</summary>
    [Flags]
    public enum ShaderStageFlags : uint
    {
        /// <summary>No stage.</summary>
        None = 0,

        /// <summary><c>VK_SHADER_STAGE_VERTEX_BIT</c>.</summary>
        Vertex = 0x1,

        /// <summary><c>VK_SHADER_STAGE_FRAGMENT_BIT</c>.</summary>
        Fragment = 0x10,

        /// <summary><c>VK_SHADER_STAGE_COMPUTE_BIT</c>.</summary>
        Compute = 0x20,
    }

    /// <summary>A <c>VkFormat</c> for vertex attributes, with the numeric values of the Vulkan specification.</summary>
    public enum Format : uint
    {
        /// <summary><c>VK_FORMAT_UNDEFINED</c>.</summary>
        Undefined = 0,

        /// <summary><c>VK_FORMAT_R8G8B8A8_UNORM</c>.</summary>
        R8G8B8A8Unorm = 37,

        /// <summary><c>VK_FORMAT_R8G8B8A8_SNORM</c>.</summary>
        R8G8B8A8Snorm = 38,

        /// <summary><c>VK_FORMAT_R8G8B8A8_UINT</c>.</summary>
        R8G8B8A8Uint = 41,

        /// <summary><c>VK_FORMAT_R8G8B8A8_SINT</c>.</summary>
        R8G8B8A8Sint = 42,

        /// <summary><c>VK_FORMAT_R16G16_UNORM</c>.</summary>
        R16G16Unorm = 77,

        /// <summary><c>VK_FORMAT_R16G16_SNORM</c>.</summary>
        R16G16Snorm = 78,

        /// <summary><c>VK_FORMAT_R16G16_UINT</c>.</summary>
        R16G16Uint = 81,

        /// <summary><c>VK_FORMAT_R16G16_SINT</c>.</summary>
        R16G16Sint = 82,

        /// <summary><c>VK_FORMAT_R16G16_SFLOAT</c>.</summary>
        R16G16Sfloat = 83,

        /// <summary><c>VK_FORMAT_R16G16B16A16_UNORM</c>.</summary>
        R16G16B16A16Unorm = 91,

        /// <summary><c>VK_FORMAT_R16G16B16A16_SNORM</c>.</summary>
        R16G16B16A16Snorm = 92,

        /// <summary><c>VK_FORMAT_R16G16B16A16_UINT</c>.</summary>
        R16G16B16A16Uint = 95,

        /// <summary><c>VK_FORMAT_R16G16B16A16_SINT</c>.</summary>
        R16G16B16A16Sint = 96,

        /// <summary><c>VK_FORMAT_R16G16B16A16_SFLOAT</c>.</summary>
        R16G16B16A16Sfloat = 97,

        /// <summary><c>VK_FORMAT_R32_UINT</c>.</summary>
        R32Uint = 98,

        /// <summary><c>VK_FORMAT_R32_SINT</c>.</summary>
        R32Sint = 99,

        /// <summary><c>VK_FORMAT_R32_SFLOAT</c>.</summary>
        R32Sfloat = 100,

        /// <summary><c>VK_FORMAT_R32G32_UINT</c>.</summary>
        R32G32Uint = 101,

        /// <summary><c>VK_FORMAT_R32G32_SINT</c>.</summary>
        R32G32Sint = 102,

        /// <summary><c>VK_FORMAT_R32G32_SFLOAT</c>.</summary>
        R32G32Sfloat = 103,

        /// <summary><c>VK_FORMAT_R32G32B32_UINT</c>.</summary>
        R32G32B32Uint = 104,

        /// <summary><c>VK_FORMAT_R32G32B32_SINT</c>.</summary>
        R32G32B32Sint = 105,

        /// <summary><c>VK_FORMAT_R32G32B32_SFLOAT</c>.</summary>
        R32G32B32Sfloat = 106,

        /// <summary><c>VK_FORMAT_R32G32B32A32_UINT</c>.</summary>
        R32G32B32A32Uint = 107,

        /// <summary><c>VK_FORMAT_R32G32B32A32_SINT</c>.</summary>
        R32G32B32A32Sint = 108,

        /// <summary><c>VK_FORMAT_R32G32B32A32_SFLOAT</c>.</summary>
        R32G32B32A32Sfloat = 109,
    }

    /// <summary>A <c>VkVertexInputRate</c>.</summary>
    public enum VertexInputRate : uint
    {
        /// <summary>Attributes advance per vertex.</summary>
        Vertex = 0,

        /// <summary>Attributes advance per instance.</summary>
        Instance = 1,
    }

    /// <summary>The fields of one <c>VkDescriptorSetLayoutBinding</c> (immutable samplers are not used).</summary>
    /// <param name="Binding">The binding number.</param>
    /// <param name="DescriptorType">The descriptor type.</param>
    /// <param name="DescriptorCount">The array size, at least 1.</param>
    /// <param name="StageFlags">The stages that use it.</param>
    /// <param name="Name">The HLSL name, for messages and for matching writes to bindings; not part of Vulkan.</param>
    public readonly record struct DescriptorSetLayoutBindingInfo(uint Binding, DescriptorType DescriptorType, uint DescriptorCount, ShaderStageFlags StageFlags, string Name)
    {
        /// <summary>True for a <c>*_BUFFER_DYNAMIC</c> descriptor: the host passes one offset for it at every <c>vkCmdBindDescriptorSets</c>, in binding order.</summary>
        public bool IsDynamic => DescriptorType is DescriptorType.UniformBufferDynamic or DescriptorType.StorageBufferDynamic;

        /// <summary>
        /// The <c>VkDescriptorSetLayoutBindingFlagsCreateInfo</c> flags of this binding; <see cref="DescriptorBindingFlags.None"/> for ordinary
        /// bindings. Set only on bindings of unbounded arrays and heaps.
        /// </summary>
        public DescriptorBindingFlags BindingFlags { get; init; }

        /// <summary>True when the shader declares this binding without a size (an unbounded array or a heap); <see cref="DescriptorCount"/> is then the maximum the layout was built for.</summary>
        public bool IsUnbounded { get; init; }

        /// <summary>True when the binding's flags include <see cref="DescriptorBindingFlags.VariableDescriptorCount"/>: <see cref="DescriptorCount"/> is the largest count a set may be allocated with.</summary>
        public bool IsVariableCount => (BindingFlags & DescriptorBindingFlags.VariableDescriptorCount) != 0;

        /// <summary>For <see cref="DescriptorType.Mutable"/>: the descriptor types the binding may hold (<c>VkMutableDescriptorTypeListEXT</c>); empty otherwise.</summary>
        public IReadOnlyList<DescriptorType> MutableTypes
        {
            get => _mutableTypes ?? [];
            init => _mutableTypes = value;
        }

        private readonly IReadOnlyList<DescriptorType>? _mutableTypes;
    }

    /// <summary>One <c>VkDescriptorSetLayout</c> to create.</summary>
    /// <param name="Set">The set number.</param>
    /// <param name="Bindings">The bindings, sorted by binding number; empty for a set no shader uses.</param>
    public sealed record DescriptorSetLayoutInfo(uint Set, IReadOnlyList<DescriptorSetLayoutBindingInfo> Bindings)
    {
        /// <summary>The <c>VkDescriptorSetLayoutCreateInfo</c> flags: <see cref="DescriptorSetLayoutCreateFlags.UpdateAfterBindPool"/> when a binding is update-after-bind.</summary>
        public DescriptorSetLayoutCreateFlags CreateFlags { get; init; }

        /// <summary>True when any binding has flags, so the layout needs a <c>VkDescriptorSetLayoutBindingFlagsCreateInfo</c> (and descriptor indexing enabled).</summary>
        public bool HasBindingFlags
        {
            get
            {
                foreach (var b in Bindings)
                {
                    if (b.BindingFlags != DescriptorBindingFlags.None)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }

    /// <summary>One <c>VkPushConstantRange</c>.</summary>
    /// <param name="StageFlags">The stages that read the block.</param>
    /// <param name="Offset">The byte offset; always 0, since a block starts at offset 0.</param>
    /// <param name="Size">The byte size, a multiple of 4.</param>
    public readonly record struct PushConstantRangeInfo(ShaderStageFlags StageFlags, uint Offset, uint Size);

    /// <summary>One <c>VkVertexInputBindingDescription</c>.</summary>
    /// <param name="Binding">The vertex buffer binding.</param>
    /// <param name="Stride">Bytes between vertices (or instances).</param>
    /// <param name="InputRate">Per vertex or per instance.</param>
    public readonly record struct VertexInputBindingInfo(uint Binding, uint Stride, VertexInputRate InputRate);

    /// <summary>One <c>VkVertexInputAttributeDescription</c>.</summary>
    /// <param name="Location">The shader input location.</param>
    /// <param name="Binding">The vertex buffer binding it reads.</param>
    /// <param name="Format">The attribute format.</param>
    /// <param name="Offset">Byte offset inside the vertex.</param>
    public readonly record struct VertexInputAttributeInfo(uint Location, uint Binding, Format Format, uint Offset);

    /// <summary>What a <c>VkPipelineVertexInputStateCreateInfo</c> is made of.</summary>
    /// <param name="Bindings">The binding descriptions.</param>
    /// <param name="Attributes">The attribute descriptions.</param>
    public sealed record VertexInputInfo(IReadOnlyList<VertexInputBindingInfo> Bindings, IReadOnlyList<VertexInputAttributeInfo> Attributes);

    /// <summary>What a <c>VkPipelineLayoutCreateInfo</c> is made of: one descriptor set layout per set, in set order, and the push-constant ranges.</summary>
    /// <param name="SetLayouts">One entry per set from 0; <c>SetLayouts[i].Set == i</c>.</param>
    /// <param name="PushConstantRanges">At most one range per stage.</param>
    /// <param name="SubgroupOperations">The subgroup operations the shaders use; check them against <c>subgroupSupportedOperations</c> before creating the pipeline.</param>
    /// <param name="RequiredFeatures">The 16-bit and 64-bit device features the shaders need (<c>shaderFloat16</c>, <c>shaderInt16</c>, <c>shaderFloat64</c>, <c>shaderInt64</c>, the 16-bit storage features, 64-bit atomics); enable them when creating the device.</param>
    public sealed record PipelineLayoutInfo(IReadOnlyList<DescriptorSetLayoutInfo> SetLayouts, IReadOnlyList<PushConstantRangeInfo> PushConstantRanges, SubgroupOperations SubgroupOperations = SubgroupOperations.None, DeviceFeatures RequiredFeatures = DeviceFeatures.None);

    /// <summary>Choices a host can make that the shader does not decide.</summary>
    public sealed class VulkanLayoutOptions
    {
        /// <summary>
        /// Names of constant or storage buffers to declare as dynamic-offset descriptors (<c>UNIFORM_BUFFER_DYNAMIC</c>,
        /// <c>STORAGE_BUFFER_DYNAMIC</c>). Default: none.
        /// </summary>
        public IReadOnlyCollection<string> DynamicBuffers { get; init; } = [];

        /// <summary>
        /// The same by slot, for hosts that address a binding by number: <c>(set, binding)</c> pairs of constant or storage buffers to declare
        /// dynamic. Either this, <see cref="DynamicBuffers"/> or <see cref="ResourceLayout.DynamicOffset"/> is enough. Slots that name no
        /// buffer are ignored. Default: none.
        /// </summary>
        public IReadOnlyCollection<(uint Set, uint Binding)> DynamicBindings { get; init; } = [];

        /// <summary>
        /// Extra stages OR-ed into the visibility of every binding of a set, by set number, so a descriptor set layout made once fits pipelines
        /// whose shaders use different stages (for example <c>{ [0] = ShaderStages.Vertex | ShaderStages.Pixel }</c>). Sets with no bindings
        /// are not changed. Default: none.
        /// </summary>
        public IReadOnlyDictionary<uint, ShaderStages> WidenSets { get; init; } = new Dictionary<uint, ShaderStages>();

        /// <summary>The same for single bindings, by <c>(set, binding)</c>; combined with <see cref="WidenSets"/>. Default: none.</summary>
        public IReadOnlyDictionary<(uint Set, uint Binding), ShaderStages> WidenBindings { get; init; } = new Dictionary<(uint Set, uint Binding), ShaderStages>();

        /// <summary>Extra stages OR-ed into the stage flags of every push-constant range, so one range fits several pipelines. Default: none.</summary>
        public ShaderStages WidenPushConstants { get; init; }

        /// <summary>
        /// The descriptor count given to an unbounded array or a descriptor heap, which the shader leaves open: the largest number of
        /// descriptors a set of this layout can hold. Keep it within the device's <c>maxPerStageDescriptorUpdateAfterBind*</c> limits; samplers
        /// have much lower limits than images on some devices. Default 4096.
        /// </summary>
        public uint UnboundedMaxCount { get; init; } = 4096;

        /// <summary>The same by <c>(set, binding)</c>, overriding <see cref="UnboundedMaxCount"/> for those bindings. Default: none.</summary>
        public IReadOnlyDictionary<(uint Set, uint Binding), uint> UnboundedCounts { get; init; } = new Dictionary<(uint Set, uint Binding), uint>();

        /// <summary>Mark unbounded bindings <see cref="DescriptorBindingFlags.PartiallyBound"/>, so descriptors the shader never reads may stay unwritten (needs <c>descriptorBindingPartiallyBound</c>). Default true.</summary>
        public bool PartiallyBound { get; init; } = true;

        /// <summary>
        /// Mark the highest-numbered binding of a set <see cref="DescriptorBindingFlags.VariableDescriptorCount"/> when it is unbounded, so each set
        /// is allocated with the count it needs (needs <c>descriptorBindingVariableDescriptorCount</c>). Vulkan allows the flag on the last binding
        /// of a set only: other unbounded bindings keep the fixed maximum count. Default true.
        /// </summary>
        public bool VariableDescriptorCount { get; init; } = true;

        /// <summary>
        /// Mark unbounded bindings <see cref="DescriptorBindingFlags.UpdateAfterBind"/> and the layout of their set
        /// <see cref="DescriptorSetLayoutCreateFlags.UpdateAfterBindPool"/>, so descriptors can be written while a command buffer that uses the set is
        /// pending (the usual way a bindless renderer works; needs the matching <c>descriptorBindingXxxUpdateAfterBind</c> features, and the pool
        /// must be created with <c>UPDATE_AFTER_BIND</c>). Default false.
        /// </summary>
        public bool UpdateAfterBind { get; init; }
    }
}

namespace OneShader.Layouts
{
    using OneShader.Layouts.Vulkan;

    public sealed partial class PipelineLayout
    {
        /// <summary>
        /// The Vulkan description: descriptor set layouts for set 0 up to the highest used set, and the push-constant ranges. Never fails.
        /// </summary>
        /// <param name="options">Host choices; defaults when null.</param>
        /// <returns>Plain data for <c>vkCreateDescriptorSetLayout</c> and <c>vkCreatePipelineLayout</c>.</returns>
        public PipelineLayoutInfo ToVulkan(VulkanLayoutOptions? options = null)
        {
            var dynamic = options?.DynamicBuffers ?? [];
            var dynamicSlots = options?.DynamicBindings ?? [];
            var sets = new DescriptorSetLayoutInfo[Groups.Count];
            for (int g = 0; g < sets.Length; g++)
            {
                var entries = Groups[g].Entries;
                var bindings = new DescriptorSetLayoutBindingInfo[entries.Count];
                for (int i = 0; i < bindings.Length; i++)
                {
                    var r = entries[i].Resource;
                    bool isDynamic = r.DynamicOffset;
                    foreach (string d in dynamic)
                    {
                        isDynamic |= d == r.Name;
                    }

                    foreach (var d in dynamicSlots)
                    {
                        isDynamic |= d.Set == r.Set && d.Binding == r.Binding;
                    }

                    var stages = r.Stages;
                    if (options is not null)
                    {
                        stages |= WidenedStages(r.Set, r.Binding, options.WidenSets, options.WidenBindings);
                    }

                    if (r.IsUnbounded)
                    {
                        bindings[i] = UnboundedBinding(r, VulkanStages(stages), options, i == bindings.Length - 1);
                        continue;
                    }

                    bindings[i] = new DescriptorSetLayoutBindingInfo(r.Binding, VulkanDescriptor(r.Kind, isDynamic), Math.Max(r.Count, 1), VulkanStages(stages), r.Name);
                }

                bool updateAfterBind = false;
                foreach (var b in bindings)
                {
                    updateAfterBind |= (b.BindingFlags & DescriptorBindingFlags.UpdateAfterBind) != 0;
                }

                sets[g] = new DescriptorSetLayoutInfo(Groups[g].Set, bindings) { CreateFlags = updateAfterBind ? DescriptorSetLayoutCreateFlags.UpdateAfterBindPool : DescriptorSetLayoutCreateFlags.None };
            }

            var ranges = new PushConstantRangeInfo[PushConstants.Count];
            for (int i = 0; i < ranges.Length; i++)
            {
                // Vulkan wants a multiple of 4; block sizes already are, rounding keeps a hand-built layout valid.
                ranges[i] = new PushConstantRangeInfo(VulkanStages(PushConstants[i].Stages | (options?.WidenPushConstants ?? ShaderStages.None)), 0, (PushConstants[i].Size + 3) & ~3u);
            }

            return new PipelineLayoutInfo(sets, ranges, SubgroupOperations, RequiredFeatures);
        }

        // The bindings of a set are sorted by binding number, so the last one is the highest.
        private static DescriptorSetLayoutBindingInfo UnboundedBinding(ResourceLayout r, ShaderStageFlags stages, VulkanLayoutOptions? options, bool highestInSet)
        {
            uint max = options?.UnboundedMaxCount ?? 4096;
            if (options is not null && options.UnboundedCounts.TryGetValue((r.Set, r.Binding), out uint own))
            {
                max = own;
            }

            max = Math.Max(max, 1);
            var flags = DescriptorBindingFlags.None;
            if (options?.PartiallyBound ?? true)
            {
                flags |= DescriptorBindingFlags.PartiallyBound;
            }

            if ((options?.VariableDescriptorCount ?? true) && highestInSet)
            {
                flags |= DescriptorBindingFlags.VariableDescriptorCount;
            }

            if (options?.UpdateAfterBind ?? false)
            {
                flags |= DescriptorBindingFlags.UpdateAfterBind;
            }

            if (r.HeapKinds.Count > 1)
            {
                // One binding, several descriptor types: a mutable descriptor.
                var types = new List<DescriptorType>();
                foreach (var kind in r.HeapKinds)
                {
                    var t = VulkanDescriptor(kind);
                    if (!types.Contains(t))
                    {
                        types.Add(t);
                    }
                }

                return new DescriptorSetLayoutBindingInfo(r.Binding, DescriptorType.Mutable, max, stages, r.Name) { BindingFlags = flags, IsUnbounded = true, MutableTypes = types };
            }

            return new DescriptorSetLayoutBindingInfo(r.Binding, VulkanDescriptor(r.Kind), max, stages, r.Name) { BindingFlags = flags, IsUnbounded = true };
        }

        internal static ShaderStages WidenedStages(uint set, uint binding, IReadOnlyDictionary<uint, ShaderStages> sets, IReadOnlyDictionary<(uint Set, uint Binding), ShaderStages> bindings)
        {
            var extra = ShaderStages.None;
            if (sets.TryGetValue(set, out var s))
            {
                extra |= s;
            }

            if (bindings.TryGetValue((set, binding), out var b))
            {
                extra |= b;
            }

            return extra;
        }

        /// <summary>The Vulkan descriptor type for a resource kind.</summary>
        /// <param name="kind">The kind.</param>
        /// <param name="dynamic">True for the dynamic-offset variant of a buffer.</param>
        /// <returns>The descriptor type.</returns>
        public static DescriptorType VulkanDescriptor(ResourceKind kind, bool dynamic = false) => kind switch
        {
            ResourceKind.ConstantBuffer => dynamic ? DescriptorType.UniformBufferDynamic : DescriptorType.UniformBuffer,
            ResourceKind.ReadOnlyStorageBuffer or ResourceKind.StorageBuffer => dynamic ? DescriptorType.StorageBufferDynamic : DescriptorType.StorageBuffer,
            ResourceKind.SampledTexture => DescriptorType.SampledImage,
            ResourceKind.StorageTexture => DescriptorType.StorageImage,
            ResourceKind.Sampler => DescriptorType.Sampler,
            ResourceKind.UniformTexelBuffer => DescriptorType.UniformTexelBuffer,
            _ => DescriptorType.StorageTexelBuffer,
        };

        /// <summary>The Vulkan stage-flag mask for a set of stages.</summary>
        /// <param name="stages">The stages.</param>
        /// <returns>The <c>VkShaderStageFlagBits</c> mask.</returns>
        public static ShaderStageFlags VulkanStages(ShaderStages stages)
        {
            var r = ShaderStageFlags.None;
            if ((stages & ShaderStages.Vertex) != 0)
            {
                r |= ShaderStageFlags.Vertex;
            }

            if ((stages & ShaderStages.Pixel) != 0)
            {
                r |= ShaderStageFlags.Fragment;
            }

            if ((stages & ShaderStages.Compute) != 0)
            {
                r |= ShaderStageFlags.Compute;
            }

            return r;
        }
    }

    public sealed partial class VertexLayout
    {
        /// <summary>The Vulkan vertex input state for this layout in one vertex buffer.</summary>
        /// <param name="rate">Per vertex (default) or per instance data.</param>
        /// <param name="binding">The vertex buffer binding number (default 0).</param>
        /// <returns>One binding description and one attribute description per attribute.</returns>
        public VertexInputInfo ToVulkan(VertexStepMode rate = VertexStepMode.Vertex, uint binding = 0)
        {
            var attributes = new VertexInputAttributeInfo[_attributes.Length];
            for (int i = 0; i < attributes.Length; i++)
            {
                attributes[i] = new VertexInputAttributeInfo(_attributes[i].Location, binding, VulkanFormat(_attributes[i].Format), _attributes[i].Offset);
            }

            return new VertexInputInfo([new VertexInputBindingInfo(binding, Stride, rate == VertexStepMode.Instance ? VertexInputRate.Instance : VertexInputRate.Vertex)], attributes);
        }

        /// <summary>The <c>VkFormat</c> of a vertex format.</summary>
        /// <param name="format">The vertex format.</param>
        /// <returns>The Vulkan format.</returns>
        public static Format VulkanFormat(VertexFormat format) => format switch
        {
            VertexFormat.Float32 => Format.R32Sfloat,
            VertexFormat.Float32x2 => Format.R32G32Sfloat,
            VertexFormat.Float32x3 => Format.R32G32B32Sfloat,
            VertexFormat.Float32x4 => Format.R32G32B32A32Sfloat,
            VertexFormat.Sint32 => Format.R32Sint,
            VertexFormat.Sint32x2 => Format.R32G32Sint,
            VertexFormat.Sint32x3 => Format.R32G32B32Sint,
            VertexFormat.Sint32x4 => Format.R32G32B32A32Sint,
            VertexFormat.Uint32 => Format.R32Uint,
            VertexFormat.Uint32x2 => Format.R32G32Uint,
            VertexFormat.Uint32x3 => Format.R32G32B32Uint,
            VertexFormat.Uint32x4 => Format.R32G32B32A32Uint,
            VertexFormat.Float16x2 => Format.R16G16Sfloat,
            VertexFormat.Float16x4 => Format.R16G16B16A16Sfloat,
            VertexFormat.Unorm8x4 => Format.R8G8B8A8Unorm,
            VertexFormat.Snorm8x4 => Format.R8G8B8A8Snorm,
            VertexFormat.Uint8x4 => Format.R8G8B8A8Uint,
            VertexFormat.Sint8x4 => Format.R8G8B8A8Sint,
            VertexFormat.Unorm16x2 => Format.R16G16Unorm,
            VertexFormat.Unorm16x4 => Format.R16G16B16A16Unorm,
            VertexFormat.Snorm16x2 => Format.R16G16Snorm,
            VertexFormat.Snorm16x4 => Format.R16G16B16A16Snorm,
            VertexFormat.Uint16x2 => Format.R16G16Uint,
            VertexFormat.Uint16x4 => Format.R16G16B16A16Uint,
            VertexFormat.Sint16x2 => Format.R16G16Sint,
            VertexFormat.Sint16x4 => Format.R16G16B16A16Sint,
            _ => Format.Undefined,
        };
    }

    /// <summary>Whether a vertex buffer advances once per vertex or once per instance.</summary>
    public enum VertexStepMode
    {
        /// <summary>One element per vertex.</summary>
        Vertex = 0,

        /// <summary>One element per instance.</summary>
        Instance = 1,
    }
}
