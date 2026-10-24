using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace OneShader.ParityTests;

/// <summary>How an expected output is compared with the GPU's.</summary>
public enum ExpectKind
{
    /// <summary>Bytes must be identical.</summary>
    Exact,

    /// <summary>32-bit floats within <c>tolerance * (1 + |expected|)</c>.</summary>
    FloatRelative,

    /// <summary>Bytes within an absolute difference.</summary>
    ByteAbsolute,
}

/// <summary>An output computed on the CPU for one resource.</summary>
public sealed record Expectation(string Key, byte[] Bytes, ExpectKind Kind, double Tolerance);

/// <summary>
/// CPU implementations of the compute corpus shaders. They are written from the shader's intent,
/// not translated line by line, so a shader bug and a harness bug cannot cancel out.
/// </summary>
public static class CpuReferences
{
    /// <summary>Computes the expectations of the reference called <paramref name="name"/> for <paramref name="model"/>.</summary>
    public static IReadOnlyList<Expectation> Compute(string name, CaseModel model, CaseData data) => name switch
    {
        "prefix_sum" => PrefixSum(model, data),
        "histogram" => Histogram(model, data),
        "image_filter" => ImageFilter(model, data),
        "particles" => Particles(model, data),
        _ => throw new ArgumentException($"unknown CPU reference '{name}'"),
    };

    private static int Find(CaseModel m, ResourceKind kind, int set, int binding) =>
        m.Resources.FindIndex(r => r.Kind == kind && r.Set == set && r.Binding == binding);

