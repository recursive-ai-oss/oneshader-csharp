using System;
using OneShader.IR;
using OneShader.Types;

namespace OneShader.Optimization;

/// <summary>Exact folding for the 16-bit and 64-bit scalar kinds (half, int16_t, uint16_t, int64_t, uint64_t).</summary>
internal static partial class ConstFold
{
    /// <summary>
    /// The folding kind for a scalar: the min-precision kinds that survive only when 16-bit types are on are the same as their
    /// plain 16-bit twins.
    /// </summary>
    private static ScalarKind Norm(ScalarKind k) => k switch
    {
        ScalarKind.Min16Float or ScalarKind.Min10Float => ScalarKind.Half,
        ScalarKind.Min16Int => ScalarKind.Int16,
        ScalarKind.Min16UInt => ScalarKind.UInt16,
        _ => k,
    };

    private static bool IsWide(ScalarKind k) => k is ScalarKind.Half or ScalarKind.Int16 or ScalarKind.UInt16 or ScalarKind.Int64 or ScalarKind.UInt64;

    private static bool IsWideInt(ScalarKind k) => k is ScalarKind.Int16 or ScalarKind.UInt16 or ScalarKind.Int64 or ScalarKind.UInt64;

    private static int WidthOf(ScalarKind k) => k switch
    {
        ScalarKind.Int16 or ScalarKind.UInt16 or ScalarKind.Half => 16,
        ScalarKind.Int64 or ScalarKind.UInt64 or ScalarKind.Double => 64,
        _ => 32,
    };

    private static ulong MaskOf(int width) => width == 64 ? ulong.MaxValue : (1UL << width) - 1;

    private static long SignExtend(ulong bits, int width) => width == 64 ? (long)bits : (long)(bits << (64 - width)) >> (64 - width);

    /// <summary>A half that is zero or normal: subnormal halves are flushed by some drivers, so they are left to run time.</summary>
    private static bool Plain(Half h) => Half.IsFinite(h) && (h == Half.Zero || Half.Abs(h) >= (Half)6.103515625e-05);

