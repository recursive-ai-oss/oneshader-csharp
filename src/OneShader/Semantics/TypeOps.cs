using System;
using OneShader.Types;

namespace OneShader.Semantics;

/// <summary>A compile-time scalar: what <c>static const int N = 4;</c> and literal arithmetic evaluate to.</summary>
internal readonly struct ConstValue
{
    public ConstValue(ScalarType type, long integer, double floating)
    {
        Type = type;
        Int = integer;
        Float = floating;
    }

    public ScalarType Type { get; }

    /// <summary>The value of a bool or integer type, kept sign-extended (unsigned 32-bit values are non-negative).</summary>
    public long Int { get; }

    /// <summary>The value of a floating-point type.</summary>
    public double Float { get; }

    public bool IsFloat => Type.IsFloatingPoint;

    public bool IsZero => IsFloat ? Float == 0 : Int == 0;

    public double AsDouble() => IsFloat ? Float : (Type.ScalarKind == ScalarKind.UInt64 ? (double)(ulong)Int : Int);

    public static ConstValue FromInt(ScalarType type, long v)
    {
        return new ConstValue(type, Wrap(type, v), 0);
    }

    public static ConstValue FromFloat(ScalarType type, double v)
    {
        return new ConstValue(type, 0, RoundFloat(type, v));
    }

    public static ConstValue Bool(bool b) => new(ScalarType.Bool, b ? 1 : 0, 0);

    private static double RoundFloat(ScalarType t, double v) => t.ScalarKind switch
    {
        ScalarKind.Float => (double)(float)v,
        ScalarKind.Half or ScalarKind.Min16Float or ScalarKind.Min10Float => (double)(Half)v,
        // An unsuffixed literal keeps double precision until it meets a concrete type (DXC: 0.1 added to a double is the double 0.1).
        _ => v,
    };

    public static long Wrap(ScalarType t, long v) => t.ScalarKind switch
    {
        ScalarKind.Bool => v != 0 ? 1 : 0,
        ScalarKind.Int or ScalarKind.LiteralInt => (int)v,
        ScalarKind.UInt => (uint)v,
        ScalarKind.Int16 or ScalarKind.Min16Int => (short)v,
        ScalarKind.UInt16 or ScalarKind.Min16UInt => (ushort)v,
        _ => v,
    };

    /// <summary>Converts with the C-like rules HLSL uses for casts.</summary>
    public ConstValue ConvertTo(ScalarType to)
    {
        if (to.IsFloatingPoint)
        {
            return FromFloat(to, AsDouble());
        }

        if (to.ScalarKind == ScalarKind.Bool)
        {
            return Bool(!IsZero);
        }

        if (IsFloat)
        {
            double f = Float;
            if (double.IsNaN(f))
            {
                return FromInt(to, 0);
            }

            if (to.ScalarKind == ScalarKind.UInt64)
            {
                return FromInt(to, f <= 0 ? 0 : f >= 18446744073709551615.0 ? -1 : unchecked((long)(ulong)f));
            }

            double lo = to.IsSignedInteger ? -9.2233720368547758e18 : 0;
            long v = f <= lo ? (to.IsSignedInteger ? long.MinValue : 0) : f >= 9.2233720368547758e18 ? long.MaxValue : (long)f;
            return FromInt(to, v);
        }

        return FromInt(to, Int);
    }

    /// <summary>The bit pattern an IR constant of this value's type holds.</summary>
    public ulong Bits() => Type.ScalarKind switch
    {
        ScalarKind.Float or ScalarKind.LiteralFloat => BitConverter.SingleToUInt32Bits((float)Float),
        ScalarKind.Half or ScalarKind.Min16Float or ScalarKind.Min10Float => BitConverter.HalfToUInt16Bits((Half)Float),
        ScalarKind.Double => BitConverter.DoubleToUInt64Bits(Float),
        ScalarKind.Int64 or ScalarKind.UInt64 => unchecked((ulong)Int),
        ScalarKind.Int16 or ScalarKind.Min16Int => (ushort)Int,
        ScalarKind.UInt16 or ScalarKind.Min16UInt => (ushort)Int,
        _ => (uint)Int,
    };
}

