using System;
using System.Collections.Generic;
using OneShader.IR;
using OneShader.Types;

namespace OneShader.Optimization;

/// <summary>
/// Evaluates an instruction whose operands are all constants. It folds only what is exactly defined:
/// integer operations wrap as HLSL's do, float operations run in the operand's own precision, and
/// anything whose result could differ between a CPU and a GPU (division by zero, NaN results, subnormal
/// values, float-to-int conversions out of range, transcendental functions) is left for the driver.
/// </summary>
internal static partial class ConstFold
{
    private const uint SignBit32 = 0x8000_0000u;
    private const ulong SignBit64 = 0x8000_0000_0000_0000UL;

    /// <summary>Returns the constant result, or null when the instruction cannot or should not be folded.</summary>
    public static Constant? Fold(Instruction i)
    {
        foreach (var o in i.Operands)
        {
            if (o is not Constant c || !IsDefined(c))
            {
                return null;
            }
        }

        switch (i.Op)
        {
            case Opcode.Add or Opcode.Sub or Opcode.Mul or Opcode.Div or Opcode.Rem or Opcode.BitAnd or Opcode.BitOr
                or Opcode.BitXor or Opcode.Shl or Opcode.Shr or Opcode.LogicalAnd or Opcode.LogicalOr
                or Opcode.Eq or Opcode.Ne or Opcode.Lt or Opcode.Le or Opcode.Gt or Opcode.Ge:
                return FoldBinary(i);
            case Opcode.Neg or Opcode.BitNot or Opcode.LogicalNot:
                return FoldUnary(i);
            case Opcode.Select:
                return FoldSelect(i);
            case Opcode.Convert or Opcode.Bitcast:
                return FoldCast(i);
            case Opcode.CompositeConstruct:
                return FoldConstruct(i);
            case Opcode.CompositeExtract:
                return FoldExtract(i);
            case Opcode.CompositeInsert:
                return FoldInsert(i);
            case Opcode.VectorShuffle:
                return FoldShuffle(i);
            case Opcode.VectorExtractDynamic:
                if (i.Operands[1] is Constant { ConstantKind: ConstantKind.Scalar } idx && i.Operands[0] is Constant { ConstantKind: ConstantKind.Composite } vec
                    && idx.Bits < (ulong)vec.Elements.Count)
                {
                    return vec.Elements[(int)idx.Bits];
                }

                return null;
            case Opcode.CallIntrinsic:
                return FoldIntrinsic(i);
            default:
                return null;
        }
    }

