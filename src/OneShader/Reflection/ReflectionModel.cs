using System;
using System.Collections.Generic;
using OneShader.Types;

namespace OneShader.Reflection;

/// <summary>The kinds of node in a <see cref="ReflectedType"/> tree.</summary>
public enum ReflectedTypeKind
{
    /// <summary>One scalar.</summary>
    Scalar,

    /// <summary>A vector.</summary>
    Vector,

    /// <summary>A matrix.</summary>
    Matrix,

    /// <summary>An array.</summary>
    Array,

    /// <summary>A struct.</summary>
    Struct,
}

/// <summary>Built-in (system-value) stage variables.</summary>
public enum BuiltIn
{
    /// <summary>Not a built-in; the variable is a user location.</summary>
    None,

    /// <summary><c>SV_Position</c> as a vertex output.</summary>
    Position,

    /// <summary><c>SV_Position</c> as a pixel input (the fragment coordinate).</summary>
    FragCoord,

    /// <summary><c>SV_VertexID</c> (zero-based index, base vertex included as in HLSL).</summary>
    VertexIndex,

    /// <summary><c>SV_InstanceID</c>.</summary>
    InstanceIndex,

    /// <summary><c>SV_IsFrontFace</c>.</summary>
    FrontFacing,

    /// <summary><c>SV_Depth</c>.</summary>
    FragDepth,

    /// <summary><c>SV_SampleIndex</c>.</summary>
    SampleIndex,

    /// <summary><c>SV_Coverage</c>.</summary>
    SampleMask,

    /// <summary><c>SV_PrimitiveID</c>.</summary>
    PrimitiveIndex,

    /// <summary><c>SV_RenderTargetArrayIndex</c>.</summary>
    Layer,

    /// <summary><c>SV_ViewportArrayIndex</c>.</summary>
    ViewportIndex,

    /// <summary><c>SV_ClipDistance</c>.</summary>
    ClipDistance,

    /// <summary><c>SV_CullDistance</c>.</summary>
    CullDistance,

    /// <summary><c>SV_DispatchThreadID</c>.</summary>
    GlobalInvocationId,

    /// <summary><c>SV_GroupThreadID</c>.</summary>
    LocalInvocationId,

    /// <summary><c>SV_GroupID</c>.</summary>
    WorkgroupId,

    /// <summary><c>SV_GroupIndex</c>.</summary>
    LocalInvocationIndex,
}

/// <summary>
/// A type described for tooling, with the byte layout the buffer rules gave it. Shape by
/// <see cref="Kind"/>; properties that do not apply to a kind are 0, empty or null.
/// </summary>
public sealed class ReflectedType
{
    /// <summary>Which node this is.</summary>
    public required ReflectedTypeKind Kind { get; init; }

    /// <summary>For scalar, vector, matrix: the scalar type.</summary>
    public ScalarKind Scalar { get; init; }

    /// <summary>For vectors the component count; for matrices the row count.</summary>
    public uint Rows { get; init; }

    /// <summary>For matrices the column count.</summary>
    public uint Columns { get; init; }

    /// <summary>For structs the struct name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Size in bytes, padding included.</summary>
    public uint Size { get; init; }

    /// <summary>Alignment in bytes.</summary>
    public uint Alignment { get; init; }

    /// <summary>For arrays: element count, 0 when unsized.</summary>
    public uint ArrayLength { get; init; }

    /// <summary>For arrays: bytes between elements.</summary>
    public uint ArrayStride { get; init; }

    /// <summary>For matrices: bytes between stored columns (column-major) or rows (row-major).</summary>
    public uint MatrixStride { get; init; }

    /// <summary>For matrices: how they are stored.</summary>
    public MatrixPacking Packing { get; init; }

    /// <summary>For arrays: the element type.</summary>
    public ReflectedType? Element { get; init; }

    /// <summary>For structs: members in declaration order.</summary>
    public IReadOnlyList<ReflectedMember> Members { get; init; } = [];

    /// <summary>Converts a computed layout into the reflected form.</summary>
    public static ReflectedType FromLayout(TypeLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return FromLayout(layout, 0);
    }

