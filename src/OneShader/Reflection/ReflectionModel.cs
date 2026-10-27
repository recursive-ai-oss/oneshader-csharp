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

    /// <summary>The lane's index in its subgroup (<c>WaveGetLaneIndex</c>). Never a shader's own stage variable; the compiler adds it when a wave intrinsic needs it.</summary>
    SubgroupLocalInvocationId,

    /// <summary>The number of lanes in the subgroup (<c>WaveGetLaneCount</c>). Never a shader's own stage variable.</summary>
    SubgroupSize,
}

/// <summary>
/// Kinds of subgroup (wave) operations a shader uses. The values equal Vulkan's <c>VkSubgroupFeatureFlagBits</c>, so a Vulkan host can test
/// them against <c>subgroupSupportedOperations</c> directly. A Metal host needs a GPU family with SIMD-group functions (all Apple
/// silicon and the Macs Metal 2.1 supports); a WebGPU host needs the <c>subgroups</c> feature.
/// </summary>
[Flags]
public enum SubgroupOperations
{
    /// <summary>No subgroup operation.</summary>
    None = 0,

    /// <summary>Lane index, lane count, <c>WaveIsFirstLane</c>.</summary>
    Basic = 1,

    /// <summary><c>WaveActiveAnyTrue</c>, <c>WaveActiveAllTrue</c>, <c>WaveActiveAllEqual</c>.</summary>
    Vote = 2,

    /// <summary><c>WaveActiveSum</c>, <c>Min</c>, <c>Max</c>, bit operations and the <c>WavePrefix*</c> scans.</summary>
    Arithmetic = 4,

    /// <summary><c>WaveActiveBallot</c>, <c>WaveActiveCountBits</c>, <c>WavePrefixCountBits</c>, <c>WaveReadLaneFirst</c>.</summary>
    Ballot = 8,

    /// <summary><c>WaveReadLaneAt</c>.</summary>
    Shuffle = 16,

    /// <summary>Quad operations: <c>QuadReadAcross*</c>, <c>QuadReadLaneAt</c>.</summary>
    Quad = 128,
}

/// <summary>
/// Optional device features a shader needs because of the scalar types it uses. The Vulkan features are the ones the SPIR-V
/// capabilities require; a WebGPU host needs <c>shader-f16</c> for <see cref="Float16"/>. Metal has no feature switches for these types
/// (it has no 64-bit float, and <see cref="Int64Atomics"/> needs an Apple GPU family that has 64-bit atomics).
/// </summary>
[Flags]
public enum DeviceFeatures
{
    /// <summary>Nothing beyond 32-bit scalars.</summary>
    None = 0,

    /// <summary>16-bit floating-point arithmetic (Vulkan <c>shaderFloat16</c>, WebGPU <c>shader-f16</c>).</summary>
    Float16 = 1,

    /// <summary>16-bit integer arithmetic (Vulkan <c>shaderInt16</c>).</summary>
    Int16 = 2,

    /// <summary>64-bit floating point (Vulkan <c>shaderFloat64</c>).</summary>
    Float64 = 4,

    /// <summary>64-bit integers (Vulkan <c>shaderInt64</c>).</summary>
    Int64 = 8,

    /// <summary>16-bit values in storage buffers (Vulkan <c>storageBuffer16BitAccess</c>).</summary>
    StorageBuffer16 = 16,

    /// <summary>16-bit values in uniform buffers (Vulkan <c>uniformAndStorageBuffer16BitAccess</c>).</summary>
    UniformBuffer16 = 32,

    /// <summary>16-bit values in push constants (Vulkan <c>storagePushConstant16</c>).</summary>
    PushConstant16 = 64,

    /// <summary>16-bit values as stage inputs or outputs (Vulkan <c>storageInputOutput16</c>).</summary>
    InputOutput16 = 128,

