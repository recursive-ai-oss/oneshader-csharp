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
        "wgsl_shared_rw" => SharedRw(model, data),
        "image_filter" => ImageFilter(model, data),
        "particles" => Particles(model, data),
        "push_kernel" => PushKernel(model, data),
        "cull_instances" => CullInstances(model, data),
        "cluster_build" => ClusterBuild(model, data),
        "light_cull" => LightCull(model, data),
        "depth_bounds" => DepthBounds(model, data),
        "wave_scan_compact" => WaveScanCompact(model, data),
        "wave_reduce" => WaveReduce(model, data),
        "dot4_packed" => Dot4Packed(model, data),
        "float_extremes" => FloatExtremes(model, data),
        "wave_ballot" => WaveBallot(model, data),
        "spec_const_compute" => SpecConstCompute(model, data),
        "coop_f16" => CoopProduct(model, data, bfloat: false),
        "coop_bf16" => CoopProduct(model, data, bfloat: true),
        "coop_layouts" => CoopLayouts(model, data),
        "coop_gemm_f32" => CoopGemmF32(model, data),
        "continue_unrolled" => ContinueUnrolled(model, data),
        "atomics_signed" => AtomicsSigned(model, data),
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

    private static float Half(byte[] bytes, int index) => (float)BitConverter.ToHalf(bytes, index * 2);

    private static float BFloat(byte[] bytes, int index) => DataGen.FromBFloat16Bits(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(index * 2)));

    private static byte[] Floats(float[] values, int sizeBytes)
    {
        byte[] b = new byte[sizeBytes];
        for (int i = 0; i < values.Length && i * 4 < sizeBytes; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(i * 4), values[i]);
        }
        return b;
    }

    // The 16x16x16 cooperative-matrix kernels: C = A x B with float accumulation, row-major. The bfloat16 kernel doubles B on the way in.
    private static IReadOnlyList<Expectation> CoopProduct(CaseModel m, CaseData d, bool bfloat)
    {
        int a = Find(m, ResourceKind.Storage, 0, 0);
        int b = Find(m, ResourceKind.Storage, 0, 1);
        int c = Find(m, ResourceKind.Storage, 0, 2);
        float[] result = new float[256];
        for (int r = 0; r < 16; r++)
        {
            for (int n = 0; n < 16; n++)
            {
                float sum = 0;
                for (int k = 0; k < 16; k++)
                {
                    float x = bfloat ? BFloat(d.Buffers[a], r * 16 + k) : Half(d.Buffers[a], r * 16 + k);
                    float y = bfloat ? BFloat(d.Buffers[b], k * 16 + n) * 2f : Half(d.Buffers[b], k * 16 + n);
                    sum += x * y;
                }
                result[r * 16 + n] = sum;
            }
        }
        return [new Expectation(Key(m, c), Floats(result, d.Buffers[c].Length), ExpectKind.FloatRelative, 1e-4)];
    }

    // coop_gemm_f32: C (64 x 64) = A (64 x 128) x B (128 x 64), all float and row-major.
    private static IReadOnlyList<Expectation> CoopGemmF32(CaseModel m, CaseData d)
    {
        int a = Find(m, ResourceKind.Storage, 0, 0);
        int b = Find(m, ResourceKind.Storage, 0, 1);
        int c = Find(m, ResourceKind.Storage, 0, 2);
        float[] result = new float[64 * 64];
        for (int r = 0; r < 64; r++)
        {
            for (int n = 0; n < 64; n++)
            {
                float sum = 0;
                for (int k = 0; k < 128; k++)
                {
                    sum += BinaryPrimitives.ReadSingleLittleEndian(d.Buffers[a].AsSpan((r * 128 + k) * 4))
                        * BinaryPrimitives.ReadSingleLittleEndian(d.Buffers[b].AsSpan((k * 64 + n) * 4));
                }
                result[r * 64 + n] = sum;
            }
        }
        return [new Expectation(Key(m, c), Floats(result, d.Buffers[c].Length), ExpectKind.FloatRelative, 1e-4)];
    }

    // coop_layouts: A (16 x 32) is column-major at element 8 with stride 24, B (32 x 16) is row-major; C is stored column-major with stride 19, D row-major doubled.
    private static IReadOnlyList<Expectation> CoopLayouts(CaseModel m, CaseData d)
    {
        int a = Find(m, ResourceKind.Storage, 0, 0);
        int b = Find(m, ResourceKind.Storage, 0, 1);
        int c = Find(m, ResourceKind.Storage, 0, 2);
        int dd = Find(m, ResourceKind.Storage, 0, 4);
        int u = Find(m, ResourceKind.Uniform, 0, 3);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(d.Buffers[u].AsSpan(0));
        uint stride = BinaryPrimitives.ReadUInt32LittleEndian(d.Buffers[u].AsSpan(4));
        float[] cOut = new float[19 * 16];
        float[] dOut = new float[256];
        for (int r = 0; r < 16; r++)
        {
            for (int n = 0; n < 16; n++)
            {
                float sum = 0;
                for (int k = 0; k < 32; k++)
                {
                    sum += Half(d.Buffers[a], (int)(offset + k * stride) + r) * Half(d.Buffers[b], k * 16 + n);
                }
                cOut[n * 19 + r] = sum;
                dOut[r * 16 + n] = sum * 2f;
            }
        }
        return
        [
            new Expectation(Key(m, c), Floats(cOut, d.Buffers[c].Length), ExpectKind.FloatRelative, 1e-4),
            new Expectation(Key(m, dd), Floats(dOut, d.Buffers[dd].Length), ExpectKind.FloatRelative, 1e-4),
        ];
    }

    // atomics_signed: every result below is independent of the order the invocations run in, and each one would differ if a
    // signed comparison were done unsigned (or the reverse).
    private static IReadOnlyList<Expectation> AtomicsSigned(CaseModel m, CaseData d)
    {
        int input = Find(m, ResourceKind.Storage, 0, 0);
        int signed = Find(m, ResourceKind.Storage, 0, 1);
        int unsigned = Find(m, ResourceKind.Storage, 0, 2);
        int raw = Find(m, ResourceKind.Storage, 0, 3);
        int groups = Find(m, ResourceKind.Storage, 0, 4);
        uint[] w = Words(d.Buffers[input]);
        const int Count = 256;
        const int GroupSize = 64;
        int[] v = new int[Count];
        for (int i = 0; i < Count; i++)
        {
            v[i] = unchecked((int)w[i]);
        }

        uint[] s = new uint[16];
        s[0] = unchecked((uint)v.Take(Count).Min());
        s[1] = unchecked((uint)v.Take(Count).Max());
        s[2] = 7;
        s[3] = 1;
        s[4] = 7 * (Count - 1);
        s[5] = unchecked((uint)-5);
        s[6] = unchecked((uint)(-5 * Count));
        s[7] = unchecked((uint)-2);
        s[8] = 1;
        s[9] = int.MaxValue;
        s[10] = unchecked((uint)int.MinValue);
        s[11] = s[0];
        s[12] = 5;

        uint[] u = new uint[16];
        u[0] = w.Take(Count).Min();
        u[1] = w.Take(Count).Max();
        u[2] = 0x80000001u;
        u[3] = 1;
        u[4] = 3;
        u[5] = unchecked(3u * Count);
        u[6] = 0xFFFFFFF0u;
        u[8] = 0;

        uint[] r = new uint[8];
        r[0] = u[0];
        r[1] = u[1];
        r[2] = 0x80000001u;
        r[3] = 1;
        r[4] = 3;
        r[5] = unchecked(3u * Count);
        r[6] = 0;
        r[7] = 0xFFFFFFF0u;

        uint[] g = new uint[Count / GroupSize * 16];
        for (int grp = 0; grp < Count / GroupSize; grp++)
        {
            var gv = v.Skip(grp * GroupSize).Take(GroupSize).ToArray();
            var gw = w.Skip(grp * GroupSize).Take(GroupSize).ToArray();
            int b = grp * 16;
            g[b + 0] = unchecked((uint)gv.Min());
            g[b + 1] = unchecked((uint)gv.Max());
            g[b + 2] = 7;
            g[b + 3] = unchecked((uint)-2);
            g[b + 4] = unchecked((uint)int.MinValue);
            g[b + 5] = 3;
            g[b + 6] = 7 * (GroupSize - 1);
            g[b + 7] = 1;
            g[b + 8] = gw.Min();
            g[b + 9] = gw.Max();
            g[b + 10] = 0x80000001u;
            g[b + 11] = 1;
        }

        return
        [
            new Expectation(Key(m, signed), Bytes(s, d.Buffers[signed].Length), ExpectKind.Exact, 0),
            new Expectation(Key(m, unsigned), Bytes(u, d.Buffers[unsigned].Length), ExpectKind.Exact, 0),
            new Expectation(Key(m, raw), Bytes(r, d.Buffers[raw].Length), ExpectKind.Exact, 0),
            new Expectation(Key(m, groups), Bytes(g, d.Buffers[groups].Length), ExpectKind.Exact, 0),
        ];
    }

    private static string Key(CaseModel m, int index) => m.Resources[index].OutputKey;

    // Only the Count.x by Count.y block of the 4 x 4 output is written; the 7s the case starts with must survive everywhere else.
    private static IReadOnlyList<Expectation> ContinueUnrolled(CaseModel m, CaseData d)
    {
        int counts = Find(m, ResourceKind.Uniform, 0, 0);
        int output = Find(m, ResourceKind.Storage, 0, 1);
        uint[] count = Words(d.Buffers[counts]);
        uint[] result = new uint[16];
        Array.Fill(result, 7u);
        for (uint row = 0; row < 4; row++)
        {
            for (uint col = 0; col < 4; col++)
            {
                if (row < count[0] && col < count[1])
                {
                    result[row * 4 + col] = 1;
                }
            }
        }

        return [new Expectation(Key(m, output), Bytes(result, d.Buffers[output].Length), ExpectKind.Exact, 0)];
    }

    // The specialization-constant kernel: the case's 'spec' lines override the defaults the shader declares.
    private static IReadOnlyList<Expectation> SpecConstCompute(CaseModel m, CaseData d)
    {
        string? Override(string name) => m.Specs.Where(s => s.Key == name).Select(s => s.Value).FirstOrDefault();
        bool useOffset = Override("UseOffset") is { } uo ? uo == "true" : false;
        int rounds = Override("Rounds") is { } r ? int.Parse(r, System.Globalization.CultureInfo.InvariantCulture) : 4;
        uint mask = Override("Mask") is { } mk ? uint.Parse(mk, System.Globalization.CultureInfo.InvariantCulture) : 0xFFu;
        float scale = Override("Scale") is { } sc ? float.Parse(sc, System.Globalization.CultureInfo.InvariantCulture) : 0.5f;
        int bias = rounds * 3 + 1;
        float shift = scale * 8.0f;
        int ints = Find(m, ResourceKind.Storage, 0, 0);
        int floats = Find(m, ResourceKind.Storage, 0, 1);
        uint[] outInts = new uint[32];
        byte[] outFloats = new byte[d.Buffers[floats].Length];
        for (uint id = 0; id < 32; id++)
        {
            uint acc = 0;
            for (int i = 0; i < rounds; i++)
            {
                acc += (id + (uint)i) & mask;
            }

            if (useOffset)
            {
                acc += 1000u;
            }

            outInts[id] = acc + (uint)bias;
            float f = useOffset ? (id * scale) + shift : (id * scale) - shift;
            BinaryPrimitives.WriteSingleLittleEndian(outFloats.AsSpan((int)id * 4), f);
        }

        return
        [
            new Expectation(Key(m, ints), Bytes(outInts, d.Buffers[ints].Length), ExpectKind.Exact, 0),
            new Expectation(Key(m, floats), outFloats, ExpectKind.Exact, 0),
        ];
    }

    // Exclusive scan per 256-element block, and the sum of each block.
    private static IReadOnlyList<Expectation> SharedRw(CaseModel m, CaseData d)
    {
        int input = Find(m, ResourceKind.Storage, 0, 0);
        int output = Find(m, ResourceKind.Storage, 0, 1);
        uint[] values = Words(d.Buffers[input]);
        uint[] sums = new uint[values.Length];
        for (int i = 0; i < sums.Length; i++)
        {
            sums[i] = values[i] + 1;
        }
        return [new Expectation(Key(m, output), Bytes(sums, d.Buffers[output].Length), ExpectKind.Exact, 0)];
    }

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
    // Reads the block the case pushed: Count 0, Scale 4, Offset 16, Mode 28, Bias 32, Mul 48. A kernel that never received
    // the push constants would see Count 0 and write nothing, which this reference does not expect.
    private static IReadOnlyList<Expectation> PushKernel(CaseModel m, CaseData d)
    {
        byte[] p = d.Push ?? throw new InvalidOperationException("push_kernel needs push constants");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(0));
        float scale = BitConverter.ToSingle(p, 4);
        uint mode = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(28));
        float[] offset = [BitConverter.ToSingle(p, 16), BitConverter.ToSingle(p, 20), BitConverter.ToSingle(p, 24)];
        float[] bias = [BitConverter.ToSingle(p, 32), BitConverter.ToSingle(p, 36)];
        float[] mul = [BitConverter.ToSingle(p, 48), BitConverter.ToSingle(p, 52), BitConverter.ToSingle(p, 56), BitConverter.ToSingle(p, 60)];
        int input = Find(m, ResourceKind.Storage, 0, 0);
        int output = Find(m, ResourceKind.Storage, 0, 1);
        float[] src = new float[d.Buffers[input].Length / 4];
        Buffer.BlockCopy(d.Buffers[input], 0, src, 0, d.Buffers[input].Length);
        float[] dst = new float[d.Buffers[output].Length / 4];
        for (int i = 0; i < count; i++)
        {
            float[] v = [(src[i * 4] * scale) + offset[0], (src[(i * 4) + 1] * scale) + offset[1], (src[(i * 4) + 2] * scale) + offset[2], src[(i * 4) + 3]];
            v[0] += bias[0];
            v[1] += bias[1];
            for (int k = 0; k < 4; k++)
            {
                v[k] *= mul[k];
            }

            for (int k = 0; k < 4; k++)
            {
                dst[(i * 4) + k] = mode == 1 ? v[3 - k] : v[k];
            }
        }

        byte[] bytes = new byte[dst.Length * 4];
        Buffer.BlockCopy(dst, 0, bytes, 0, bytes.Length);
        return [new Expectation(Key(m, output), bytes, ExpectKind.FloatRelative, 1e-5)];
    }

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
    // Sphere-versus-plane frustum test for each instance. The list holds the visible indices in any order, so
    // the case compares it sorted; the flags are per instance; the draw arguments are index count and visible count.
    private static IReadOnlyList<Expectation> CullInstances(CaseModel m, CaseData d)
    {
        int uniform = Find(m, ResourceKind.Uniform, 0, 0);
        int instances = Find(m, ResourceKind.Storage, 0, 1);
        int list = Find(m, ResourceKind.Storage, 0, 2);
        int flags = Find(m, ResourceKind.Storage, 0, 3);
        int args = Find(m, ResourceKind.Storage, 0, 4);
        byte[] u = d.Buffers[uniform];
        float[] planes = new float[24];
        Buffer.BlockCopy(u, 0, planes, 0, 96);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(u.AsSpan(96));
        uint indexCount = BinaryPrimitives.ReadUInt32LittleEndian(u.AsSpan(100));
        float[] inst = new float[d.Buffers[instances].Length / 4];
        Buffer.BlockCopy(d.Buffers[instances], 0, inst, 0, d.Buffers[instances].Length);

        uint[] visibleList = new uint[d.Buffers[list].Length / 4];
        uint[] visibleFlags = new uint[d.Buffers[flags].Length / 4];
        int visible = 0;
        for (int i = 0; i < count; i++)
        {
            float cx = inst[i * 4];
            float cy = inst[(i * 4) + 1];
            float cz = inst[(i * 4) + 2];
            float radius = inst[(i * 4) + 3];
            bool inside = true;
            for (int p = 0; p < 6 && inside; p++)
            {
                float distance = (planes[p * 4] * cx) + (planes[(p * 4) + 1] * cy) + (planes[(p * 4) + 2] * cz) + planes[(p * 4) + 3];
                inside = !(distance < -radius);
            }
            if (inside)
            {
                visibleList[visible++] = (uint)i;
                visibleFlags[i] = 1;
            }
        }
        Array.Sort(visibleList);
        return
        [
            new Expectation(Key(m, list), Bytes(visibleList, d.Buffers[list].Length), ExpectKind.Exact, 0),
            new Expectation(Key(m, flags), Bytes(visibleFlags, d.Buffers[flags].Length), ExpectKind.Exact, 0),
            new Expectation(Key(m, args), Bytes([indexCount, (uint)visible], d.Buffers[args].Length), ExpectKind.Exact, 0),
        ];
    }

    // View-space bounds of each cluster: the tile's corner rays through the near-plane points of the inverse
    // projection, scaled to the slice's near and far depth (exponential slices). Min/Max .w hold the slice depths.
    private static IReadOnlyList<Expectation> ClusterBuild(CaseModel m, CaseData d)
    {
        int uniform = Find(m, ResourceKind.Uniform, 0, 0);
        int clusters = Find(m, ResourceKind.Storage, 0, 1);
        byte[] u = d.Buffers[uniform];
        float[] f = new float[24];
        Buffer.BlockCopy(u, 0, f, 0, 96);
        // The matrix is column-major: element (row, column) is at column * 4 + row.
        double Inv(int r, int c) => f[(c * 4) + r];
        uint[] words = Words(u);
        uint gx = words[16];
        uint gy = words[17];
        uint gz = words[18];
        uint tile = words[19];
        double screenX = f[20];
        double screenY = f[21];
        double near = f[22];
        double far = f[23];

        double[] NearPoint(double px, double py)
        {
            double ndcX = (px / screenX * 2) - 1;
            double ndcY = (py / screenY * 2) - 1;
            double[] v = new double[4];
            for (int r = 0; r < 4; r++)
            {
                v[r] = (Inv(r, 0) * ndcX) + (Inv(r, 1) * ndcY) + Inv(r, 3);
            }
            return [v[0] / v[3], v[1] / v[3], v[2] / v[3]];
        }

        float[] result = new float[d.Buffers[clusters].Length / 4];
        for (uint z = 0; z < gz; z++)
        {
            double sliceNear = near * Math.Pow(far / near, (double)z / gz);
            double sliceFar = near * Math.Pow(far / near, (double)(z + 1) / gz);
            for (uint y = 0; y < gy; y++)
            {
                for (uint x = 0; x < gx; x++)
                {
                    double[] lo = NearPoint(x * tile, y * tile);
                    double[] hi = NearPoint((x + 1) * tile, (y + 1) * tile);
                    double[] min = [double.MaxValue, double.MaxValue, double.MaxValue];
                    double[] max = [double.MinValue, double.MinValue, double.MinValue];
                    foreach (double[] p in new[] { lo, hi })
                    {
                        foreach (double depth in new[] { sliceNear, sliceFar })
                        {
                            double k = depth / -p[2];
                            for (int c = 0; c < 3; c++)
                            {
                                min[c] = Math.Min(min[c], p[c] * k);
                                max[c] = Math.Max(max[c], p[c] * k);
                            }
                        }
                    }
                    int o = (int)(x + (gx * (y + (gy * z)))) * 8;
                    for (int c = 0; c < 3; c++)
                    {
                        result[o + c] = (float)min[c];
                        result[o + 4 + c] = (float)max[c];
                    }
                    result[o + 3] = (float)sliceNear;
                    result[o + 7] = (float)sliceFar;
                }
            }
        }
        byte[] bytes = new byte[result.Length * 4];
        Buffer.BlockCopy(result, 0, bytes, 0, bytes.Length);
        return [new Expectation(Key(m, clusters), bytes, ExpectKind.FloatRelative, 1e-4)];
    }
    // Each cluster lists, in light order and up to the per-cluster maximum, the lights whose sphere touches its box.
    private static IReadOnlyList<Expectation> LightCull(CaseModel m, CaseData d)
    {
        int uniform = Find(m, ResourceKind.Uniform, 0, 0);
        int clusters = Find(m, ResourceKind.Storage, 0, 1);
        int lights = Find(m, ResourceKind.Storage, 0, 2);
        int grid = Find(m, ResourceKind.Storage, 0, 3);
        int indices = Find(m, ResourceKind.Storage, 0, 4);
        uint[] u = Words(d.Buffers[uniform]);
        uint clusterCount = u[0];
        uint lightCount = u[1];
        uint max = u[2];
        float[] boxes = new float[d.Buffers[clusters].Length / 4];
        Buffer.BlockCopy(d.Buffers[clusters], 0, boxes, 0, d.Buffers[clusters].Length);
        float[] ls = new float[d.Buffers[lights].Length / 4];
        Buffer.BlockCopy(d.Buffers[lights], 0, ls, 0, d.Buffers[lights].Length);
        uint[] gridWords = new uint[d.Buffers[grid].Length / 4];
        uint[] indexWords = new uint[d.Buffers[indices].Length / 4];
        for (uint c = 0; c < clusterCount; c++)
        {
            uint count = 0;
            for (uint l = 0; l < lightCount && count < max; l++)
            {
                float distanceSquared = 0;
                for (int axis = 0; axis < 3; axis++)
                {
                    float centre = ls[(l * 8) + axis];
                    float closest = Math.Clamp(centre, boxes[(c * 8) + axis], boxes[(c * 8) + 4 + axis]);
                    distanceSquared += (closest - centre) * (closest - centre);
                }
                float radius = ls[(l * 8) + 3];
                if (distanceSquared <= radius * radius)
                {
                    indexWords[(c * max) + count++] = l;
                }
            }
            gridWords[c * 2] = c * max;
            gridWords[(c * 2) + 1] = count;
        }
        return
        [
            new Expectation(Key(m, grid), Bytes(gridWords, d.Buffers[grid].Length), ExpectKind.Exact, 0),
            new Expectation(Key(m, indices), Bytes(indexWords, d.Buffers[indices].Length), ExpectKind.Exact, 0),
        ];
    }

    // Smallest and largest positive depth per 16x16 tile; a tile with no positive depth reads (0, 0).
    private static IReadOnlyList<Expectation> DepthBounds(CaseModel m, CaseData d)
    {
        int uniform = Find(m, ResourceKind.Uniform, 0, 0);
        int depth = Find(m, ResourceKind.Texture, 0, 1);
        int bounds = Find(m, ResourceKind.Storage, 0, 2);
        uint[] u = Words(d.Buffers[uniform]);
        int width = (int)u[0];
        int height = (int)u[1];
        int tilesX = (width + 15) / 16;
        int tilesY = (height + 15) / 16;
        float[] texels = new float[width * height];
        Buffer.BlockCopy(d.Images[depth], 0, texels, 0, texels.Length * 4);
        float[] result = new float[d.Buffers[bounds].Length / 4];
        for (int ty = 0; ty < tilesY; ty++)
        {
            for (int tx = 0; tx < tilesX; tx++)
            {
                float min = float.MaxValue;
                float max = 0;
                for (int y = ty * 16; y < Math.Min((ty + 1) * 16, height); y++)
                {
                    for (int x = tx * 16; x < Math.Min((tx + 1) * 16, width); x++)
                    {
                        float v = texels[(y * width) + x];
                        if (v > 0)
                        {
                            min = Math.Min(min, v);
                            max = Math.Max(max, v);
                        }
                    }
                }
                int o = ((ty * tilesX) + tx) * 2;
                result[o] = max == 0 ? 0 : min;
                result[o + 1] = max;
            }
        }
        byte[] bytes = new byte[result.Length * 4];
        Buffer.BlockCopy(result, 0, bytes, 0, bytes.Length);
        return [new Expectation(Key(m, bounds), bytes, ExpectKind.Exact, 0)];
    }

    // Exclusive scan and sums per group of 256, and the values divisible by 3 packed in order at the start of each group's region.
    private static IReadOnlyList<Expectation> WaveScanCompact(CaseModel m, CaseData d)
    {
        uint[] values = Words(d.Buffers[Find(m, ResourceKind.Storage, 0, 0)]);
        int scanIndex = Find(m, ResourceKind.Storage, 0, 1);
        int sumsIndex = Find(m, ResourceKind.Storage, 0, 2);
        int compactedIndex = Find(m, ResourceKind.Storage, 0, 3);
        int keptIndex = Find(m, ResourceKind.Storage, 0, 4);
        int groups = m.Dispatches[0].X;
        uint[] scan = new uint[values.Length];
        uint[] sums = new uint[groups];
        uint[] compacted = new uint[values.Length];
        uint[] kept = new uint[groups];
        for (int g = 0; g < groups; g++)
        {
            uint running = 0;
            for (int i = g * 256; i < (g + 1) * 256; i++)
            {
                scan[i] = running;
                running += values[i];
                if (values[i] % 3 == 0)
                {
                    compacted[(g * 256) + kept[g]++] = values[i];
                }
            }
            sums[g] = running;
        }
        return
        [
            new Expectation(Key(m, scanIndex), Bytes(scan, d.Buffers[scanIndex].Length), ExpectKind.Exact, 0),
            new Expectation(Key(m, sumsIndex), Bytes(sums, d.Buffers[sumsIndex].Length), ExpectKind.Exact, 0),
            new Expectation(Key(m, compactedIndex), Bytes(compacted, d.Buffers[compactedIndex].Length), ExpectKind.Exact, 0),
            new Expectation(Key(m, keptIndex), Bytes(kept, d.Buffers[keptIndex].Length), ExpectKind.Exact, 0),
        ];
    }

    // Sixteen reductions per group of 256 (see the shader for the meaning of each word); integers wrap and floats are exact.
    private static IReadOnlyList<Expectation> WaveReduce(CaseModel m, CaseData d)
    {
        uint[] values = Words(d.Buffers[Find(m, ResourceKind.Storage, 0, 0)]);
        int resultIndex = Find(m, ResourceKind.Storage, 0, 1);
        int groups = m.Dispatches[0].X;
        uint[] r = new uint[groups * 16];
        for (int g = 0; g < groups; g++)
        {
            uint sum = 0, min = uint.MaxValue, max = 0, and = uint.MaxValue, or = 0, xor = 0, product = 1, sumLow = 0, odd = 0;
            int smin = int.MaxValue, smax = int.MinValue;
            float fmin = 1.0e30f, fmax = -1.0e30f, fsum = 0, vmax = -1.0e30f;
            for (int i = g * 256; i < (g + 1) * 256; i++)
            {
                uint v = values[i];
                int s = (int)v - 500;
                sum += v;
                min = Math.Min(min, v);
                max = Math.Max(max, v);
                and &= (v << 20) | 0x0F0F0000u;
                or |= v;
                xor ^= v;
                product *= v | 1u;
                smin = Math.Min(smin, s);
                smax = Math.Max(smax, s);
                fmin = Math.Min(fmin, s);
                fmax = Math.Max(fmax, s);
                fsum += v & 255u;
                sumLow += v & 7u;
                vmax = Math.Max(vmax, 2.0f * v);
                odd += v & 1u;
            }
            uint F(float x) => BitConverter.SingleToUInt32Bits(x);
            uint[] row = [sum, min, max, and, or, xor, product, F(fmin), F(fmax), F(fsum), (uint)smin, (uint)smax, sum, sumLow, F(vmax), odd];
            Array.Copy(row, 0, r, g * 16, 16);
        }
        return [new Expectation(Key(m, resultIndex), Bytes(r, d.Buffers[resultIndex].Length), ExpectKind.Exact, 0)];
    }

    // Bit patterns of the constants float_extremes.hlsl writes; the first is the running maximum of the four seed values (-2, -1, -2^127 and -100: -1).
    private static IReadOnlyList<Expectation> FloatExtremes(CaseModel m, CaseData d)
    {
        int bi = Find(m, ResourceKind.Storage, 0, 1);
        uint[] bits =
        [
            BitConverter.SingleToUInt32Bits(-1f), 0xff7fffffu, 0x7f7fffffu, 0x7f7fffffu, 0xff7fffffu, 0x7f7ffffeu, 0x00800000u, 0x80800000u,
            0x80000000u, BitConverter.SingleToUInt32Bits(16777216f), BitConverter.SingleToUInt32Bits(0.1f), 0x4b7fffffu,
        ];
        int vi = Find(m, ResourceKind.Storage, 0, 2);
        uint[] values = [0xff7fffffu, 0x7f7fffffu, 0xff7fffffu, 0x00800000u, 0x80000000u, BitConverter.SingleToUInt32Bits(16777216f)];
        return
        [
            new Expectation(Key(m, bi), Bytes(bits, d.Buffers[bi].Length), ExpectKind.Exact, 0),
            new Expectation(Key(m, vi), Bytes(values, d.Buffers[vi].Length), ExpectKind.Exact, 0),
        ];
    }

    // Packed 8-bit dot products, lane 0 the least significant byte, added to the accumulator with wrapping.
    private static IReadOnlyList<Expectation> Dot4Packed(CaseModel m, CaseData d)
    {
        uint[] a = Words(d.Buffers[Find(m, ResourceKind.Storage, 0, 0)]);
        uint[] b = Words(d.Buffers[Find(m, ResourceKind.Storage, 0, 1)]);
        int si = Find(m, ResourceKind.Storage, 0, 2), ui = Find(m, ResourceKind.Storage, 0, 3), ni = Find(m, ResourceKind.Storage, 0, 4);
        uint[] s = Words(d.Buffers[si]);
        uint[] u = Words(d.Buffers[ui]);
        uint[] sums = new uint[s.Length];
        for (int i = 0; i < a.Length; i++)
        {
            int sd = 0;
            uint ud = 0, byteSumA = 0, byteSumB = 0;
            for (int lane = 0; lane < 4; lane++)
            {
                int shift = 8 * lane;
                sd += (sbyte)(a[i] >> shift) * (sbyte)(b[i] >> shift);
                ud += ((a[i] >> shift) & 255u) * ((b[i] >> shift) & 255u);
                byteSumA = unchecked((uint)((int)byteSumA + (sbyte)(a[i] >> shift)));
                byteSumB += (b[i] >> shift) & 255u;
            }

            unchecked
            {
                s[i] = (uint)sd + s[i];
                u[i] = ud + u[i];
                sums[i] = byteSumA + byteSumB;
            }
        }

        return
        [
            new Expectation(Key(m, si), Bytes(s, d.Buffers[si].Length), ExpectKind.Exact, 0),
            new Expectation(Key(m, ui), Bytes(u, d.Buffers[ui].Length), ExpectKind.Exact, 0),
            new Expectation(Key(m, ni), Bytes(sums, d.Buffers[ni].Length), ExpectKind.Exact, 0),
        ];
    }

    // Eight totals per group that hold for any subgroup size of at most 128 lanes (see the shader).
    private static IReadOnlyList<Expectation> WaveBallot(CaseModel m, CaseData d)
    {
        uint[] values = Words(d.Buffers[Find(m, ResourceKind.Storage, 0, 0)]);
        int totalsIndex = Find(m, ResourceKind.Storage, 0, 1);
        int groups = m.Dispatches[0].X;
        uint[] t = new uint[groups * 8];
        for (int g = 0; g < groups; g++)
        {
            uint count = 0;
            for (int i = g * 256; i < (g + 1) * 256; i++)
            {
                if (values[i] % 5 == 0)
                {
                    count++;
                }
            }
            uint below = 128u * (uint)(g % 3);
            uint[] row = [count, below, below, 256, 256, 256, count, 256];
            Array.Copy(row, 0, t, g * 8, 8);
        }
        return [new Expectation(Key(m, totalsIndex), Bytes(t, d.Buffers[totalsIndex].Length), ExpectKind.Exact, 0)];
    }
}
