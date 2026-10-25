using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OneShader.Types;

/// <summary>The family of an <see cref="HlslType"/>.</summary>
public enum TypeKind
{
    /// <summary><c>void</c>.</summary>
    Void,

    /// <summary>The type of an expression that already produced an error; compatible with everything so one mistake reports once.</summary>
    Error,

    /// <summary>A scalar.</summary>
    Scalar,

    /// <summary>A vector of 1 to 4 scalars.</summary>
    Vector,

    /// <summary>A matrix of up to 4x4 scalars.</summary>
    Matrix,

    /// <summary>A fixed-size or unsized array.</summary>
    Array,

    /// <summary>A <c>struct</c> or <c>class</c>.</summary>
    Struct,

    /// <summary>An <c>enum</c>.</summary>
    Enum,

    /// <summary>A texture, buffer or constant buffer.</summary>
    Resource,

    /// <summary>A sampler state.</summary>
    Sampler,

    /// <summary>A pointer; exists only in the IR.</summary>
    Pointer,

    /// <summary>A template parameter that has not been substituted yet.</summary>
    TemplateParameter,
}

/// <summary>The scalar types of HLSL, including the untyped literal types used during type checking.</summary>
public enum ScalarKind
{
    /// <summary><c>bool</c>.</summary>
    Bool,

    /// <summary><c>int</c> (32-bit signed).</summary>
    Int,

    /// <summary><c>uint</c> (32-bit unsigned).</summary>
    UInt,

    /// <summary><c>int16_t</c>.</summary>
    Int16,

    /// <summary><c>uint16_t</c>.</summary>
    UInt16,

    /// <summary><c>int64_t</c>.</summary>
    Int64,

    /// <summary><c>uint64_t</c>.</summary>
    UInt64,

    /// <summary><c>half</c>: 16 bits with 16-bit types enabled, otherwise stored as 32.</summary>
    Half,

    /// <summary><c>float</c> (32-bit).</summary>
    Float,

    /// <summary><c>double</c> (64-bit).</summary>
    Double,

    /// <summary><c>min16float</c>.</summary>
    Min16Float,

    /// <summary><c>min10float</c>.</summary>
    Min10Float,

    /// <summary><c>min16int</c>.</summary>
    Min16Int,

    /// <summary><c>min16uint</c>.</summary>
    Min16UInt,

    /// <summary>An integer literal that has not been given a concrete type yet.</summary>
    LiteralInt,

    /// <summary>A floating-point literal that has not been given a concrete type yet.</summary>
    LiteralFloat,
}

/// <summary>Row- or column-major storage of a matrix in a buffer.</summary>
public enum MatrixPacking
{
    /// <summary>HLSL's default: consecutive columns are the stored vectors.</summary>
    ColumnMajor = 0,

    /// <summary>Consecutive rows are the stored vectors.</summary>
    RowMajor = 1,
}

/// <summary>Where a pointer's memory lives (IR only), following SPIR-V's logical storage classes.</summary>
public enum AddressSpace
{
    /// <summary>Function-local variable.</summary>
    Function,

    /// <summary>Module-scope private variable (HLSL static globals).</summary>
    Private,

    /// <summary>Workgroup shared memory (<c>groupshared</c>).</summary>
    Workgroup,

    /// <summary>Constant buffer memory.</summary>
    Uniform,

    /// <summary>Storage buffer memory.</summary>
    StorageBuffer,

    /// <summary>Opaque handles: textures, samplers, typed buffers.</summary>
    UniformConstant,

    /// <summary>Stage input.</summary>
    Input,

    /// <summary>Stage output.</summary>
    Output,
}

/// <summary>
/// Base of the HLSL type system, shared by the front end and the IR. Scalars, vectors, matrices,
/// arrays, pointers and resource types compare structurally; structs, enums and template
/// parameters compare by identity.
/// </summary>
public abstract class HlslType : IEquatable<HlslType>
{
    private protected HlslType()
    {
    }

    /// <summary>The family of this type.</summary>
    public abstract TypeKind Kind { get; }

    /// <summary>True for <see cref="TypeKind.Scalar"/>.</summary>
    public bool IsScalar => Kind == TypeKind.Scalar;

    /// <summary>True for <see cref="TypeKind.Vector"/>.</summary>
    public bool IsVector => Kind == TypeKind.Vector;