    /// <summary>64-bit atomics (Vulkan <c>shaderBufferInt64Atomics</c> / <c>shaderSharedInt64Atomics</c>).</summary>
    Int64Atomics = 256,
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

/// <summary>How a specialization constant is named and numbered for one target.</summary>
/// <param name="Target">The backend.</param>
/// <param name="Index">
/// SPIR-V: the <c>SpecId</c> (the <c>constant_id</c>). Metal: the <c>[[function_constant(n)]]</c> index. WebGPU: the <c>@id(n)</c>; the
/// pipeline's <c>constants</c> record is keyed by its decimal text. All three equal the HLSL <c>constant_id</c>.
/// </param>
/// <param name="Name">
/// The name in that target's source: the SPIR-V <c>OpName</c> (present unless names are stripped), the Metal function-constant variable
/// (usable with <c>setConstantValue:type:withName:</c>) and the WGSL <c>override</c> identifier.
/// </param>
public readonly record struct SpecConstantSlot(CompileTarget Target, uint Index, string Name);

/// <summary>
/// A specialization constant (<c>[[vk::constant_id(N)]] const T Name = default;</c>): a scalar the host sets when it builds the pipeline,
/// without recompiling the shader. Only <c>bool</c>, <c>int</c>, <c>uint</c> and <c>float</c> are allowed.
/// </summary>
public sealed class SpecConstantInfo
{
    /// <summary>The HLSL name.</summary>
    public required string Name { get; init; }

    /// <summary>The <c>constant_id</c>.</summary>
    public required uint Id { get; init; }

    /// <summary>The scalar type: <see cref="ScalarKind.Bool"/>, <see cref="ScalarKind.Int"/>, <see cref="ScalarKind.UInt"/> or <see cref="ScalarKind.Float"/>.</summary>
    public required ScalarKind Scalar { get; init; }

    /// <summary>The default as the 32-bit pattern the host would write: 0 or 1 for bool, two's complement for int, IEEE bits for float.</summary>
    public uint DefaultBits { get; init; }

    /// <summary>The default as a double (bool as 0 or 1), which is exact for all four types.</summary>
    public double DefaultValue => Scalar switch
    {
        ScalarKind.Float => BitConverter.UInt32BitsToSingle(DefaultBits),
        ScalarKind.Int => (int)DefaultBits,
        _ => DefaultBits,
    };

    /// <summary>One entry per enabled target, in <see cref="CompileTarget"/> order.</summary>
    public IReadOnlyList<SpecConstantSlot> Slots { get; init; } = [];
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

    /// <summary>Names of the specialization constants this entry point (and the functions it calls) reads, a subset of <see cref="ShaderReflection.SpecConstants"/>.</summary>
    public IReadOnlyList<string> UsedSpecConstants { get; init; } = [];

    /// <summary>The subgroup operations this entry point (and the functions it calls) uses; <see cref="SubgroupOperations.None"/> when it uses none.</summary>
    public SubgroupOperations SubgroupOperations { get; init; }

    /// <summary>True when the entry point needs subgroup support from the device (Vulkan subgroup operations, Metal SIMD-group functions, the WebGPU <c>subgroups</c> feature).</summary>
    public bool UsesSubgroups => SubgroupOperations != SubgroupOperations.None;

    /// <summary>The optional device features this entry point needs for 16-bit and 64-bit types; <see cref="DeviceFeatures.None"/> when it uses only 32-bit scalars.</summary>
    public DeviceFeatures RequiredFeatures { get; init; }
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

    /// <summary>Specialization constants in declaration order.</summary>
    public IReadOnlyList<SpecConstantInfo> SpecConstants { get; init; } = [];

    /// <summary>Entry points in declaration order.</summary>
    public IReadOnlyList<EntryPointInfo> EntryPoints { get; init; } = [];

    /// <summary>True when any entry point uses subgroup operations; a WebGPU host must then request the <c>subgroups</c> feature.</summary>
    public bool UsesSubgroups
    {
        get
        {
            foreach (var e in EntryPoints)
            {
                if (e.UsesSubgroups)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
