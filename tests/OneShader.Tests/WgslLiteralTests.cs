using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using OneShader.Wgsl;
using Xunit;

namespace OneShader.Tests;

/// <summary>WGSL float literals must be in range as real numbers and read back as the same bits.</summary>
public sealed class WgslLiteralTests
{
    private const string MaxF32 = "340282346638528859811704183484516925440";

    // Parses text the way the WGSL spec does: the exact real value of the decimal, rounded to nearest even, with an error above the maximum.
    private static float ParseExact(string text)
    {
        string t = text.Trim('(', ')');
        Assert.EndsWith("f", t, StringComparison.Ordinal);
        t = t[..^1];
        bool neg = t.StartsWith('-');
        if (neg)
        {
            t = t[1..];
        }

        int e = 0;
        int ei = t.IndexOfAny(['e', 'E']);
        if (ei >= 0)
        {
            e = int.Parse(t[(ei + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            t = t[..ei];
        }

        int dot = t.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            e -= t.Length - dot - 1;
            t = t.Remove(dot, 1);
        }

        BigInteger digits = BigInteger.Parse(t, CultureInfo.InvariantCulture);
        // value = digits * 10^e as the fraction num / den.
        BigInteger num = digits, den = BigInteger.One;
        if (e >= 0)
        {
            num *= BigInteger.Pow(10, e);
        }
        else
        {
            den = BigInteger.Pow(10, -e);
        }

        Assert.True(num <= BigInteger.Parse(MaxF32, CultureInfo.InvariantCulture) * den, text + " is above the largest finite f32");

        // Nearest float by search over the neighbours of the correctly rounded double (double rounding cannot matter at these digit counts for a check this tight).
        float guess = (float)((double)digits * Math.Pow(10, e));
        float best = float.NaN;
        BigInteger bestErr = default;
        uint bits = BitConverter.SingleToUInt32Bits(Math.Abs(guess));
        for (long b = Math.Max(0, (long)bits - 40); b <= Math.Min(0x7f7fffff, (long)bits + 40); b++)
        {
            float c = BitConverter.UInt32BitsToSingle((uint)b);
            // |c - value| * den * 2^149 as an integer, using the exact value of c = m * 2^-149.
            BigInteger cNum = new BigInteger((long)b < 0x800000 ? b : ((b & 0x7fffff) | 0x800000)) * BigInteger.Pow(2, (int)((b >> 23) == 0 ? 0 : (b >> 23) - 1));
            BigInteger err = BigInteger.Abs(cNum * den - num * BigInteger.Pow(2, 149));
            if (float.IsNaN(best) || err < bestErr)
            {
                best = c;
                bestErr = err;
            }
        }

        return neg ? -best : best;
    }

    public static IEnumerable<object[]> Edge()
    {
        uint[] bits =
        [
            0x7f7fffff, 0xff7fffff, 0x7f7ffffe, 0x00800000, 0x80800000, 0x007fffff, 0x00000001, 0x80000001, 0x00000000, 0x80000000, 0x3f800000, 0x4b800000,
            0x4b7fffff, 0x7e967699, 0x0a4fb11e, 0x3dcccccd, 0x7f000000, 0x00800001,
        ];
        foreach (uint b in bits)
        {
            yield return [b];
        }

        var rng = new Random(41);
        for (int i = 0; i < 300; i++)
        {
            uint b = (uint)rng.NextInt64(0, 0x100000000);
            if (float.IsFinite(BitConverter.UInt32BitsToSingle(b)))
            {
                yield return [b];
            }
        }
    }

    [Theory]
    [MemberData(nameof(Edge))]
    public void F32LiteralIsInRangeAndRoundTrips(uint bits)
    {
        string text = WgslEmitter.F32Text(BitConverter.UInt32BitsToSingle(bits));
        float back = ParseExact(text);
        Assert.Equal(bits, BitConverter.SingleToUInt32Bits(back));
    }

    [Fact]
    public void LargestFloatIsWrittenWithItsExactDecimal() =>
        Assert.Equal("3.4028234663852885981170418348451692544e38f", WgslEmitter.F32Text(float.MaxValue));

    [Fact]
    public void F16LiteralsAreExact()
    {
        for (int b = 0; b < 0x10000; b++)
        {
            var h = BitConverter.UInt16BitsToHalf((ushort)b);
            if (!Half.IsFinite(h))
            {
                continue;
            }

            string t = WgslEmitter.F16Text(h).Trim('(', ')');
            Assert.EndsWith("h", t, StringComparison.Ordinal);
            float f = float.Parse(t[..^1], CultureInfo.InvariantCulture);
            Assert.Equal((ushort)b, BitConverter.HalfToUInt16Bits((Half)f));
            Assert.True(Math.Abs(f) <= 65504f);
        }
    }
}