    /// <summary>True for <see cref="TypeKind.Matrix"/>.</summary>
    public bool IsMatrix => Kind == TypeKind.Matrix;

    /// <summary>True for <see cref="TypeKind.Void"/>.</summary>
    public bool IsVoid => Kind == TypeKind.Void;

    /// <summary>True for <see cref="TypeKind.Error"/>.</summary>
    public bool IsError => Kind == TypeKind.Error;

    /// <summary>The <c>void</c> type.</summary>
    public static HlslType Void { get; } = new VoidType();

    /// <summary>The type of erroneous expressions.</summary>
    public static HlslType Error { get; } = new ErrorType();

    /// <summary>Makes a vector type; <paramref name="count"/> is 1 to 4.</summary>
    public static VectorType Vector(ScalarType element, int count) => new(element, count);

    /// <summary>Makes a matrix type with the given rows and columns, each 1 to 4.</summary>
    public static MatrixType Matrix(ScalarType element, int rows, int columns) => new(element, rows, columns);

    /// <summary>Makes an array type; <paramref name="length"/> 0 means unsized.</summary>
    public static ArrayType Array(HlslType element, int length) => new(element, length);

    /// <summary>The scalar type at the bottom of scalars, vectors and matrices; null for other types.</summary>
    public ScalarType? ScalarElement => this switch
    {
        ScalarType s => s,
        VectorType v => v.Element,
        MatrixType m => m.Element,
        _ => null,
    };

    /// <inheritdoc/>
    public virtual bool Equals(HlslType? other) => ReferenceEquals(this, other);

    /// <inheritdoc/>
    public sealed override bool Equals(object? obj) => obj is HlslType t && Equals(t);

    /// <inheritdoc/>
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    /// <summary>Compares two types.</summary>
    public static bool operator ==(HlslType? a, HlslType? b) => a is null ? b is null : a.Equals(b);

    /// <summary>Compares two types.</summary>
    public static bool operator !=(HlslType? a, HlslType? b) => !(a == b);

    /// <summary>The type spelled the way HLSL spells it (IR-only types use a readable form). Deterministic.</summary>
    public abstract override string ToString();
}

internal sealed class VoidType : HlslType
{
    public override TypeKind Kind => TypeKind.Void;

    public override string ToString() => "void";
}

internal sealed class ErrorType : HlslType
{
    public override TypeKind Kind => TypeKind.Error;

    public override string ToString() => "<error>";
}

/// <summary>A scalar type. One shared instance exists per <see cref="ScalarKind"/>.</summary>
public sealed class ScalarType : HlslType
{
    private static readonly ScalarType[] All = CreateAll();

    private ScalarType(ScalarKind scalarKind)
    {
        ScalarKind = scalarKind;
    }

    /// <summary>Which scalar this is.</summary>
    public ScalarKind ScalarKind { get; }

    /// <inheritdoc/>
    public override TypeKind Kind => TypeKind.Scalar;

    /// <summary><c>bool</c>.</summary>
    public static ScalarType Bool => Get(ScalarKind.Bool);

    /// <summary><c>int</c>.</summary>
    public static ScalarType Int => Get(ScalarKind.Int);

    /// <summary><c>uint</c>.</summary>
    public static ScalarType UInt => Get(ScalarKind.UInt);

    /// <summary><c>half</c>.</summary>
    public static ScalarType Half => Get(ScalarKind.Half);

    /// <summary><c>float</c>.</summary>
    public static ScalarType Float => Get(ScalarKind.Float);

    /// <summary><c>double</c>.</summary>
    public static ScalarType Double => Get(ScalarKind.Double);

    /// <summary>True for the floating-point scalars and the float literal type.</summary>
    public bool IsFloatingPoint => ScalarKind is ScalarKind.Half or ScalarKind.Float or ScalarKind.Double
        or ScalarKind.Min16Float or ScalarKind.Min10Float or ScalarKind.LiteralFloat;

    /// <summary>True for the integer scalars (not <c>bool</c>) and the integer literal type.</summary>
    public bool IsInteger => !IsFloatingPoint && ScalarKind != ScalarKind.Bool;

    /// <summary>True for signed integers.</summary>
    public bool IsSignedInteger => ScalarKind is ScalarKind.Int or ScalarKind.Int16 or ScalarKind.Int64
        or ScalarKind.Min16Int or ScalarKind.LiteralInt;