/// <summary>Type rules shared by checking and lowering: promotion, shapes and implicit conversion.</summary>
internal static class TypeOps
{
    public static bool IsNumeric(HlslType t) => t.ScalarElement is not null;

    public static bool IsBoolLike(HlslType t) => t.ScalarElement?.ScalarKind == ScalarKind.Bool;

    public static bool IsFloatLike(HlslType t) => t.ScalarElement?.IsFloatingPoint == true;

    public static bool IsIntLike(HlslType t) => t.ScalarElement is { IsInteger: true };

    public static int Count(HlslType t) => t switch
    {
        ScalarType => 1,
        VectorType v => v.Count,
        MatrixType m => m.Rows * m.Columns,
        _ => 0,
    };

    public static HlslType Reshape(HlslType shape, ScalarType s) => shape switch
    {
        VectorType v => HlslType.Vector(s, v.Count),
        MatrixType m => HlslType.Matrix(s, m.Rows, m.Columns),
        _ => s,
    };

    public static bool SameShape(HlslType a, HlslType b) => a switch
    {
        ScalarType => b is ScalarType,
        VectorType va => b is VectorType vb && va.Count == vb.Count,
        MatrixType ma => b is MatrixType mb && ma.Rows == mb.Rows && ma.Columns == mb.Columns,
        _ => false,
    };

    public static int BitWidth(ScalarType s) => s.ScalarKind switch
    {
        ScalarKind.Bool => 1,
        ScalarKind.Int16 or ScalarKind.UInt16 or ScalarKind.Half or ScalarKind.Min16Float or ScalarKind.Min10Float
            or ScalarKind.Min16Int or ScalarKind.Min16UInt => 16,
        ScalarKind.Int64 or ScalarKind.UInt64 or ScalarKind.Double => 64,
        _ => 32,
    };

    private static int FloatRank(ScalarType s) => s.ScalarKind switch
    {
        ScalarKind.Double => 3,
        ScalarKind.Float or ScalarKind.LiteralFloat => 2,
        ScalarKind.Half or ScalarKind.Min16Float or ScalarKind.Min10Float => 1,
        _ => 0,
    };

    private static bool IsUnsigned(ScalarType s) => s.IsInteger && !s.IsSignedInteger;

    /// <summary>The scalar type arithmetic between <paramref name="a"/> and <paramref name="b"/> is done in.</summary>
    public static ScalarType Common(ScalarType a, ScalarType b)
    {
        int fa = FloatRank(a);
        int fb = FloatRank(b);
        if (fa > 0 || fb > 0)
        {
            if (fa == fb)
            {
                // A min-precision value mixed with a plain one is plain (DXC: min16float * float is float).
                return a.IsRelaxed && !b.IsRelaxed ? b : a;
            }

            return fa > fb ? a : b;
        }

        if (a.ScalarKind == ScalarKind.Bool && b.ScalarKind == ScalarKind.Bool)
        {
            return ScalarType.Int;
        }

        if (a.ScalarKind == ScalarKind.Bool)
        {
            a = ScalarType.Int;
        }

        if (b.ScalarKind == ScalarKind.Bool)
        {
            b = ScalarType.Int;
        }

        if (a == b)
        {
            return a.IsRelaxed && !b.IsRelaxed ? b : a;
        }

        int wa = BitWidth(a);
        int wb = BitWidth(b);
        if (wa == 64 || wb == 64)
        {
            bool u = (wa == 64 && IsUnsigned(a)) || (wb == 64 && IsUnsigned(b));
            return ScalarType.Get(u ? ScalarKind.UInt64 : ScalarKind.Int64);
        }

        return (wa == 32 && IsUnsigned(a)) || (wb == 32 && IsUnsigned(b)) ? ScalarType.UInt : ScalarType.Int;
    }

    /// <summary>The type an operand is held in when an arithmetic operator is applied: bool becomes int, everything else is kept.</summary>
    public static ScalarType ArithmeticScalar(ScalarType s) => s.ScalarKind == ScalarKind.Bool ? ScalarType.Int : s;

    public enum Conv
    {
        None,
        Exact,
        Ok,
        Truncates,
    }

