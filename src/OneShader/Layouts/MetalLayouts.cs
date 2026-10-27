using System;
using System.Collections.Generic;
using OneShader.Reflection;

namespace OneShader.Layouts.Metal
{
    /// <summary>The class of Metal argument a resource is bound as; each class has its own index space.</summary>
    public enum ArgumentKind
    {
        /// <summary><c>[[buffer(n)]]</c>: constant and storage buffers.</summary>
        Buffer = 0,

        /// <summary><c>[[texture(n)]]</c>: textures and texel buffers.</summary>
        Texture = 1,

        /// <summary><c>[[sampler(n)]]</c>: samplers.</summary>
        Sampler = 2,
    }

    /// <summary>One resource of one stage, with the index to bind it at.</summary>
    /// <param name="Name">The HLSL name.</param>
    /// <param name="Set">The descriptor set the resource has on the other targets; hosts use <c>(Set, Binding)</c> to find it from a bind group.</param>
    /// <param name="Binding">The binding number on the other targets.</param>
    /// <param name="Kind">The argument class.</param>
    /// <param name="Index">The index within <paramref name="Kind"/>; an array of N resources takes N consecutive indices from here.</param>
    /// <param name="Count">The array size, at least 1.</param>
    public readonly record struct MetalArgument(string Name, uint Set, uint Binding, ArgumentKind Kind, uint Index, uint Count);

    /// <summary>Where a stage reads its push-constant block: <c>setVertexBytes</c>, <c>setFragmentBytes</c> or <c>setBytes</c> at this buffer index.</summary>
    /// <param name="Name">The HLSL variable name.</param>
    /// <param name="BufferIndex">The buffer index.</param>
    /// <param name="Size">Bytes to set.</param>
    public readonly record struct MetalPushConstant(string Name, uint BufferIndex, uint Size);

    /// <summary>What one stage of a pipeline binds.</summary>
    public sealed class MetalStageLayout
    {
        internal MetalStageLayout(ShaderStage stage, MetalArgument[] arguments, MetalPushConstant? push)
        {
            Stage = stage;
            Arguments = arguments;
            PushConstant = push;
        }

        /// <summary>The stage.</summary>
        public ShaderStage Stage { get; }

        /// <summary>The stage's resources, ordered by class (buffer, texture, sampler) and index.</summary>
        public IReadOnlyList<MetalArgument> Arguments { get; }

        /// <summary>The stage's push-constant block, or null.</summary>
        public MetalPushConstant? PushConstant { get; }

        /// <summary>Finds the argument for a resource given its set and binding on the other targets.</summary>
        /// <param name="set">The set.</param>
        /// <param name="binding">The binding.</param>
        /// <param name="argument">The argument when found.</param>
        /// <returns>True if this stage uses the resource.</returns>
        public bool TryFind(uint set, uint binding, out MetalArgument argument)
        {
            foreach (var a in Arguments)
            {
                if (a.Set == set && a.Binding == binding)
                {
                    argument = a;
                    return true;
                }
            }

            argument = default;
            return false;
        }
    }

    /// <summary>The Metal binding tables of a pipeline, one per stage that is present.</summary>
    public sealed class MetalPipelineLayout
    {
        internal MetalPipelineLayout(MetalStageLayout[] stages, SubgroupOperations subgroupOperations = SubgroupOperations.None)
        {
            Stages = stages;
            SubgroupOperations = subgroupOperations;
        }

        /// <summary>The subgroup operations the shaders use (Metal SIMD-group and quad functions); empty when none, otherwise the GPU family must support them.</summary>
        public SubgroupOperations SubgroupOperations { get; }

        /// <summary>One entry per stage of the pipeline, in vertex, pixel, compute order.</summary>
        public IReadOnlyList<MetalStageLayout> Stages { get; }

        /// <summary>The table of a stage, or null if the pipeline has none.</summary>
        /// <param name="stage">The stage.</param>
        /// <returns>The stage's layout.</returns>
        public MetalStageLayout? ForStage(ShaderStage stage)
        {
            foreach (var s in Stages)
            {
                if (s.Stage == stage)
                {
                    return s;
                }
            }

            return null;
        }
    }

    /// <summary>An <c>MTLVertexFormat</c>, with the numeric values of Metal's header.</summary>
    public enum MetalVertexFormat : uint
    {
        /// <summary>No format.</summary>
        Invalid = 0,

        /// <summary><c>MTLVertexFormatUChar4</c>.</summary>
        UChar4 = 3,

        /// <summary><c>MTLVertexFormatChar4</c>.</summary>
        Char4 = 6,

        /// <summary><c>MTLVertexFormatUChar4Normalized</c>.</summary>
        UChar4Normalized = 9,

        /// <summary><c>MTLVertexFormatChar4Normalized</c>.</summary>
        Char4Normalized = 12,

        /// <summary><c>MTLVertexFormatUShort2</c>.</summary>
        UShort2 = 13,

        /// <summary><c>MTLVertexFormatUShort4</c>.</summary>
        UShort4 = 15,