    private static ReflectedType FromLayout(TypeLayout l, int depth)
    {
        // Layout computation already caps nesting; this guard keeps a hand-built layout from recursing without bound.
        if (depth > 128)
        {
            throw new ArgumentException("layout is nested too deeply", nameof(l));
        }

        switch (l.Type)
        {
            case VectorType v:
                return new ReflectedType { Kind = ReflectedTypeKind.Vector, Scalar = v.Element.ScalarKind, Rows = (uint)v.Count, Size = (uint)l.Size, Alignment = (uint)l.Alignment };
            case MatrixType m:
                return new ReflectedType
                {
                    Kind = ReflectedTypeKind.Matrix,
                    Scalar = m.Element.ScalarKind,
                    Rows = (uint)m.Rows,
                    Columns = (uint)m.Columns,
                    Size = (uint)l.Size,
                    Alignment = (uint)l.Alignment,
                    MatrixStride = (uint)l.MatrixStride,
                    Packing = l.MatrixPacking,
                };
            case ArrayType:
                return new ReflectedType
                {
                    Kind = ReflectedTypeKind.Array,
                    Size = (uint)l.Size,
                    Alignment = (uint)l.Alignment,
                    ArrayLength = (uint)l.ArrayLength,
                    ArrayStride = (uint)l.ArrayStride,
                    Element = FromLayout(l.Element!, depth + 1),
                };
            case StructType st:
            {
                var members = new List<ReflectedMember>(l.Members.Count);
                foreach (var ml in l.Members)
                {
                    members.Add(new ReflectedMember(ml.Name, (uint)ml.Offset, FromLayout(ml.Layout, depth + 1)));
                }

                return new ReflectedType { Kind = ReflectedTypeKind.Struct, Name = st.Name, Size = (uint)l.Size, Alignment = (uint)l.Alignment, Members = members };
            }

            default:
                return new ReflectedType
                {
                    Kind = ReflectedTypeKind.Scalar,
                    Scalar = l.Type.ScalarElement?.ScalarKind ?? (l.Type is EnumType e ? e.Underlying.ScalarKind : ScalarKind.UInt),
                    Size = (uint)l.Size,
                    Alignment = (uint)l.Alignment,
                };
        }
    }
}

/// <summary>One member of a reflected struct.</summary>
/// <param name="Name">The member name.</param>
/// <param name="Offset">Byte offset from the start of the struct.</param>
/// <param name="Type">The member type.</param>
public sealed record ReflectedMember(string Name, uint Offset, ReflectedType Type);

/// <summary>Where a resource lives for one target. Metal uses the index within its argument class (buffer, texture, sampler) with <see cref="Set"/> 0.</summary>
/// <param name="Target">The backend.</param>
/// <param name="Set">Descriptor set / bind group (0 for Metal).</param>
/// <param name="Binding">Binding number, or Metal argument index.</param>
public readonly record struct TargetSlot(CompileTarget Target, uint Set, uint Binding);

/// <summary>A resource a shader file declares, with its slots and stage visibility.</summary>
public sealed class ResourceInfo
{
    /// <summary>The HLSL variable (or cbuffer) name.</summary>
    public required string Name { get; init; }

    /// <summary>The descriptor kind.</summary>
    public required ResourceKind Kind { get; init; }

    /// <summary>Array size; 1 for a single resource, 0 when unbounded.</summary>
    public uint Count { get; init; } = 1;

    /// <summary>Stages whose entry points use it.</summary>
    public ShaderStages Stages { get; init; }

    /// <summary>The HLSL type as spelled, for example <c>Texture2D&lt;float4&gt;</c>; empty for a <c>cbuffer</c>.</summary>
    public string HlslType { get; init; } = "";

    /// <summary>The resource family, which tells textures' dimensionality and buffer flavour.</summary>
    public ResourceTypeKind ResourceType { get; init; }

    /// <summary>For read-write textures declared with <c>[[vk::image_format("...")]]</c>: the format name as written (<c>rgba8</c>, <c>r32f</c>); empty otherwise.</summary>
    public string ImageFormat { get; init; } = "";

    /// <summary>One slot per enabled target, in <see cref="CompileTarget"/> order.</summary>
    public IReadOnlyList<TargetSlot> Slots { get; init; } = [];

    /// <summary>
    /// For append and consume buffers: where the hidden element counter lives, one slot per enabled target in
    /// <see cref="CompileTarget"/> order. The counter is a separate 4-byte read-write storage buffer in the same set as
    /// the buffer; the application must bind it (zeroed before the first dispatch to start empty). Empty for every other resource.
    /// </summary>
    public IReadOnlyList<TargetSlot> CounterSlots { get; init; } = [];

    /// <summary>
    /// For constant and structured buffers: the layout of the buffer's struct / element; its
    /// <see cref="ReflectedType.Size"/> is the constant buffer size or the element stride. For textures and typed buffers: the texel type
    /// (offsets and strides are 0). Null for samplers and byte-address buffers.
    /// </summary>
    public ReflectedType? Layout { get; init; }
}

/// <summary>
/// A push-constant block (<c>[[vk::push_constant]]</c>): a small struct the host writes per draw or dispatch with no descriptor.
/// Its layout is the same bytes on every target, so one CPU struct fills it everywhere; only the way the host delivers it differs.
/// </summary>
public sealed class PushConstantInfo
{
    /// <summary>The HLSL variable name.</summary>
    public required string Name { get; init; }

