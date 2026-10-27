using System;
using System.Collections.Generic;
using System.Globalization;
using OneShader.Diagnostics;
using OneShader.Reflection;
using OneShader.Types;

namespace OneShader.IR;

/// <summary>Anything an instruction can use as an operand: constants, arguments, globals and instruction results.</summary>
public abstract class IrValue
{
    private protected IrValue(HlslType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        Type = type;
    }

    /// <summary>The value's type. Pointer-typed for globals and <c>alloca</c>.</summary>
    public HlslType Type { get; internal set; }

    /// <summary>A source-level name for dumps and for names in generated code; null when anonymous. Not part of identity.</summary>
    public string? Name { get; set; }
}

/// <summary>What a <see cref="Constant"/> holds.</summary>
public enum ConstantKind
{
    /// <summary>A scalar; the raw bits are in <see cref="Constant.Bits"/>.</summary>
    Scalar,

    /// <summary>A vector, matrix (elements are the rows), array or struct built from <see cref="Constant.Elements"/>.</summary>
    Composite,

    /// <summary>An undefined value of the type; any value may be assumed.</summary>
    Undef,
}

/// <summary>
/// A constant. Scalars hold their exact bit pattern (IEEE bits for floats, two's complement for
/// integers, 0/1 for bool), so folding and printing are exact and deterministic.
/// </summary>
public sealed class Constant : IrValue
{
    private Constant(HlslType type, ConstantKind kind, ulong bits, IReadOnlyList<Constant> elements)
        : base(type)
    {
        ConstantKind = kind;
        Bits = bits;
        Elements = elements;
    }

    /// <summary>What kind of constant this is.</summary>
    public ConstantKind ConstantKind { get; }

    /// <summary>For scalars: the value's bits, zero-extended to 64.</summary>
    public ulong Bits { get; }

    /// <summary>For composites: one constant per vector component, matrix row, array element or struct member.</summary>
    public IReadOnlyList<Constant> Elements { get; }

    /// <summary>A bool constant.</summary>
    public static Constant Bool(bool value) => new(ScalarType.Bool, ConstantKind.Scalar, value ? 1UL : 0UL, []);

    /// <summary>An <c>int</c> constant.</summary>
    public static Constant Int(int value) => new(ScalarType.Int, ConstantKind.Scalar, (uint)value, []);

    /// <summary>A <c>uint</c> constant.</summary>
    public static Constant UInt(uint value) => new(ScalarType.UInt, ConstantKind.Scalar, value, []);

    /// <summary>A <c>float</c> constant.</summary>
    public static Constant Float(float value) => new(ScalarType.Float, ConstantKind.Scalar, BitConverter.SingleToUInt32Bits(value), []);

    /// <summary>A <c>double</c> constant.</summary>
    public static Constant Double(double value) => new(ScalarType.Double, ConstantKind.Scalar, BitConverter.DoubleToUInt64Bits(value), []);

    /// <summary>A scalar constant of any scalar type from raw bits (only the low bits that the type uses matter).</summary>
    public static Constant FromBits(ScalarType type, ulong bits)
    {
        ArgumentNullException.ThrowIfNull(type);
        return new Constant(type, ConstantKind.Scalar, bits, []);
    }

    /// <summary>A composite constant.</summary>
    public static Constant Composite(HlslType type, IReadOnlyList<Constant> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        return new Constant(type, ConstantKind.Composite, 0, elements);
    }

    /// <summary>An undefined value.</summary>
    public static Constant Undef(HlslType type) => new(type, ConstantKind.Undef, 0, []);

    /// <summary>The all-zero value of a scalar, vector, matrix, array (up to 4096 elements) or struct type.</summary>
    public static Constant Zero(HlslType type) => Zero(type, 0);

    private static Constant Zero(HlslType type, int depth)
    {
        if (depth > 64)
        {
            return Undef(type);
        }

        switch (type)
        {
            case ScalarType:
                return new Constant(type, ConstantKind.Scalar, 0, []);
            case VectorType v:
            {
                var z = Zero(v.Element, depth + 1);
                var list = new Constant[v.Count];
                System.Array.Fill(list, z);
                return Composite(type, list);
            }

            case MatrixType m:
            {
                var row = Zero(HlslType.Vector(m.Element, m.Columns), depth + 1);
                var list = new Constant[m.Rows];
                System.Array.Fill(list, row);
                return Composite(type, list);
            }

            case ArrayType a when a.Length is > 0 and <= 4096:
            {
                var z = Zero(a.Element, depth + 1);
                var list = new Constant[a.Length];
                System.Array.Fill(list, z);
                return Composite(type, list);
            }

            case StructType s:
            {
                var list = new Constant[s.Fields.Count];
                for (int i = 0; i < list.Length; i++)
                {
                    list[i] = Zero(s.Fields[i].Type, depth + 1);
                }

                return Composite(type, list);
            }

            default:
                return Undef(type);
        }
    }