        /// <summary><c>MTLVertexFormatShort2</c>.</summary>
        Short2 = 16,

        /// <summary><c>MTLVertexFormatShort4</c>.</summary>
        Short4 = 18,

        /// <summary><c>MTLVertexFormatUShort2Normalized</c>.</summary>
        UShort2Normalized = 19,

        /// <summary><c>MTLVertexFormatUShort4Normalized</c>.</summary>
        UShort4Normalized = 21,

        /// <summary><c>MTLVertexFormatShort2Normalized</c>.</summary>
        Short2Normalized = 22,

        /// <summary><c>MTLVertexFormatShort4Normalized</c>.</summary>
        Short4Normalized = 24,

        /// <summary><c>MTLVertexFormatHalf2</c>.</summary>
        Half2 = 25,

        /// <summary><c>MTLVertexFormatHalf4</c>.</summary>
        Half4 = 27,

        /// <summary><c>MTLVertexFormatFloat</c>.</summary>
        Float = 28,

        /// <summary><c>MTLVertexFormatFloat2</c>.</summary>
        Float2 = 29,

        /// <summary><c>MTLVertexFormatFloat3</c>.</summary>
        Float3 = 30,

        /// <summary><c>MTLVertexFormatFloat4</c>.</summary>
        Float4 = 31,

        /// <summary><c>MTLVertexFormatInt</c>.</summary>
        Int = 32,

        /// <summary><c>MTLVertexFormatInt2</c>.</summary>
        Int2 = 33,

        /// <summary><c>MTLVertexFormatInt3</c>.</summary>
        Int3 = 34,

        /// <summary><c>MTLVertexFormatInt4</c>.</summary>
        Int4 = 35,

        /// <summary><c>MTLVertexFormatUInt</c>.</summary>
        UInt = 36,

        /// <summary><c>MTLVertexFormatUInt2</c>.</summary>
        UInt2 = 37,

        /// <summary><c>MTLVertexFormatUInt3</c>.</summary>
        UInt3 = 38,

        /// <summary><c>MTLVertexFormatUInt4</c>.</summary>
        UInt4 = 39,
    }

    /// <summary>An <c>MTLVertexStepFunction</c>.</summary>
    public enum VertexStepFunction : uint
    {
        /// <summary>Per vertex.</summary>
        PerVertex = 1,

        /// <summary>Per instance.</summary>
        PerInstance = 2,
    }

    /// <summary>One <c>MTLVertexAttributeDescriptor</c>.</summary>
    /// <param name="AttributeIndex">The index in <c>vertexDescriptor.attributes</c>; equal to the shader input location (<c>[[attribute(n)]]</c>).</param>
    /// <param name="Format">The attribute format.</param>
    /// <param name="Offset">Byte offset inside the vertex.</param>
    /// <param name="BufferIndex">The buffer the attribute is read from.</param>
    public readonly record struct MetalVertexAttribute(uint AttributeIndex, MetalVertexFormat Format, uint Offset, uint BufferIndex);

    /// <summary>An <c>MTLVertexDescriptor</c> for one interleaved vertex buffer.</summary>
    /// <param name="BufferIndex">The buffer index the vertex data is bound at (<c>layouts[BufferIndex]</c>).</param>
    /// <param name="Stride">Bytes between elements.</param>
    /// <param name="StepFunction">Per vertex or per instance.</param>
    /// <param name="Attributes">The attributes.</param>
    public sealed record MetalVertexDescriptor(uint BufferIndex, uint Stride, VertexStepFunction StepFunction, IReadOnlyList<MetalVertexAttribute> Attributes);
}

namespace OneShader.Layouts
{
    using OneShader.Layouts.Metal;

    public sealed partial class PipelineLayout
    {
        /// <summary>
        /// The Metal binding tables per stage: the argument index of each resource and the buffer index of the push-constant block. Metal has
        /// no sets, so each stage uses the indices the compiler gave its shader file.
        /// </summary>
        /// <param name="layout">The tables on success.</param>
        /// <param name="error">What is wrong on failure: the shader was not built for Metal.</param>
        /// <returns>True on success.</returns>
        public bool TryToMetal(out MetalPipelineLayout? layout, out LayoutError? error)
        {
            layout = null;
            var stages = new List<MetalStageLayout>();
            for (int s = 0; s < 3; s++)
            {
                var stage = (ShaderStage)s;
                if (EntryFor(stage) is not { } entry)
                {
                    continue;
                }

                var args = new List<MetalArgument>();
                foreach (var group in Groups)
                {
                    foreach (var b in group.Entries)
                    {
                        if ((b.Resource.Stages & EntryLayout.StageBit(stage)) == 0)
                        {
                            continue;
                        }

                        uint index = b.MetalIndexFor(stage);
                        if (index == LayoutSlots.None)
                        {
                            error = new LayoutError(LayoutErrorKind.MissingTarget, "The resource '" + b.Resource.Name + "' has no Metal index: add Msl to OneShaderTargets (or CompileOptions.Targets) and build again.");
                            return false;
                        }

                        args.Add(new MetalArgument(b.Resource.Name, b.Resource.Set, b.Resource.Binding, ArgumentOf(b.Resource.Kind), index, Math.Max(b.Resource.Count, 1)));
                    }
                }

                args.Sort(static (a, b) => a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind) : a.Index.CompareTo(b.Index));
                MetalPushConstant? push = null;
                if (entry.PushConstant is { } p)
                {
                    foreach (var block in PushConstants)
                    {
                        if (block.Name == p.Name && block.Size == p.Size)
                        {
                            uint index = block.MetalIndexFor(stage);
                            if (index == LayoutSlots.None)
                            {
                                error = new LayoutError(LayoutErrorKind.MissingTarget, "The push-constant block '" + block.Name + "' has no Metal index: add Msl to OneShaderTargets (or CompileOptions.Targets) and build again.");
                                return false;
                            }

                            push = new MetalPushConstant(block.Name, index, block.Size);
                        }
                    }
                }

                stages.Add(new MetalStageLayout(stage, args.ToArray(), push));
            }

