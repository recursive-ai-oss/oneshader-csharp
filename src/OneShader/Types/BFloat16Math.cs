using System;

namespace OneShader.Types;

/// <summary>bfloat16 (the upper 16 bits of an IEEE binary32) conversions used to fold constants.</summary>
internal static class BFloat16Math
{
    /// <summary>The bfloat16 bits of a float, rounded to nearest even; NaN stays a NaN.</summary>
    public static ushort FromSingle(float value)
    {
        uint bits = BitConverter.SingleToUInt32Bits(value);
        if (float.IsNaN(value))
        {
            return (ushort)((bits >> 16) | 0x0040);
        }

        uint rounding = 0x7FFF + ((bits >> 16) & 1);
        return (ushort)((bits + rounding) >> 16);
    }

    /// <summary>The exact float a bfloat16 bit pattern stands for.</summary>
    public static float ToSingle(ulong bits) => BitConverter.UInt32BitsToSingle((uint)(ushort)bits << 16);
}
