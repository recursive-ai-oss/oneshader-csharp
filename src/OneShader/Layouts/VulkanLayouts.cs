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
    public readonly record struct DescriptorSetLayoutBindingInfo(uint Binding, DescriptorType DescriptorType, uint DescriptorCount, ShaderStageFlags StageFlags, string Name);

    /// <summary>One <c>VkDescriptorSetLayout</c> to create.</summary>
    /// <param name="Set">The set number.</param>
    /// <param name="Bindings">The bindings, sorted by binding number; empty for a set no shader uses.</param>
    public sealed record DescriptorSetLayoutInfo(uint Set, IReadOnlyList<DescriptorSetLayoutBindingInfo> Bindings);

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
            var sets = new DescriptorSetLayoutInfo[Groups.Count];
            for (int g = 0; g < sets.Length; g++)
            {
                var entries = Groups[g].Entries;
                var bindings = new DescriptorSetLayoutBindingInfo[entries.Count];
                for (int i = 0; i < bindings.Length; i++)
                {
                    var r = entries[i].Resource;
                    bool isDynamic = false;
                    foreach (string d in dynamic)
                    {
                        isDynamic |= d == r.Name;
                    }

                    bindings[i] = new DescriptorSetLayoutBindingInfo(r.Binding, VulkanDescriptor(r.Kind, isDynamic), Math.Max(r.Count, 1), VulkanStages(r.Stages), r.Name);
                }

                sets[g] = new DescriptorSetLayoutInfo(Groups[g].Set, bindings);
            }

            var ranges = new PushConstantRangeInfo[PushConstants.Count];
            for (int i = 0; i < ranges.Length; i++)
            {
                // Vulkan wants a multiple of 4; block sizes already are, rounding keeps a hand-built layout valid.
                ranges[i] = new PushConstantRangeInfo(VulkanStages(PushConstants[i].Stages), 0, (PushConstants[i].Size + 3) & ~3u);
            }

            return new PipelineLayoutInfo(sets, ranges, SubgroupOperations, RequiredFeatures);
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