            layout = new MetalPipelineLayout(stages.ToArray(), SubgroupOperations);
            error = null;
            return true;
        }

        private static ArgumentKind ArgumentOf(ResourceKind kind) => MetalClass(kind) switch
        {
            2 => ArgumentKind.Sampler,
            1 => ArgumentKind.Texture,
            _ => ArgumentKind.Buffer,
        };
    }

    public sealed partial class VertexLayout
    {
        /// <summary>The Metal vertex descriptor for this layout in one interleaved buffer.</summary>
        /// <param name="bufferIndex">The buffer index the vertex data is bound at; default <see cref="LayoutSlots.DefaultMetalVertexBufferIndex"/> (30), or pass the generated <c>MslVertexBufferIndex</c>.</param>
        /// <param name="step">Per vertex (default) or per instance data.</param>
        /// <returns>The descriptor.</returns>
        public MetalVertexDescriptor ToMetal(uint bufferIndex = LayoutSlots.DefaultMetalVertexBufferIndex, VertexStepMode step = VertexStepMode.Vertex)
        {
            var attributes = new MetalVertexAttribute[_attributes.Length];
            for (int i = 0; i < attributes.Length; i++)
            {
                attributes[i] = new MetalVertexAttribute(_attributes[i].Location, MetalFormat(_attributes[i].Format), _attributes[i].Offset, bufferIndex);
            }

            return new MetalVertexDescriptor(bufferIndex, Stride, step == VertexStepMode.Instance ? VertexStepFunction.PerInstance : VertexStepFunction.PerVertex, attributes);
        }

        /// <summary>The <c>MTLVertexFormat</c> of a vertex format.</summary>
        /// <param name="format">The vertex format.</param>
        /// <returns>The Metal format.</returns>
        public static MetalVertexFormat MetalFormat(VertexFormat format) => format switch
        {
            VertexFormat.Float32 => MetalVertexFormat.Float,
            VertexFormat.Float32x2 => MetalVertexFormat.Float2,
            VertexFormat.Float32x3 => MetalVertexFormat.Float3,
            VertexFormat.Float32x4 => MetalVertexFormat.Float4,
            VertexFormat.Sint32 => MetalVertexFormat.Int,
            VertexFormat.Sint32x2 => MetalVertexFormat.Int2,
            VertexFormat.Sint32x3 => MetalVertexFormat.Int3,
            VertexFormat.Sint32x4 => MetalVertexFormat.Int4,
            VertexFormat.Uint32 => MetalVertexFormat.UInt,
            VertexFormat.Uint32x2 => MetalVertexFormat.UInt2,
            VertexFormat.Uint32x3 => MetalVertexFormat.UInt3,
            VertexFormat.Uint32x4 => MetalVertexFormat.UInt4,
            VertexFormat.Float16x2 => MetalVertexFormat.Half2,
            VertexFormat.Float16x4 => MetalVertexFormat.Half4,
            VertexFormat.Unorm8x4 => MetalVertexFormat.UChar4Normalized,
            VertexFormat.Snorm8x4 => MetalVertexFormat.Char4Normalized,
            VertexFormat.Uint8x4 => MetalVertexFormat.UChar4,
            VertexFormat.Sint8x4 => MetalVertexFormat.Char4,
            VertexFormat.Unorm16x2 => MetalVertexFormat.UShort2Normalized,
            VertexFormat.Unorm16x4 => MetalVertexFormat.UShort4Normalized,
            VertexFormat.Snorm16x2 => MetalVertexFormat.Short2Normalized,
            VertexFormat.Snorm16x4 => MetalVertexFormat.Short4Normalized,
            VertexFormat.Uint16x2 => MetalVertexFormat.UShort2,
            VertexFormat.Uint16x4 => MetalVertexFormat.UShort4,
            VertexFormat.Sint16x2 => MetalVertexFormat.Short2,
            VertexFormat.Sint16x4 => MetalVertexFormat.Short4,
            _ => MetalVertexFormat.Invalid,
        };
    }
}