    public static Conv Classify(HlslType from, HlslType to)
    {
        if (from.IsError || to.IsError || from.Equals(to))
        {
            return Conv.Exact;
        }

        if (from is StructType fst && to is StructType && fst.IsDerivedFrom((StructType)to))
        {
            return Conv.Ok;
        }

        if (from is EnumType fe && to.ScalarElement is not null)
        {
            return Classify(fe.Underlying, to);
        }

        if (from is ResourceType { ResourceKind: ResourceTypeKind.ConstantBuffer, Element: { } el } && el.Equals(to))
        {
            return Conv.Ok;
        }

        var fs = from.ScalarElement;
        var ts = to.ScalarElement;
        if (fs is null || ts is null)
        {
            return Conv.None;
        }

        switch (from)
        {
            case ScalarType:
                return Conv.Ok;
            case VectorType fv:
                if (to is ScalarType)
                {
                    return Conv.Truncates;
                }

                if (to is VectorType tv)
                {
                    return tv.Count == fv.Count ? Conv.Ok : tv.Count < fv.Count ? Conv.Truncates : Conv.None;
                }

                return Conv.None;
            case MatrixType fm:
                if (to is ScalarType)
                {
                    return Conv.Truncates;
                }

                if (to is MatrixType tm)
                {
                    if (tm.Rows == fm.Rows && tm.Columns == fm.Columns)
                    {
                        return Conv.Ok;
                    }

                    return tm.Rows <= fm.Rows && tm.Columns <= fm.Columns ? Conv.Truncates : Conv.None;
                }

                return Conv.None;
        }

        return Conv.None;
    }

    private static int ScalarCost(ScalarType f, ScalarType t)
    {
        if (f == t)
        {
            return 0;
        }

        bool ff = f.IsFloatingPoint;
        bool tf = t.IsFloatingPoint;
        if (t.ScalarKind == ScalarKind.Bool)
        {
            return 6;
        }

        if (f.ScalarKind == ScalarKind.Bool)
        {
            return 3;
        }

        if (ff && tf)
        {
            return FloatRank(t) > FloatRank(f) ? 1 : 2;
        }

        if (!ff && tf)
        {
            return 3;
        }

        if (ff && !tf)
        {
            return 6;
        }

        // Integer to integer.
        if (BitWidth(t) > BitWidth(f) && t.IsSignedInteger == f.IsSignedInteger)
        {
            return 1;
        }

        return t.IsSignedInteger == f.IsSignedInteger ? 2 : 3;
    }

    /// <summary>How good an implicit conversion is for overload ranking; lower is better, <see cref="int.MaxValue"/> means not allowed.</summary>
    public static int Cost(HlslType from, HlslType to)
    {
        var c = Classify(from, to);
        if (c == Conv.None)
        {
            return int.MaxValue;
        }

        if (from.Equals(to) || from.IsError || to.IsError)
        {
            return 0;
        }

        var fs = from.ScalarElement;
        var ts = to.ScalarElement;
        if (fs is null || ts is null)
        {
            return 1;
        }

        int cost = ScalarCost(fs, ts);
        if (c == Conv.Truncates)
        {
            cost += 10;
        }
        else if (from is ScalarType && to is not ScalarType)
        {
            cost += 1;
        }

        return cost;
    }

    /// <summary>The element type reached by indexing once, or null when the type cannot be indexed.</summary>
    public static HlslType? IndexedElement(HlslType t) => t switch
    {
        ArrayType a => a.Element,
        VectorType v => v.Element,
        MatrixType m => HlslType.Vector(m.Element, m.Columns),
        _ => null,
    };

    /// <summary>True when values of the type can be held in a local variable (not a resource or void).</summary>
    public static bool IsValueType(HlslType t) => IsValueType(t, 0);

    // The depth counts through structs and arrays alike: a struct that holds an array of itself must end the walk too.
    private static bool IsValueType(HlslType t, int depth)
    {
        switch (t)
        {
            case ScalarType or VectorType or MatrixType or EnumType:
                return true;
            case ArrayType a:
                return depth <= 32 && IsValueType(a.Element, depth + 1);
            case StructType s:
                if (depth > 32)
                {
                    return false;
                }

                foreach (var f in s.Fields)
                {
                    if (!IsValueType(f.Type, depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return false;
        }
    }
}