    /// <summary>The scalar as a float (bits reinterpreted when the type is 32-bit float, converted otherwise).</summary>
    public double AsDouble()
    {
        var s = Type as ScalarType;
        return s?.ScalarKind switch
        {
            ScalarKind.Double => BitConverter.UInt64BitsToDouble(Bits),
            ScalarKind.Float or ScalarKind.LiteralFloat => BitConverter.UInt32BitsToSingle((uint)Bits),
            ScalarKind.Half or ScalarKind.Min16Float or ScalarKind.Min10Float => (double)BitConverter.UInt16BitsToHalf((ushort)Bits),
            ScalarKind.Int or ScalarKind.Int16 or ScalarKind.Min16Int => (int)Bits,
            ScalarKind.Int64 => (long)Bits,
            _ => Bits,
        };
    }

    /// <summary>Structural equality of value and type; floats compare by bits so NaNs and signed zeros stay distinct.</summary>
    public bool ValueEquals(Constant other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ConstantKind != other.ConstantKind || !Type.Equals(other.Type))
        {
            return false;
        }

        if (ConstantKind == ConstantKind.Scalar)
        {
            return Bits == other.Bits;
        }

        if (Elements.Count != other.Elements.Count)
        {
            return false;
        }

        for (int i = 0; i < Elements.Count; i++)
        {
            if (!Elements[i].ValueEquals(other.Elements[i]))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// A specialization constant: a scalar the host sets when it creates the pipeline. It is a value like a constant, but never a
/// <see cref="Constant"/>, so no pass folds it to its default; the backends declare it in their target's native form.
/// </summary>
public sealed class SpecConstant : IrValue
{
    /// <summary>Creates a specialization constant.</summary>
    public SpecConstant(string name, uint id, Constant defaultValue)
        : base(defaultValue?.Type ?? throw new ArgumentNullException(nameof(defaultValue)))
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
        Id = id;
        Default = defaultValue;
    }

    /// <summary>The <c>[[vk::constant_id]]</c> number: SPIR-V <c>SpecId</c>, Metal function-constant index and WGSL <c>@id</c>.</summary>
    public uint Id { get; }

    /// <summary>The value used when the host sets nothing.</summary>
    public Constant Default { get; }

    /// <summary>Where it was declared.</summary>
    public SourceSpan Span { get; set; }
}

/// <summary>A function parameter. Out and inout parameters have pointer type.</summary>
public sealed class Argument : IrValue
{
    /// <summary>Creates a parameter.</summary>
    public Argument(HlslType type, string? name = null)
        : base(type)
    {
        Name = name;
    }

    /// <summary>Position in the parameter list.</summary>
    public int Index { get; internal set; }
}

/// <summary>Why a global exists and which side of the shader interface it is on.</summary>
public enum GlobalKind
{
    /// <summary>A <c>static</c> variable, private to the module.</summary>
    Private,

    /// <summary>A <c>groupshared</c> variable.</summary>
    Workgroup,

    /// <summary>A bound resource: constant buffer, texture, sampler or buffer.</summary>
    Resource,

    /// <summary>A stage input.</summary>
    Input,

    /// <summary>A stage output.</summary>
    Output,
}

/// <summary>
/// A module-scope variable. Its value is a pointer; <see cref="ValueType"/> is the pointee. Resources
/// use their <see cref="ResourceType"/> as value type, and access chains into a constant buffer or
/// structured buffer start inside the element type (see docs/ir.md).
/// </summary>
public sealed class GlobalVariable : IrValue
{
    /// <summary>Creates a global.</summary>
    public GlobalVariable(string name, GlobalKind kind, AddressSpace space, HlslType valueType)
        : base(new PointerType(space, valueType))
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
        GlobalKind = kind;
        ValueType = valueType;
    }

    /// <summary>The kind.</summary>
    public GlobalKind GlobalKind { get; }

    /// <summary>The pointee type.</summary>
    public HlslType ValueType { get; }

    /// <summary>Initializer for private variables; null means zero-initialized.</summary>
    public Constant? Initializer { get; set; }

    /// <summary>Descriptor set / register space, or null when unbound.</summary>
    public int? Set { get; set; }

    /// <summary>Binding number, or null when unbound.</summary>
    public int? Binding { get; set; }

    /// <summary>Array size of a resource array (1 for a single resource, 0 unbounded).</summary>
    public int ResourceCount { get; set; } = 1;

    /// <summary>User location for stage interface variables, or null.</summary>
    public int? Location { get; set; }

    /// <summary>Built-in this variable carries, if any.</summary>
    public BuiltIn BuiltIn { get; set; }

    /// <summary>Interpolation for stage interface variables.</summary>
    public Interpolation Interpolation { get; set; }

    /// <summary>Semantic text such as <c>TEXCOORD1</c>, kept for reflection and diagnostics.</summary>
    public string? Semantic { get; set; }

    /// <summary>Name of the storage-image format (see <see cref="ImageFormats"/>) for read-write textures; null leaves it unspecified.</summary>
    public string? ImageFormat { get; set; }

    /// <summary>Buffer byte layout for constant and storage buffer resources.</summary>
    public TypeLayout? Layout { get; set; }

    /// <summary>Where it was declared.</summary>
    public SourceSpan Span { get; set; }
}