    /// <summary>Stages whose entry points read it: the stage flags of the Vulkan push-constant range.</summary>
    public ShaderStages Stages { get; init; }

    /// <summary>
    /// The struct layout; its <see cref="ReflectedType.Size"/> is the number of bytes to push. Members follow the vector-relaxed
    /// std430 rules (a <c>float3</c> may share a 16-byte row with the next scalar), which is what DXC emits for push constants.
    /// </summary>
    public required ReflectedType Layout { get; init; }

    /// <summary>
    /// Where each enabled target takes the bytes, in <see cref="CompileTarget"/> order. SPIR-V has no slot (the block is pushed with
    /// <c>vkCmdPushConstants</c>). MSL: <see cref="TargetSlot.Binding"/> is the <c>[[buffer(n)]]</c> index to fill with
    /// <c>setVertexBytes</c>, <c>setFragmentBytes</c> or <c>setBytes</c>. WGSL: a uniform buffer at <c>@group(Set) @binding(Binding)</c>,
    /// at least <see cref="ReflectedType.Size"/> rounded up to 16 bytes long.
    /// </summary>
    public IReadOnlyList<TargetSlot> Slots { get; init; } = [];
}

/// <summary>An input or output of an entry point.</summary>
public sealed class StageVariable
{
    /// <summary>The HLSL name, for example <c>Position</c> (flattened as <c>i.Position</c> when it came from a struct parameter).</summary>
    public required string Name { get; init; }

    /// <summary>The semantic name without index (<c>TEXCOORD</c>, <c>SV_Target</c>).</summary>
    public string Semantic { get; init; } = "";

    /// <summary>The semantic index (<c>1</c> for <c>TEXCOORD1</c>).</summary>
    public uint SemanticIndex { get; init; }

    /// <summary>The user location; null for built-ins.</summary>
    public uint? Location { get; init; }

    /// <summary>The built-in this maps to.</summary>
    public BuiltIn BuiltIn { get; init; }

    /// <summary>The component scalar type.</summary>
    public ScalarKind Scalar { get; init; }

    /// <summary>Number of components, 1 to 4.</summary>
    public uint Components { get; init; } = 1;

    /// <summary>Vertex inputs: the vertex format; null elsewhere.</summary>
    public VertexFormat? Format { get; init; }

    /// <summary>Vertex inputs: byte offset inside the vertex; 0 elsewhere.</summary>
    public uint Offset { get; init; }

    /// <summary>Interpolation of a vertex output / pixel input.</summary>
    public Interpolation Interpolation { get; init; }
}

/// <summary>One entry point and its interface.</summary>
public sealed class EntryPointInfo
{
    /// <summary>The function name.</summary>
    public required string Name { get; init; }

    /// <summary>The stage.</summary>
    public required ShaderStage Stage { get; init; }

    /// <summary>Stage inputs, in declaration order.</summary>
    public IReadOnlyList<StageVariable> Inputs { get; init; } = [];

    /// <summary>Stage outputs, in declaration order.</summary>
    public IReadOnlyList<StageVariable> Outputs { get; init; } = [];

    /// <summary>Compute: the <c>[numthreads]</c> sizes; zeros for other stages.</summary>
    public (uint X, uint Y, uint Z) WorkgroupSize { get; init; }

    /// <summary>Vertex: bytes of one interleaved vertex made of <see cref="Inputs"/>; 0 elsewhere.</summary>
    public uint VertexStride { get; init; }

    /// <summary>Names of the resources this entry point uses (a subset of <see cref="ShaderReflection.Resources"/>).</summary>
    public IReadOnlyList<string> UsedResources { get; init; } = [];

    /// <summary>The name of the push-constant block this entry point reads (an entry of <see cref="ShaderReflection.PushConstants"/>), or null.</summary>
    public string? PushConstant { get; init; }
}

/// <summary>Everything tooling and generated code need to know about one compiled shader file.</summary>
public sealed class ShaderReflection
{
    /// <summary>Current version of the serialized form.</summary>
    public const int FormatVersion = 1;

    /// <summary>The source file's name as given to the compiler (no directory is added).</summary>
    public string SourceName { get; init; } = "";

    /// <summary>The targets that were compiled, in <see cref="CompileTarget"/> order.</summary>
    public IReadOnlyList<CompileTarget> Targets { get; init; } = [];

    /// <summary>Resources in declaration order.</summary>
    public IReadOnlyList<ResourceInfo> Resources { get; init; } = [];

    /// <summary>Push-constant blocks in declaration order; an entry point reads at most one. They are not in <see cref="Resources"/>.</summary>
    public IReadOnlyList<PushConstantInfo> PushConstants { get; init; } = [];

    /// <summary>Entry points in declaration order.</summary>
    public IReadOnlyList<EntryPointInfo> EntryPoints { get; init; } = [];
}