    /// <summary>True for the untyped literal types.</summary>
    public bool IsLiteral => ScalarKind is ScalarKind.LiteralInt or ScalarKind.LiteralFloat;

    /// <summary>The shared instance for a scalar kind.</summary>
    public static ScalarType Get(ScalarKind kind) => All[(int)kind];

    /// <summary>
    /// Bytes this scalar takes in a buffer. <c>half</c>, <c>min16*</c> and <c>min10float</c> take 2
    /// bytes only when 16-bit types are enabled (the DXC <c>-enable-16bit-types</c> behaviour) and
    /// 4 otherwise; <c>bool</c> takes 4. Literal types count as 32-bit.
    /// </summary>
    public int StorageBytes(bool enable16BitTypes) => ScalarKind switch
    {
        ScalarKind.Int16 or ScalarKind.UInt16 => 2,
        ScalarKind.Int64 or ScalarKind.UInt64 or ScalarKind.Double => 8,
        ScalarKind.Half or ScalarKind.Min16Float or ScalarKind.Min10Float or ScalarKind.Min16Int
            or ScalarKind.Min16UInt => enable16BitTypes ? 2 : 4,
        _ => 4,
    };

    /// <inheritdoc/>
    public override string ToString() => ScalarKind switch
    {
        ScalarKind.Bool => "bool",
        ScalarKind.Int => "int",
        ScalarKind.UInt => "uint",
        ScalarKind.Int16 => "int16_t",
        ScalarKind.UInt16 => "uint16_t",
        ScalarKind.Int64 => "int64_t",
        ScalarKind.UInt64 => "uint64_t",
        ScalarKind.Half => "half",
        ScalarKind.Float => "float",
        ScalarKind.Double => "double",
        ScalarKind.Min16Float => "min16float",
        ScalarKind.Min10Float => "min10float",
        ScalarKind.Min16Int => "min16int",
        ScalarKind.Min16UInt => "min16uint",
        ScalarKind.LiteralInt => "<int literal>",
        _ => "<float literal>",
    };

    private static ScalarType[] CreateAll()
    {
        var values = Enum.GetValues<ScalarKind>();
        var all = new ScalarType[values.Length];
        foreach (var k in values)
        {
            all[(int)k] = new ScalarType(k);
        }

        return all;
    }
}

/// <summary>A vector of 1 to 4 scalars, such as <c>float3</c>.</summary>
public sealed class VectorType : HlslType
{
    internal VectorType(ScalarType element, int count)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 4);
        Element = element;
        Count = count;
    }

    /// <summary>The component type.</summary>
    public ScalarType Element { get; }

    /// <summary>Number of components, 1 to 4.</summary>
    public int Count { get; }

    /// <inheritdoc/>
    public override TypeKind Kind => TypeKind.Vector;

    /// <inheritdoc/>
    public override bool Equals(HlslType? other) => other is VectorType v && v.Element == Element && v.Count == Count;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(1, (int)Element.ScalarKind, Count);

    /// <inheritdoc/>
    public override string ToString() => Element + Count.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A matrix, such as <c>float4x3</c> (4 rows, 3 columns).</summary>
public sealed class MatrixType : HlslType
{
    internal MatrixType(ScalarType element, int rows, int columns)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, 4);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, 4);
        Element = element;
        Rows = rows;
        Columns = columns;
    }

    /// <summary>The element type.</summary>
    public ScalarType Element { get; }

    /// <summary>Number of rows, 1 to 4.</summary>
    public int Rows { get; }

    /// <summary>Number of columns, 1 to 4.</summary>
    public int Columns { get; }

    /// <inheritdoc/>
    public override TypeKind Kind => TypeKind.Matrix;

    /// <inheritdoc/>
    public override bool Equals(HlslType? other) =>
        other is MatrixType m && m.Element == Element && m.Rows == Rows && m.Columns == Columns;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(2, (int)Element.ScalarKind, Rows, Columns);

    /// <inheritdoc/>
    public override string ToString() =>
        Element + Rows.ToString(CultureInfo.InvariantCulture) + "x" + Columns.ToString(CultureInfo.InvariantCulture);
}

