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
        "cull_instances" => CullInstances(model, data),
        "cluster_build" => ClusterBuild(model, data),
        "light_cull" => LightCull(model, data),
        "depth_bounds" => DepthBounds(model, data),
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
}