    private static uint[] Words(byte[] bytes)
    {
        uint[] w = new uint[bytes.Length / 4];
        for (int i = 0; i < w.Length; i++)
        {
            w[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4));
        }
        return w;
    }

    private static byte[] Bytes(uint[] words, int sizeBytes)
    {
        byte[] b = new byte[sizeBytes];
        for (int i = 0; i < words.Length && i * 4 < sizeBytes; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), words[i]);
        }
        return b;
    }

    private static string Key(CaseModel m, int index) => m.Resources[index].OutputKey;

    // Exclusive scan per 256-element block, and the sum of each block.
    private static IReadOnlyList<Expectation> PrefixSum(CaseModel m, CaseData d)
    {
        int input = Find(m, ResourceKind.Storage, 0, 0);
        int output = Find(m, ResourceKind.Storage, 0, 1);
        int sums = Find(m, ResourceKind.Storage, 0, 2);
        uint[] values = Words(d.Buffers[input]);
        int groups = m.Dispatches[0].X;
        uint[] scan = new uint[values.Length];
        uint[] blockSums = new uint[groups];
        for (int g = 0; g < groups; g++)
        {
            uint running = 0;
            for (int i = g * 256; i < (g + 1) * 256; i++)
            {
                scan[i] = running;
                running += values[i];
            }
            blockSums[g] = running;
        }
        return
        [
            new Expectation(Key(m, output), Bytes(scan, d.Buffers[output].Length), ExpectKind.Exact, 0),
            new Expectation(Key(m, sums), Bytes(blockSums, d.Buffers[sums].Length), ExpectKind.Exact, 0),
        ];
    }

    private static IReadOnlyList<Expectation> Histogram(CaseModel m, CaseData d)
    {
        int uniform = Find(m, ResourceKind.Uniform, 0, 0);
        int pixels = Find(m, ResourceKind.Storage, 0, 1);
        int histogram = Find(m, ResourceKind.Storage, 0, 2);
        uint count = Words(d.Buffers[uniform])[0];
        uint[] px = Words(d.Buffers[pixels]);
        uint[] bins = new uint[256];
        for (int i = 0; i < count; i++)
        {
            uint r = px[i] & 0xFF;
            uint g = (px[i] >> 8) & 0xFF;
            uint b = (px[i] >> 16) & 0xFF;
            bins[(r * 54 + g * 183 + b * 19) >> 8]++;
        }
        return [new Expectation(Key(m, histogram), Bytes(bins, d.Buffers[histogram].Length), ExpectKind.Exact, 0)];
    }

    // 5-point sharpen with clamped edges, rounded to 8 bits; one step of slack for the GPU's own rounding.
    private static IReadOnlyList<Expectation> ImageFilter(CaseModel m, CaseData d)
    {
        int source = Find(m, ResourceKind.Texture, 0, 0);
        int result = Find(m, ResourceKind.StorageImage, 0, 1);
        var s = m.Resources[source];
        byte[] src = d.Images[source];
        byte[] outBytes = new byte[s.Width * s.Height * 4];
        float Texel(int x, int y, int c) =>
            src[((Math.Clamp(y, 0, s.Height - 1) * s.Width) + Math.Clamp(x, 0, s.Width - 1)) * 4 + c] / 255f;
        for (int y = 0; y < s.Height; y++)
        {
            for (int x = 0; x < s.Width; x++)
            {
                for (int c = 0; c < 4; c++)
                {
                    float v = Texel(x, y, c);
                    if (c < 3)
                    {
                        float sharpened = (v * 5f) - (Texel(x, y - 1, c) + Texel(x, y + 1, c) + Texel(x - 1, y, c) + Texel(x + 1, y, c));
                        v = Math.Clamp(sharpened, 0f, 1f);
                    }
                    outBytes[(((y * s.Width) + x) * 4) + c] = (byte)MathF.Round(v * 255f);
                }
            }
        }
        return [new Expectation(Key(m, result), outBytes, ExpectKind.ByteAbsolute, 1)];
    }

    // Particle: float2 position, float2 velocity, float4 colour, age, maxAge, 2 padding = 12 floats.
    private static IReadOnlyList<Expectation> Particles(CaseModel m, CaseData d)
    {
        const float gravity = 9.8f;
        int uniform = Find(m, ResourceKind.Uniform, 0, 0);
        int buffer = Find(m, ResourceKind.Storage, 0, 1);
        byte[] u = d.Buffers[uniform];
        float dt = BitConverter.ToSingle(u, 0);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(u.AsSpan(4));
        float boundsX = BitConverter.ToSingle(u, 8);
        float boundsY = BitConverter.ToSingle(u, 12);

        float[] f = new float[d.Buffers[buffer].Length / 4];
        Buffer.BlockCopy(d.Buffers[buffer], 0, f, 0, d.Buffers[buffer].Length);
        foreach (var _ in m.Dispatches)
        {
            for (int i = 0; i < count; i++)
            {
                int o = i * 12;
                float maxAge = MathF.Max(f[o + 9], 0.25f);
                f[o + 8] += dt;
                if (f[o + 8] >= maxAge)
                {
                    f[o + 8] = 0f;
                    f[o + 0] = 0.5f * boundsX;
                    f[o + 1] = 0f;
                    f[o + 2] = 0f;
                    f[o + 3] = 0f;
                }
                else
                {
                    f[o + 3] += gravity * dt;
                    f[o + 0] += f[o + 2] * dt;
                    f[o + 1] += f[o + 3] * dt;
                    if (f[o + 1] > boundsY)
                    {
                        f[o + 1] = (2f * boundsY) - f[o + 1];
                        f[o + 3] = -0.5f * f[o + 3];
                    }
                    if (f[o + 0] < 0f || f[o + 0] > boundsX)
                    {
                        f[o + 0] = Math.Clamp(f[o + 0], 0f, boundsX);
                        f[o + 2] = -f[o + 2];
                    }
                }
                f[o + 7] = 1f - (f[o + 8] / maxAge);
            }
        }
        byte[] bytes = new byte[f.Length * 4];
        Buffer.BlockCopy(f, 0, bytes, 0, bytes.Length);
        return [new Expectation(Key(m, buffer), bytes, ExpectKind.FloatRelative, 1e-4)];
    }
}