/// <summary>An array. <see cref="Length"/> 0 means unsized (runtime-sized in a buffer).</summary>
public sealed class ArrayType : HlslType
{
    internal ArrayType(HlslType element, int length)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Element = element;
        Length = length;
    }

    /// <summary>The element type.</summary>
    public HlslType Element { get; }

    /// <summary>Element count; 0 for an unsized array.</summary>
    public int Length { get; }

    /// <inheritdoc/>
    public override TypeKind Kind => TypeKind.Array;

    /// <inheritdoc/>
    public override bool Equals(HlslType? other) => other is ArrayType a && a.Length == Length && a.Element.Equals(Element);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(3, Element.GetHashCode(), Length);

    /// <inheritdoc/>
    public override string ToString() => Element + (Length == 0 ? "[]" : "[" + Length.ToString(CultureInfo.InvariantCulture) + "]");
}

/// <summary>How a stage variable is interpolated between vertex and pixel stages.</summary>
public enum Interpolation
{
    /// <summary>Perspective-correct interpolation (the default).</summary>
    Default,

    /// <summary><c>nointerpolation</c>: flat.</summary>
    Flat,

    /// <summary><c>noperspective</c>.</summary>
    NoPerspective,

    /// <summary><c>centroid</c>.</summary>
    Centroid,

    /// <summary><c>sample</c>.</summary>
    Sample,
}

/// <summary>One member of a struct, with everything the layout and interface rules need.</summary>
public sealed class StructField
{
    /// <summary>Creates a field.</summary>
    public StructField(string name, HlslType type)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(type);
        Name = name;
        Type = type;
    }

    /// <summary>The member name.</summary>
    public string Name { get; }

    /// <summary>The member type.</summary>
    public HlslType Type { get; }

    /// <summary>The semantic after the colon (<c>POSITION</c>, <c>SV_Target1</c>), or null.</summary>
    public string? Semantic { get; init; }

    /// <summary>Interpolation modifier.</summary>
    public Interpolation Interpolation { get; init; }

    /// <summary>A <c>row_major</c> / <c>column_major</c> written on this member; null inherits the default or the enclosing declaration.</summary>
    public MatrixPacking? MatrixPacking { get; init; }

    /// <summary>
    /// Byte offset from <c>[[vk::offset]]</c> or <c>packoffset</c> (register <c>cN.y</c> is
    /// <c>N*16 + 4</c>); null when the layout rules place the member.
    /// </summary>
    public int? ExplicitOffset { get; init; }

    /// <summary>The <c>[[vk::location]]</c> value, or null.</summary>
    public int? Location { get; init; }

    /// <summary>A vertex format name written as <c>[[oneshader::format("unorm8x4")]]</c>, or null when the format follows the type.</summary>
    public string? VertexFormat { get; init; }
}

/// <summary>A struct or class. Compared by identity: two structs with the same name are different types.</summary>
public sealed class StructType : HlslType
{
    private IReadOnlyList<StructField> _fields = [];

    /// <summary>Creates a struct with no fields yet; call <see cref="SetFields"/> once they are known.</summary>
    public StructType(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
    }

    /// <summary>Creates a struct with its fields.</summary>
    public StructType(string name, IReadOnlyList<StructField> fields)
        : this(name)
    {
        SetFields(fields);
    }

    /// <summary>The declared name; may be empty for an anonymous struct or a cbuffer's member block.</summary>
    public string Name { get; }

    /// <summary>The members in declaration order.</summary>
    public IReadOnlyList<StructField> Fields => _fields;

    /// <summary>The base struct of an inheriting struct, which is also its first member; null otherwise.</summary>
    internal StructType? BaseStruct { get; set; }