    /// <summary>True when the constant (and everything inside it) has a definite value.</summary>
    public static bool IsDefined(Constant c)
    {
        if (c.ConstantKind == ConstantKind.Undef)
        {
            return false;
        }

        if (c.ConstantKind == ConstantKind.Composite)
        {
            foreach (var e in c.Elements)
            {
                if (!IsDefined(e))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool Supported(ScalarType t) =>
        t.ScalarKind is ScalarKind.Bool or ScalarKind.Int or ScalarKind.UInt or ScalarKind.Float or ScalarKind.Double || IsWide(Norm(t.ScalarKind));

    /// <summary>Components of a scalar or vector constant; false for matrices, arrays, structs and unsupported scalar kinds.</summary>
    private static bool Components(IrValue v, out IReadOnlyList<Constant> parts, out ScalarType element)
    {
        parts = [];
        element = ScalarType.Int;
        if (v is not Constant c)
        {
            return false;
        }

        if (c.Type is ScalarType s && c.ConstantKind == ConstantKind.Scalar && Supported(s))
        {
            parts = [c];
            element = s;
            return true;
        }

        if (c.Type is VectorType vt && c.ConstantKind == ConstantKind.Composite && Supported(vt.Element) && c.Elements.Count == vt.Count)
        {
            parts = c.Elements;
            element = vt.Element;
            return true;
        }

        return false;
    }

    private static Constant Build(HlslType type, ScalarType element, ulong[] bits)
    {
        if (type is ScalarType)
        {
            return Constant.FromBits(element, bits[0]);
        }

        var list = new Constant[bits.Length];
        for (int k = 0; k < bits.Length; k++)
        {
            list[k] = Constant.FromBits(element, bits[k]);
        }

        return Constant.Composite(type, list);
    }

    private static Constant? FoldBinary(Instruction i)
    {
        if (!Components(i.Operands[0], out var a, out var ea) || !Components(i.Operands[1], out var b, out _) || a.Count != b.Count)
        {
            return null;
        }

        bool compare = i.Op is Opcode.Eq or Opcode.Ne or Opcode.Lt or Opcode.Le or Opcode.Gt or Opcode.Ge;
        var bits = new ulong[a.Count];
        for (int k = 0; k < bits.Length; k++)
        {
            if (!EvalBinary(i.Op, Norm(ea.ScalarKind), a[k].Bits, b[k].Bits, out bits[k]))
            {
                return null;
            }
        }

        return Build(i.Type, compare ? ScalarType.Bool : ea, bits);
    }

    private static Constant? FoldUnary(Instruction i)
    {
        if (!Components(i.Operands[0], out var a, out var ea))
        {
            return null;
        }

        var bits = new ulong[a.Count];
        for (int k = 0; k < bits.Length; k++)
        {
            ulong x = a[k].Bits;
            if (IsWide(Norm(ea.ScalarKind)))
            {
                if (i.Op is not (Opcode.Neg or Opcode.BitNot) || !EvalWideUnary(i.Op, Norm(ea.ScalarKind), x, out bits[k]))
                {
                    return null;
                }

                continue;
            }

            switch (i.Op)
            {
                case Opcode.Neg:
                    switch (ea.ScalarKind)
                    {
                        case ScalarKind.Int or ScalarKind.UInt:
                            bits[k] = (uint)(0u - (uint)x);
                            break;
                        case ScalarKind.Float:
                            bits[k] = (uint)x ^ SignBit32;
                            break;
                        case ScalarKind.Double:
                            bits[k] = x ^ SignBit64;
                            break;
                        default:
                            return null;
                    }

                    break;
                case Opcode.BitNot:
                    if (ea.ScalarKind is not (ScalarKind.Int or ScalarKind.UInt))
                    {
                        return null;
                    }

                    bits[k] = (uint)~(uint)x;
                    break;
                default:
                    if (ea.ScalarKind != ScalarKind.Bool)
                    {
                        return null;
                    }

                    bits[k] = x == 0 ? 1UL : 0UL;
                    break;
            }
        }

        return Build(i.Type, ea, bits);
    }

    private static Constant? FoldSelect(Instruction i)
    {
        if (!Components(i.Operands[0], out var cond, out var ec) || ec.ScalarKind != ScalarKind.Bool)
        {
            return null;
        }

        if (cond.Count == 1 && i.Operands[1] is Constant t1 && i.Operands[2] is Constant f1)
        {
            return cond[0].Bits != 0 ? t1 : f1;
        }

        if (!Components(i.Operands[1], out var t, out var et) || !Components(i.Operands[2], out var f, out _) || t.Count != cond.Count || f.Count != cond.Count)
        {
            return null;
        }

        var bits = new ulong[t.Count];
        for (int k = 0; k < bits.Length; k++)
        {
            bits[k] = cond[k].Bits != 0 ? t[k].Bits : f[k].Bits;
        }

        return Build(i.Type, et, bits);
    }

    private static Constant? FoldCast(Instruction i)
    {
        if (!Components(i.Operands[0], out var a, out var from) || i.Type.ScalarElement is not { } to || !Supported(to))
        {
            return null;
        }

        var bits = new ulong[a.Count];
        for (int k = 0; k < bits.Length; k++)
        {
            if (i.Op == Opcode.Bitcast)
            {
                // Only a same-width, same-shape reinterpretation is folded; bool and shape changes (uint2 to double) are left alone.
                if (from.ScalarKind == ScalarKind.Bool || to.ScalarKind == ScalarKind.Bool || WidthOf(Norm(from.ScalarKind)) != WidthOf(Norm(to.ScalarKind))
                    || (i.Type is VectorType bv ? bv.Count : 1) != a.Count)
                {
                    return null;
                }

                bits[k] = a[k].Bits & MaskOf(WidthOf(Norm(to.ScalarKind)));
            }
            else if (!ConvertScalar(Norm(from.ScalarKind), Norm(to.ScalarKind), a[k].Bits, out bits[k]))
            {
                return null;
            }
        }

        return Build(i.Type, to, bits);
    }

    private static Constant? FoldConstruct(Instruction i)
    {
        switch (i.Type)
        {
            case VectorType vt:
            {
                var flat = new List<Constant>();
                foreach (var o in i.Operands)
                {
                    var c = (Constant)o;
                    if (c.Type is ScalarType)
                    {
                        flat.Add(c);
                    }
                    else if (c.Type is VectorType && c.ConstantKind == ConstantKind.Composite)
                    {
                        flat.AddRange(c.Elements);
                    }
                    else
                    {
                        return null;
                    }
                }

                if (flat.Count == 1 && vt.Count > 1)
                {
                    var splat = new Constant[vt.Count];
                    Array.Fill(splat, flat[0]);
                    flat = [.. splat];
                }

                if (flat.Count != vt.Count)
                {
                    return null;
                }

                foreach (var c in flat)
                {
                    if (!c.Type.Equals(vt.Element))
                    {
                        return null;
                    }
                }

                return Constant.Composite(i.Type, flat);
            }

            case MatrixType or ArrayType or StructType:
            {
                var list = new List<Constant>();
                foreach (var o in i.Operands)
                {
                    list.Add((Constant)o);
                }

                int expected = i.Type switch
                {
                    MatrixType m => m.Rows,
                    ArrayType a => a.Length,
                    StructType s => s.Fields.Count,
                    _ => -1,
                };
                return list.Count == expected ? Constant.Composite(i.Type, list) : null;
            }

            default:
                return null;
        }
    }

    private static Constant? FoldExtract(Instruction i)
    {
        var c = (Constant)i.Operands[0];
        foreach (int index in i.Immediates)
        {
            if (c.ConstantKind != ConstantKind.Composite || index < 0 || index >= c.Elements.Count)
            {
                return null;
            }

            c = c.Elements[index];
        }

        return c.Type.Equals(i.Type) ? c : null;
    }

    private static Constant? FoldInsert(Instruction i)
    {
        return InsertAt((Constant)i.Operands[0], i.Immediates, 0, (Constant)i.Operands[1]);
    }

    private static Constant? InsertAt(Constant target, List<int> path, int depth, Constant value)
    {
        if (depth == path.Count)
        {
            return value;
        }

        if (target.ConstantKind != ConstantKind.Composite || path[depth] < 0 || path[depth] >= target.Elements.Count)
        {
            return null;
        }

        var inner = InsertAt(target.Elements[path[depth]], path, depth + 1, value);
        if (inner is null)
        {
            return null;
        }

        var list = new Constant[target.Elements.Count];
        for (int k = 0; k < list.Length; k++)
        {
            list[k] = target.Elements[k];
        }

        list[path[depth]] = inner;
        return Constant.Composite(target.Type, list);
    }

    private static Constant? FoldShuffle(Instruction i)
    {
        var a = (Constant)i.Operands[0];
        var b = (Constant)i.Operands[1];
        if (a.ConstantKind != ConstantKind.Composite || b.ConstantKind != ConstantKind.Composite)
        {
            return null;
        }

        var list = new Constant[i.Immediates.Count];
        for (int k = 0; k < list.Length; k++)
        {
            int idx = i.Immediates[k];
            if (idx < 0 || idx >= a.Elements.Count + b.Elements.Count)
            {
                return null;
            }

            list[k] = idx < a.Elements.Count ? a.Elements[idx] : b.Elements[idx - a.Elements.Count];
        }

        return i.Type is VectorType ? Constant.Composite(i.Type, list) : list.Length == 1 ? list[0] : null;
    }

    private static Constant? FoldIntrinsic(Instruction i)
    {
        int n = i.Operands.Count;
        switch (i.Intrinsic)
        {
            case Intrinsic.Abs or Intrinsic.Saturate or Intrinsic.Floor or Intrinsic.Ceil or Intrinsic.Trunc when n == 1:
            case Intrinsic.Min or Intrinsic.Max when n == 2:
            case Intrinsic.Clamp when n == 3:
                break;
            default:
                return null;
        }

        if (!Components(i.Operands[0], out var a, out var e))
        {
            return null;
        }

        var parts = new IReadOnlyList<Constant>[n];
        parts[0] = a;
        for (int k = 1; k < n; k++)
        {
            if (!Components(i.Operands[k], out parts[k]!, out var ek) || parts[k].Count != a.Count || ek.ScalarKind != e.ScalarKind)
            {
                return null;
            }
        }

        var bits = new ulong[a.Count];
        for (int k = 0; k < bits.Length; k++)
        {
            ulong x = a[k].Bits;
            ulong y = n > 1 ? parts[1][k].Bits : 0;
            ulong z = n > 2 ? parts[2][k].Bits : 0;
            if (!EvalIntrinsic(i.Intrinsic, Norm(e.ScalarKind), x, y, z, out bits[k]))
            {
                return null;
            }
        }

        return Build(i.Type, e, bits);
    }

    private static bool EvalIntrinsic(Intrinsic fn, ScalarKind k, ulong x, ulong y, ulong z, out ulong r)
    {
        r = 0;
        if (IsWide(k))
        {
            return EvalWideIntrinsic(fn, k, x, y, z, out r);
        }

        switch (k)
        {
            case ScalarKind.Int or ScalarKind.UInt:
            {
                bool signed = k == ScalarKind.Int;
                int sx = (int)x;
                int sy = (int)y;
                int sz = (int)z;
                uint ux = (uint)x;
                uint uy = (uint)y;
                uint uz = (uint)z;
                switch (fn)
                {
                    case Intrinsic.Abs:
                        r = signed ? (uint)(sx < 0 ? 0 - sx : sx) : ux;
                        return true;
                    case Intrinsic.Min:
                        r = signed ? (uint)Math.Min(sx, sy) : Math.Min(ux, uy);
                        return true;
                    case Intrinsic.Max:
                        r = signed ? (uint)Math.Max(sx, sy) : Math.Max(ux, uy);
                        return true;
                    case Intrinsic.Clamp:
                        if (signed ? sy > sz : uy > uz)
                        {
                            return false;
                        }

                        r = signed ? (uint)Math.Min(Math.Max(sx, sy), sz) : Math.Min(Math.Max(ux, uy), uz);
                        return true;
                    default:
                        return false;
                }
            }

            case ScalarKind.Float:
            {
                float fx = BitConverter.UInt32BitsToSingle((uint)x);
                float fy = BitConverter.UInt32BitsToSingle((uint)y);
                float fz = BitConverter.UInt32BitsToSingle((uint)z);
                if (!Plain(fx) || (fn is Intrinsic.Min or Intrinsic.Max or Intrinsic.Clamp && (!Plain(fy) || (fn == Intrinsic.Clamp && !Plain(fz)))))
                {
                    return false;
                }

                float res;
                switch (fn)
                {
                    case Intrinsic.Abs:
                        res = MathF.Abs(fx);
                        break;
                    case Intrinsic.Floor:
                        res = MathF.Floor(fx);
                        break;
                    case Intrinsic.Ceil:
                        res = MathF.Ceiling(fx);
                        break;
                    case Intrinsic.Trunc:
                        res = MathF.Truncate(fx);
                        break;
                    case Intrinsic.Saturate:
                        if (fx == 0)
                        {
                            return false;
                        }

                        res = fx < 0 ? 0f : fx > 1 ? 1f : fx;
                        break;
                    case Intrinsic.Min:
                        if (fx == fy && (uint)x != (uint)y)
                        {
                            return false;
                        }

                        res = fx < fy ? fx : fy;
                        break;
                    case Intrinsic.Max:
                        if (fx == fy && (uint)x != (uint)y)
                        {
                            return false;
                        }

                        res = fx > fy ? fx : fy;
                        break;
                    default:
                        if (fy > fz || fx == fy && (uint)x != (uint)y || fx == fz && (uint)x != (uint)z || fy == fz && (uint)y != (uint)z)
                        {
                            return false;
                        }

                        res = Math.Min(Math.Max(fx, fy), fz);
                        break;
                }

                if (fn is Intrinsic.Floor or Intrinsic.Ceil or Intrinsic.Trunc && res == 0)
                {
                    // The sign of a zero result differs between implementations; leave it to the driver.
                    return false;
                }

                r = BitConverter.SingleToUInt32Bits(res);
                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>A finite, normal (or zero) float: the only inputs whose CPU and GPU results are known to agree.</summary>
    private static bool Plain(float f) => float.IsFinite(f) && (f == 0 || MathF.Abs(f) >= 1.17549435E-38f);

    private static bool Plain(double d) => double.IsFinite(d) && (d == 0 || Math.Abs(d) >= 2.2250738585072014E-308);

    private static bool EvalBinary(Opcode op, ScalarKind k, ulong a, ulong b, out ulong r)
    {
        r = 0;
        if (k == ScalarKind.Half)
        {
            return EvalHalf(op, a, b, out r);
        }

        if (IsWideInt(k))
        {
            return EvalWideInt(op, k, a, b, out r);
        }

        switch (k)
        {
            case ScalarKind.Bool:
            {
                bool x = a != 0;
                bool y = b != 0;
                switch (op)
                {
                    case Opcode.LogicalAnd or Opcode.BitAnd:
                        r = x & y ? 1UL : 0UL;
                        return true;
                    case Opcode.LogicalOr or Opcode.BitOr:
                        r = x | y ? 1UL : 0UL;
                        return true;
                    case Opcode.BitXor or Opcode.Ne:
                        r = x ^ y ? 1UL : 0UL;
                        return true;
                    case Opcode.Eq:
                        r = x == y ? 1UL : 0UL;
                        return true;
                    default:
                        return false;
                }
            }

            case ScalarKind.Int:
            {
                int x = (int)a;
                int y = (int)b;
                switch (op)
                {
                    case Opcode.Add:
                        r = (uint)unchecked(x + y);
                        return true;
                    case Opcode.Sub:
                        r = (uint)unchecked(x - y);
                        return true;
                    case Opcode.Mul:
                        r = (uint)unchecked(x * y);
                        return true;
                    case Opcode.Div:
                        if (y == 0 || (x == int.MinValue && y == -1))
                        {
                            return false;
                        }

                        r = (uint)(x / y);
                        return true;
                    case Opcode.Rem:
                        if (y == 0 || (x == int.MinValue && y == -1))
                        {
                            return false;
                        }

                        r = (uint)(x % y);
                        return true;
                    case Opcode.BitAnd:
                        r = (uint)(x & y);
                        return true;
                    case Opcode.BitOr:
                        r = (uint)(x | y);
                        return true;
                    case Opcode.BitXor:
                        r = (uint)(x ^ y);
                        return true;
                    case Opcode.Shl:
                        r = (uint)(x << (y & 31));
                        return true;
                    case Opcode.Shr:
                        r = (uint)(x >> (y & 31));
                        return true;
                    case Opcode.Eq:
                        r = x == y ? 1UL : 0UL;
                        return true;
                    case Opcode.Ne:
                        r = x != y ? 1UL : 0UL;
                        return true;
                    case Opcode.Lt:
                        r = x < y ? 1UL : 0UL;
                        return true;
                    case Opcode.Le:
                        r = x <= y ? 1UL : 0UL;
                        return true;
                    case Opcode.Gt:
                        r = x > y ? 1UL : 0UL;
                        return true;
                    case Opcode.Ge:
                        r = x >= y ? 1UL : 0UL;
                        return true;
                    default:
                        return false;
                }
            }

            case ScalarKind.UInt:
            {
                uint x = (uint)a;
                uint y = (uint)b;
                switch (op)
                {
                    case Opcode.Add:
                        r = unchecked(x + y);
                        return true;
                    case Opcode.Sub:
                        r = unchecked(x - y);
                        return true;
                    case Opcode.Mul:
                        r = unchecked(x * y);
                        return true;
                    case Opcode.Div:
                        if (y == 0)
                        {
                            return false;
                        }

                        r = x / y;
                        return true;
                    case Opcode.Rem:
                        if (y == 0)
                        {
                            return false;
                        }

                        r = x % y;
                        return true;
                    case Opcode.BitAnd:
                        r = x & y;
                        return true;
                    case Opcode.BitOr:
                        r = x | y;
                        return true;
                    case Opcode.BitXor:
                        r = x ^ y;
                        return true;
                    case Opcode.Shl:
                        r = x << (int)(y & 31);
                        return true;
                    case Opcode.Shr:
                        r = x >> (int)(y & 31);
                        return true;
                    case Opcode.Eq:
                        r = x == y ? 1UL : 0UL;
                        return true;
                    case Opcode.Ne:
                        r = x != y ? 1UL : 0UL;
                        return true;
                    case Opcode.Lt:
                        r = x < y ? 1UL : 0UL;
                        return true;
                    case Opcode.Le:
                        r = x <= y ? 1UL : 0UL;
                        return true;
                    case Opcode.Gt:
                        r = x > y ? 1UL : 0UL;
                        return true;
                    case Opcode.Ge:
                        r = x >= y ? 1UL : 0UL;
                        return true;
                    default:
                        return false;
                }
            }

            case ScalarKind.Float:
            {
                float x = BitConverter.UInt32BitsToSingle((uint)a);
                float y = BitConverter.UInt32BitsToSingle((uint)b);
                if (!Plain(x) || !Plain(y))
                {
                    return false;
                }

                float res;
                switch (op)
                {
                    case Opcode.Add:
                        res = x + y;
                        break;
                    case Opcode.Sub:
                        res = x - y;
                        break;
                    case Opcode.Mul:
                        res = x * y;
                        break;
                    case Opcode.Div:
                        if (y == 0)
                        {
                            return false;
                        }

                        res = x / y;
                        break;
                    case Opcode.Rem:
                        if (y == 0)
                        {
                            return false;
                        }

                        res = x % y;
                        break;
                    case Opcode.Eq:
                        r = x == y ? 1UL : 0UL;
                        return true;
                    case Opcode.Ne:
                        r = x != y ? 1UL : 0UL;
                        return true;
                    case Opcode.Lt:
                        r = x < y ? 1UL : 0UL;
                        return true;
                    case Opcode.Le:
                        r = x <= y ? 1UL : 0UL;
                        return true;
                    case Opcode.Gt:
                        r = x > y ? 1UL : 0UL;
                        return true;
                    case Opcode.Ge:
                        r = x >= y ? 1UL : 0UL;
                        return true;
                    default:
                        return false;
                }

                if (!Plain(res))
                {
                    return false;
                }

                r = BitConverter.SingleToUInt32Bits(res);
                return true;
            }

            case ScalarKind.Double:
            {
                double x = BitConverter.UInt64BitsToDouble(a);
                double y = BitConverter.UInt64BitsToDouble(b);
                if (!Plain(x) || !Plain(y))
                {
                    return false;
                }

                double res;
                switch (op)
                {
                    case Opcode.Add:
                        res = x + y;
                        break;
                    case Opcode.Sub:
                        res = x - y;
                        break;
                    case Opcode.Mul:
                        res = x * y;
                        break;
                    case Opcode.Div:
                        if (y == 0)
                        {
                            return false;
                        }

                        res = x / y;
                        break;
                    case Opcode.Eq:
                        r = x == y ? 1UL : 0UL;
                        return true;
                    case Opcode.Ne:
                        r = x != y ? 1UL : 0UL;
                        return true;
                    case Opcode.Lt:
                        r = x < y ? 1UL : 0UL;
                        return true;
                    case Opcode.Le:
                        r = x <= y ? 1UL : 0UL;
                        return true;
                    case Opcode.Gt:
                        r = x > y ? 1UL : 0UL;
                        return true;
                    case Opcode.Ge:
                        r = x >= y ? 1UL : 0UL;
                        return true;
                    default:
                        return false;
                }

                if (!Plain(res))
                {
                    return false;
                }

                r = BitConverter.DoubleToUInt64Bits(res);
                return true;
            }

            default:
                return false;
        }
    }

    private static bool ConvertScalar(ScalarKind from, ScalarKind to, ulong bits, out ulong r)
    {
        r = 0;
        if (from == to)
        {
            r = bits;
            return true;
        }

        if (IsWide(from) || IsWide(to))
        {
            return ConvertWide(from, to, bits, out r);
        }

        // Read the source as a double (exact for every 32-bit source) or as a boolean.
        double value;
        switch (from)
        {
            case ScalarKind.Bool:
                value = bits != 0 ? 1 : 0;
                break;
            case ScalarKind.Int:
                value = (int)bits;
                break;
            case ScalarKind.UInt:
                value = (uint)bits;
                break;
            case ScalarKind.Float:
            {
                float f = BitConverter.UInt32BitsToSingle((uint)bits);
                if (!Plain(f))
                {
                    return false;
                }

                value = f;
                break;
            }

            case ScalarKind.Double:
            {
                double d = BitConverter.UInt64BitsToDouble(bits);
                if (!Plain(d))
                {
                    return false;
                }

                value = d;
                break;
            }

            default:
                return false;
        }

        switch (to)
        {
            case ScalarKind.Bool:
                r = value != 0 ? 1UL : 0UL;
                return true;
            case ScalarKind.Int:
            {
                double t = Math.Truncate(value);
                if (t < int.MinValue || t > int.MaxValue)
                {
                    return false;
                }

                r = (uint)(int)t;
                return true;
            }

            case ScalarKind.UInt:
            {
                double t = Math.Truncate(value);
                if (from == ScalarKind.Int && value < 0)
                {
                    r = (uint)(int)value; // int to uint keeps the bits, as the cast does
                    return true;
                }

                if (t < 0 || t > uint.MaxValue)
                {
                    return false;
                }

                r = (uint)t;
                return true;
            }

            case ScalarKind.Float:
            {
                float f = from switch
                {
                    ScalarKind.Int => (float)(int)bits,
                    ScalarKind.UInt => (float)(uint)bits,
                    ScalarKind.Double => (float)value,
                    _ => (float)value,
                };
                if (!Plain(f))
                {
                    return false;
                }

                r = BitConverter.SingleToUInt32Bits(f);
                return true;
            }

            case ScalarKind.Double:
                r = BitConverter.DoubleToUInt64Bits(value);
                return true;
            default:
                return false;
        }
    }
}