    private static bool EvalWideInt(Opcode op, ScalarKind k, ulong a, ulong b, out ulong r)
    {
        r = 0;
        int width = WidthOf(k);
        bool signed = k is ScalarKind.Int16 or ScalarKind.Int64;
        ulong mask = MaskOf(width);
        ulong ux = a & mask;
        ulong uy = b & mask;
        long sx = SignExtend(ux, width);
        long sy = SignExtend(uy, width);
        long min = width == 64 ? long.MinValue : -(1L << (width - 1));
        int shift = (int)(uy & (ulong)(width - 1));
        switch (op)
        {
            case Opcode.Add:
                r = unchecked(ux + uy) & mask;
                return true;
            case Opcode.Sub:
                r = unchecked(ux - uy) & mask;
                return true;
            case Opcode.Mul:
                r = unchecked(ux * uy) & mask;
                return true;
            case Opcode.Div:
                if (uy == 0 || (signed && sx == min && sy == -1))
                {
                    return false;
                }

                r = (signed ? unchecked((ulong)(sx / sy)) : ux / uy) & mask;
                return true;
            case Opcode.Rem:
                if (uy == 0 || (signed && sx == min && sy == -1))
                {
                    return false;
                }

                r = (signed ? unchecked((ulong)(sx % sy)) : ux % uy) & mask;
                return true;
            case Opcode.BitAnd:
                r = ux & uy;
                return true;
            case Opcode.BitOr:
                r = ux | uy;
                return true;
            case Opcode.BitXor:
                r = ux ^ uy;
                return true;
            case Opcode.Shl:
                r = (ux << shift) & mask;
                return true;
            case Opcode.Shr:
                r = (signed ? unchecked((ulong)(sx >> shift)) : ux >> shift) & mask;
                return true;
            case Opcode.Eq:
                r = ux == uy ? 1UL : 0UL;
                return true;
            case Opcode.Ne:
                r = ux != uy ? 1UL : 0UL;
                return true;
            case Opcode.Lt:
                r = (signed ? sx < sy : ux < uy) ? 1UL : 0UL;
                return true;
            case Opcode.Le:
                r = (signed ? sx <= sy : ux <= uy) ? 1UL : 0UL;
                return true;
            case Opcode.Gt:
                r = (signed ? sx > sy : ux > uy) ? 1UL : 0UL;
                return true;
            case Opcode.Ge:
                r = (signed ? sx >= sy : ux >= uy) ? 1UL : 0UL;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Half arithmetic in double: a half operation is one correctly rounded IEEE operation, and a double holds the exact
    /// sum or product of two halves (and rounds a quotient far more finely than a half needs), so rounding the double
    /// to half gives the same bits a half unit produces.
    /// </summary>
    private static bool EvalHalf(Opcode op, ulong a, ulong b, out ulong r)
    {
        r = 0;
        var hx = BitConverter.UInt16BitsToHalf((ushort)a);
        var hy = BitConverter.UInt16BitsToHalf((ushort)b);
        if (!Plain(hx) || !Plain(hy))
        {
            return false;
        }

        double x = (double)hx;
        double y = (double)hy;
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

        var h = (Half)res;
        if (!Plain(h) || (h == Half.Zero && res != 0))
        {
            return false;
        }

        r = BitConverter.HalfToUInt16Bits(h);
        return true;
    }

    private static bool EvalWideUnary(Opcode op, ScalarKind k, ulong x, out ulong r)
    {
        r = 0;
        if (k == ScalarKind.Half)
        {
            if (op != Opcode.Neg)
            {
                return false;
            }

            r = (x & 0xFFFF) ^ 0x8000;
            return true;
        }

        ulong mask = MaskOf(WidthOf(k));
        switch (op)
        {
            case Opcode.Neg:
                r = unchecked(0UL - (x & mask)) & mask;
                return true;
            case Opcode.BitNot:
                r = ~x & mask;
                return true;
            default:
                return false;
        }
    }

    private static bool EvalWideIntrinsic(Intrinsic fn, ScalarKind k, ulong x, ulong y, ulong z, out ulong r)
    {
        r = 0;
        if (!IsWideInt(k))
        {
            return false;
        }

        int width = WidthOf(k);
        bool signed = k is ScalarKind.Int16 or ScalarKind.Int64;
        ulong mask = MaskOf(width);
        long sx = SignExtend(x & mask, width);
        long sy = SignExtend(y & mask, width);
        long sz = SignExtend(z & mask, width);
        ulong ux = x & mask;
        ulong uy = y & mask;
        ulong uz = z & mask;
        switch (fn)
        {
            case Intrinsic.Abs:
                r = (signed ? unchecked((ulong)(sx < 0 ? -sx : sx)) : ux) & mask;
                return true;
            case Intrinsic.Min:
                r = (signed ? unchecked((ulong)Math.Min(sx, sy)) : Math.Min(ux, uy)) & mask;
                return true;
            case Intrinsic.Max:
                r = (signed ? unchecked((ulong)Math.Max(sx, sy)) : Math.Max(ux, uy)) & mask;
                return true;
            case Intrinsic.Clamp:
                if (signed ? sy > sz : uy > uz)
                {
                    return false;
                }

                r = (signed ? unchecked((ulong)Math.Min(Math.Max(sx, sy), sz)) : Math.Min(Math.Max(ux, uy), uz)) & mask;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Conversions where at least one side is a 16-bit or 64-bit kind. Anything that is not exactly the same on every device returns false.</summary>
    private static bool ConvertWide(ScalarKind from, ScalarKind to, ulong bits, out ulong r)
    {
        r = 0;

        // Decode the source: an integer (as its sign- or zero-extended 64-bit pattern) or a floating-point value.
        bool isFloat = false;
        bool isUnsigned64 = false;
        long integer = 0;
        double d = 0;
        switch (from)
        {
            case ScalarKind.Bool:
                integer = bits != 0 ? 1 : 0;
                break;
            case ScalarKind.Int:
                integer = (int)bits;
                break;
            case ScalarKind.UInt:
                integer = (uint)bits;
                break;
            case ScalarKind.Int16:
                integer = (short)bits;
                break;
            case ScalarKind.UInt16:
                integer = (ushort)bits;
                break;
            case ScalarKind.Int64:
                integer = (long)bits;
                break;
            case ScalarKind.UInt64:
                integer = (long)bits;
                isUnsigned64 = true;
                break;
            case ScalarKind.Half:
            {
                var h = BitConverter.UInt16BitsToHalf((ushort)bits);
                if (!Plain(h))
                {
                    return false;
                }

                d = (double)h;
                isFloat = true;
                break;
            }

            case ScalarKind.Float:
            {
                float f = BitConverter.UInt32BitsToSingle((uint)bits);
                if (!Plain(f))
                {
                    return false;
                }

                d = f;
                isFloat = true;
                break;
            }

            case ScalarKind.Double:
            {
                d = BitConverter.UInt64BitsToDouble(bits);
                if (!Plain(d))
                {
                    return false;
                }

                isFloat = true;
                break;
            }

            default:
                return false;
        }

        // Integer sources above 2^53 do not convert exactly to floating point on every runtime; leave those to the device.
        if (!isFloat && to is ScalarKind.Half or ScalarKind.Float or ScalarKind.Double && (isUnsigned64 ? (ulong)integer > (1UL << 53) : Math.Abs(integer) > (1L << 53)))
        {
            return false;
        }

        switch (to)
        {
            case ScalarKind.Bool:
                r = (isFloat ? d != 0 : integer != 0) ? 1UL : 0UL;
                return true;
            case ScalarKind.Int or ScalarKind.UInt or ScalarKind.Int16 or ScalarKind.UInt16 or ScalarKind.Int64 or ScalarKind.UInt64:
            {
                int width = WidthOf(to);
                bool signed = to is ScalarKind.Int or ScalarKind.Int16 or ScalarKind.Int64;
                ulong mask = MaskOf(width);
                if (!isFloat)
                {
                    // Integer to integer keeps the low bits (the cast wraps).
                    r = unchecked((ulong)integer) & mask;
                    return true;
                }

                double t = Math.Truncate(d);
                double lo = signed ? -Math.Pow(2, width - 1) : 0;
                double hi = signed ? Math.Pow(2, width - 1) - 1 : Math.Pow(2, width) - 1;
                if (width == 64)
                {
                    // Stay clear of the last representable values: the limit itself does not convert the same way everywhere.
                    if (t <= lo || t >= hi)
                    {
                        return false;
                    }
                }
                else if (t < lo || t > hi)
                {
                    return false;
                }

                r = signed ? unchecked((ulong)(long)t) & mask : (ulong)t & mask;
                return true;
            }

            case ScalarKind.Half:
            {
                var h = (Half)(isFloat ? d : (isUnsigned64 ? (double)(ulong)integer : integer));
                if (!Plain(h))
                {
                    return false;
                }

                r = BitConverter.HalfToUInt16Bits(h);
                return true;
            }

            case ScalarKind.Float:
            {
                float f = from == ScalarKind.Double ? (float)d : isFloat ? (float)d : isUnsigned64 ? (float)(double)(ulong)integer : (float)(double)integer;
                if (!Plain(f))
                {
                    return false;
                }

                r = BitConverter.SingleToUInt32Bits(f);
                return true;
            }

            case ScalarKind.Double:
                r = BitConverter.DoubleToUInt64Bits(isFloat ? d : isUnsigned64 ? (double)(ulong)integer : integer);
                return true;
            default:
                return false;
        }
    }
}