    /// <summary>True when <paramref name="ancestor"/> is this struct's base, or the base of its base, and so on.</summary>
    internal bool IsDerivedFrom(StructType ancestor)
    {
        for (var b = BaseStruct; b is not null; b = b.BaseStruct)
        {
            if (ReferenceEquals(b, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public override TypeKind Kind => TypeKind.Struct;

    /// <summary>Sets the members, once the declaration has been analysed. Declared separately so a struct can exist while its members are still being typed.</summary>
    public void SetFields(IReadOnlyList<StructField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        _fields = fields;
    }

    /// <summary>Index of the member called <paramref name="name"/>, or -1.</summary>
    public int IndexOf(string name)
    {
        for (int i = 0; i < _fields.Count; i++)
        {
            if (_fields[i].Name == name)
            {
                return i;
            }
        }

        return -1;
    }

    /// <inheritdoc/>
    public override string ToString() => Name.Length == 0 ? "<anonymous struct>" : Name;
}

/// <summary>An enum. Compared by identity.</summary>
public sealed class EnumType : HlslType
{
    /// <summary>Creates an enum.</summary>
    public EnumType(string name, ScalarType underlying, IReadOnlyList<KeyValuePair<string, long>> members)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(underlying);
        ArgumentNullException.ThrowIfNull(members);
        Name = name;
        Underlying = underlying;
        Members = members;
    }

    /// <summary>The enum name.</summary>
    public string Name { get; }

    /// <summary>The integer type that stores values (int unless declared otherwise).</summary>
    public ScalarType Underlying { get; }

    /// <summary>Enumerators with their values, in declaration order.</summary>
    public IReadOnlyList<KeyValuePair<string, long>> Members { get; }

    /// <inheritdoc/>
    public override TypeKind Kind => TypeKind.Enum;

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>The built-in resource type families.</summary>
public enum ResourceTypeKind
{
    /// <summary><c>Texture1D</c>.</summary>
    Texture1D,

    /// <summary><c>Texture1DArray</c>.</summary>
    Texture1DArray,

    /// <summary><c>Texture2D</c>.</summary>
    Texture2D,

    /// <summary><c>Texture2DArray</c>.</summary>
    Texture2DArray,

    /// <summary><c>Texture2DMS</c>.</summary>
    Texture2DMS,

    /// <summary><c>Texture2DMSArray</c>.</summary>
    Texture2DMSArray,

    /// <summary><c>Texture3D</c>.</summary>
    Texture3D,

    /// <summary><c>TextureCube</c>.</summary>
    TextureCube,

    /// <summary><c>TextureCubeArray</c>.</summary>
    TextureCubeArray,

    /// <summary><c>RWTexture1D</c>.</summary>
    RWTexture1D,

    /// <summary><c>RWTexture1DArray</c>.</summary>
    RWTexture1DArray,

    /// <summary><c>RWTexture2D</c>.</summary>
    RWTexture2D,

    /// <summary><c>RWTexture2DArray</c>.</summary>
    RWTexture2DArray,

    /// <summary><c>RWTexture3D</c>.</summary>
    RWTexture3D,

    /// <summary><c>Buffer&lt;T&gt;</c>.</summary>
    Buffer,

    /// <summary><c>RWBuffer&lt;T&gt;</c>.</summary>
    RWBuffer,

    /// <summary><c>StructuredBuffer&lt;T&gt;</c>.</summary>
    StructuredBuffer,

    /// <summary><c>RWStructuredBuffer&lt;T&gt;</c>.</summary>
    RWStructuredBuffer,

    /// <summary><c>AppendStructuredBuffer&lt;T&gt;</c>.</summary>
    AppendStructuredBuffer,

    /// <summary><c>ConsumeStructuredBuffer&lt;T&gt;</c>.</summary>
    ConsumeStructuredBuffer,

    /// <summary><c>ByteAddressBuffer</c>.</summary>
    ByteAddressBuffer,

    /// <summary><c>RWByteAddressBuffer</c>.</summary>
    RWByteAddressBuffer,

    /// <summary><c>ConstantBuffer&lt;T&gt;</c>, also the type behind a <c>cbuffer</c> declaration.</summary>
    ConstantBuffer,
}

/// <summary>
/// A texture, typed buffer, structured buffer, byte-address buffer or constant buffer.
/// <see cref="Element"/> is the template argument (<c>float4</c> for <c>Texture2D&lt;float4&gt;</c>,
/// the struct for <c>ConstantBuffer&lt;S&gt;</c>); null for byte-address buffers.
/// </summary>
public sealed class ResourceType : HlslType
{
    /// <summary>Creates a resource type.</summary>
    /// <param name="resourceKind">Which family.</param>
    /// <param name="element">The element type, or null where the family has none.</param>
    /// <param name="sampleCount">For multisampled textures, the declared sample count (0 if unspecified).</param>
    public ResourceType(ResourceTypeKind resourceKind, HlslType? element, int sampleCount = 0)
    {
        ResourceKind = resourceKind;
        Element = element;
        SampleCount = sampleCount;
    }

    /// <summary>Which family.</summary>
    public ResourceTypeKind ResourceKind { get; }

    /// <summary>The element type, or null.</summary>
    public HlslType? Element { get; }

    /// <summary>Declared sample count of a multisampled texture, 0 otherwise.</summary>
    public int SampleCount { get; }

    /// <inheritdoc/>
    public override TypeKind Kind => TypeKind.Resource;

    /// <summary>True for a texture of any dimension.</summary>
    public bool IsTexture => ResourceKind <= ResourceTypeKind.RWTexture3D;

    /// <summary>True for a texture or buffer that the shader may write.</summary>
    public bool IsReadWrite => ResourceKind is ResourceTypeKind.RWTexture1D or ResourceTypeKind.RWTexture1DArray
        or ResourceTypeKind.RWTexture2D or ResourceTypeKind.RWTexture2DArray or ResourceTypeKind.RWTexture3D
        or ResourceTypeKind.RWBuffer or ResourceTypeKind.RWStructuredBuffer or ResourceTypeKind.AppendStructuredBuffer
        or ResourceTypeKind.ConsumeStructuredBuffer or ResourceTypeKind.RWByteAddressBuffer;

    /// <summary>True for structured and byte-address buffers.</summary>
    public bool IsStorageBuffer => ResourceKind is >= ResourceTypeKind.StructuredBuffer and <= ResourceTypeKind.RWByteAddressBuffer;

    /// <inheritdoc/>
    public override bool Equals(HlslType? other) =>
        other is ResourceType r && r.ResourceKind == ResourceKind && r.SampleCount == SampleCount
        && (Element is null ? r.Element is null : Element.Equals(r.Element));

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(4, (int)ResourceKind, Element?.GetHashCode() ?? 0, SampleCount);

    /// <inheritdoc/>
    public override string ToString()
    {
        if (Element is null)
        {
            return ResourceKind.ToString();
        }

        var sb = new StringBuilder(ResourceKind.ToString()).Append('<').Append(Element);
        if (SampleCount > 0)
        {
            sb.Append(", ").Append(SampleCount.ToString(CultureInfo.InvariantCulture));
        }

        return sb.Append('>').ToString();
    }
}

/// <summary><c>SamplerState</c> or <c>SamplerComparisonState</c>.</summary>
public sealed class SamplerType : HlslType
{
    private SamplerType(bool isComparison)
    {
        IsComparison = isComparison;
    }

    /// <summary><c>SamplerState</c>.</summary>
    public static SamplerType State { get; } = new(false);

    /// <summary><c>SamplerComparisonState</c>.</summary>
    public static SamplerType Comparison { get; } = new(true);

    /// <summary>True for <c>SamplerComparisonState</c>.</summary>
    public bool IsComparison { get; }

    /// <inheritdoc/>
    public override TypeKind Kind => TypeKind.Sampler;

    /// <inheritdoc/>
    public override string ToString() => IsComparison ? "SamplerComparisonState" : "SamplerState";
}

/// <summary>A pointer to memory in an address space. Only the IR uses pointers; HLSL has none.</summary>
public sealed class PointerType : HlslType
{
    /// <summary>Creates a pointer type.</summary>
    public PointerType(AddressSpace space, HlslType pointee)
    {
        ArgumentNullException.ThrowIfNull(pointee);
        Space = space;
        Pointee = pointee;
    }

    /// <summary>The address space.</summary>
    public AddressSpace Space { get; }

    /// <summary>The type of the pointed-to value.</summary>
    public HlslType Pointee { get; }

    /// <inheritdoc/>
    public override TypeKind Kind => TypeKind.Pointer;

    /// <inheritdoc/>
    public override bool Equals(HlslType? other) => other is PointerType p && p.Space == Space && p.Pointee.Equals(Pointee);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(5, (int)Space, Pointee.GetHashCode());

    /// <inheritdoc/>
    public override string ToString() => "ptr<" + Space.ToString().ToLowerInvariant() + ", " + Pointee + ">";
}

/// <summary>A template parameter inside a template definition, before instantiation. Compared by identity.</summary>
public sealed class TemplateParameterType : HlslType
{
    /// <summary>Creates a template parameter type.</summary>
    public TemplateParameterType(string name, int index)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
        Index = index;
    }

    /// <summary>The parameter name.</summary>
    public string Name { get; }

    /// <summary>Position in the template parameter list.</summary>
    public int Index { get; }

    /// <inheritdoc/>
    public override TypeKind Kind => TypeKind.TemplateParameter;

    /// <inheritdoc/>
    public override string ToString() => Name;
}
