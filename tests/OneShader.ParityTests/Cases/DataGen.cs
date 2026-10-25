using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;

namespace OneShader.ParityTests;

/// <summary>
/// Turns a case's data expression into bytes, deterministically. Tokens are whitespace separated:
/// <c>1.5</c> or <c>2e-3</c> a float, <c>7u</c> or <c>0xFFu</c> an unsigned int, <c>-3i</c> a signed int,
/// and generators <c>@f01(seed,count)</c> (floats in [0,1)), <c>@fpm1(seed,count)</c> (floats in [-1,1)),
/// <c>@u32(seed,count[,modulus])</c> and <c>@quadindex(quadCount)</c> (0 1 2 2 1 3 per quad). Meshes, camera
/// matrices, record patterns and similar scene data are documented on <see cref="MeshGen"/>.
/// </summary>
public static class DataGen
{
    /// <summary>Materializes <paramref name="expression"/>; with <paramref name="indexBytes"/> 2, integers are written as 16-bit.</summary>
    public static byte[] Materialize(string expression, int indexBytes = 4)
    {
        var words = new List<uint>();
        foreach (string token in expression.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith('@'))
            {
                Generate(token, words);
            }
            else
            {
                words.Add(ParseLiteral(token));
            }
        }

        byte[] bytes = new byte[words.Count * indexBytes];
        for (int i = 0; i < words.Count; i++)
        {
            if (indexBytes == 2)
            {
                if (words[i] > ushort.MaxValue)
                {
                    throw new FormatException($"value {words[i]} does not fit a 16-bit index");
                }
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), (ushort)words[i]);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), words[i]);
            }
        }
        return bytes;
    }

    private static uint ParseLiteral(string t)
    {
        var c = CultureInfo.InvariantCulture;
        if (t.EndsWith('u'))
        {
            string digits = t[..^1];
            return digits.StartsWith("0x", StringComparison.Ordinal)
                ? uint.Parse(digits[2..], NumberStyles.HexNumber, c)
                : uint.Parse(digits, NumberStyles.None, c);
        }
        if (t.EndsWith('i'))
        {
            return unchecked((uint)int.Parse(t[..^1], NumberStyles.AllowLeadingSign, c));
        }
        if (t.Contains('.', StringComparison.Ordinal) || t.Contains('e', StringComparison.Ordinal))
        {
            return BitConverter.SingleToUInt32Bits(float.Parse(t, NumberStyles.Float, c));
        }
        throw new FormatException($"'{t}' is not a float (needs a '.'), uint (suffix u) or int (suffix i)");
    }

    private static void Generate(string token, List<uint> words)
    {
        int open = token.IndexOf('(', StringComparison.Ordinal);
        if (open < 0 || !token.EndsWith(')'))
        {
            throw new FormatException($"bad generator '{token}'");
        }
        string name = token[1..open];
        string[] a = token[(open + 1)..^1].Split(',');
        var c = CultureInfo.InvariantCulture;
        switch (name)
        {
            case "f01":
            case "fpm1":
                {
                    var rng = new Rng(uint.Parse(a[0], c));
                    int n = int.Parse(a[1], c);
                    for (int i = 0; i < n; i++)
                    {
                        float f = rng.NextFloat01();
                        words.Add(BitConverter.SingleToUInt32Bits(name == "f01" ? f : f * 2f - 1f));
                    }
                    break;
                }
            case "u32":
                {
                    var rng = new Rng(uint.Parse(a[0], c));
                    int n = int.Parse(a[1], c);
                    uint mod = a.Length > 2 ? uint.Parse(a[2], c) : 0;
                    for (int i = 0; i < n; i++)
                    {
                        uint v = rng.Next();
                        words.Add(mod == 0 ? v : v % mod);
                    }
                    break;
                }
            case "quadindex":
                {
                    int n = int.Parse(a[0], c);
                    ReadOnlySpan<uint> pattern = [0, 1, 2, 2, 1, 3];
                    for (int q = 0; q < n; q++)
                    {
                        foreach (uint p in pattern)
                        {
                            words.Add((uint)(q * 4) + p);
                        }
                    }
                    break;
                }
            default:
                if (!MeshGen.TryGenerate(name, a, words))
                {
                    throw new FormatException($"unknown generator '@{name}'");
                }
                break;
        }
    }

    /// <summary>Small xorshift generator so data never depends on the runtime's Random.</summary>
    internal struct Rng
    {
        private uint state;

        public Rng(uint seed)
        {
            state = (seed * 2654435761u) ^ 0x9E3779B9u;
            if (state == 0)
            {
                state = 1;
            }
        }

        public uint Next()
        {
            uint x = state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            state = x;
            return x;
        }

        public float NextFloat01() => (Next() >> 8) / 16777216f;
    }
}
