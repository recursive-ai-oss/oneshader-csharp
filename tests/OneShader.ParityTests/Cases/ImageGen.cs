using System;
using System.Globalization;

namespace OneShader.ParityTests;

/// <summary>
/// Procedural texel generators for textures, storage images and render-target backgrounds.
/// A generator is <c>name</c> or <c>name(arg,arg)</c>; results are RGBA floats in [0,1].
/// </summary>
public static class ImageGen
{
    /// <summary>Generates width*height*max(layers,1) RGBA texels, layer-major then row-major.</summary>
    public static float[] Generate(string generator, int width, int height, int layers)
    {
        string name = generator;
        string[] args = [];
        int open = generator.IndexOf('(', StringComparison.Ordinal);
        if (open >= 0)
        {
            name = generator[..open];
            args = generator[(open + 1)..generator.LastIndexOf(')')].Split(',');
        }
        var c = CultureInfo.InvariantCulture;
        int layerCount = Math.Max(layers, 1);
        float[] texels = new float[width * height * layerCount * 4];
        for (int l = 0; l < layerCount; l++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float u = (x + 0.5f) / width;
                    float v = (y + 0.5f) / height;
                    float[] px = name switch
                    {
                        "checker" => Checker(x, y, int.Parse(args[0], c)),
                        "gradient" => [u, v, 0.5f, 1f],
                        "photo" => Photo(x, y, u, v),
                        "noise" => Noise(x, y, l, uint.Parse(args[0], c)),
                        "solid" => [Arg(args, 0, c), Arg(args, 1, c), Arg(args, 2, c), Arg(args, 3, c)],
                        "sdf_glyph" => Sdf(u, v, 0f, 0f),
                        "msdf_glyph" => [Sdf(u, v, 0.012f, 0f)[0], Sdf(u, v, -0.012f, 0.012f)[0], Sdf(u, v, 0f, -0.012f)[0], 1f],
                        "tiles" => Tile(x, y, u, v, l),
                        _ => throw new FormatException($"unknown image generator '{name}'"),
                    };
                    int o = (((l * height) + y) * width + x) * 4;
                    px.CopyTo(texels, o);
                }
            }
        }
        return texels;
    }

    private static float Arg(string[] a, int i, CultureInfo c) => float.Parse(a[i], NumberStyles.Float, c);

    private static float[] Checker(int x, int y, int cell) =>
        (((x / cell) + (y / cell)) & 1) == 0 ? [0.85f, 0.85f, 0.9f, 1f] : [0.35f, 0.4f, 0.5f, 1f];

    // Smooth colour fields with a hard-edged disc, so filters and blurs have both to work on.
    private static float[] Photo(int x, int y, float u, float v)
    {
        float r = 0.5f + 0.5f * MathF.Sin(x * 0.21f + y * 0.07f);
        float g = 0.5f + 0.5f * MathF.Sin(y * 0.17f - x * 0.05f + 1f);
        float b = 0.5f + 0.5f * MathF.Sin((x + y) * 0.11f + 2f);
        float dx = u - 0.5f;
        float dy = v - 0.5f;
        if (dx * dx + dy * dy < 0.09f)
        {
            return [1f - r, 1f - g, 1f - b, 1f];
        }
        return [r, g, b, 1f];
    }

    private static float[] Noise(int x, int y, int layer, uint seed)
    {
        var rng = new DataGen.Rng(seed ^ (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(layer * 83492791));
        return [rng.NextFloat01(), rng.NextFloat01(), rng.NextFloat01(), 1f];
    }

    // Signed distance field of a ring with a bar through it, remapped so 0.5 is the edge and
    // larger is inside; the field spans +-0.125 of the texture.
    private static float[] Sdf(float u, float v, float shiftU, float shiftV)
    {
        float px = u - 0.5f - shiftU;
        float py = v - 0.5f - shiftV;
        float ring = MathF.Abs(MathF.Sqrt(px * px + py * py) - 0.27f) - 0.07f;
        float bx = MathF.Abs(px) - 0.32f;
        float by = MathF.Abs(py) - 0.04f;
        float bar = MathF.Sqrt(MathF.Max(bx, 0f) * MathF.Max(bx, 0f) + MathF.Max(by, 0f) * MathF.Max(by, 0f)) + MathF.Min(MathF.Max(bx, by), 0f);
        float d = MathF.Min(ring, bar);
        float s = Math.Clamp(0.5f - d * 4f, 0f, 1f);
        return [s, s, s, 1f];
    }

    private static readonly float[][] Palette =
    [
        [0.80f, 0.25f, 0.20f, 1f],
        [0.25f, 0.65f, 0.30f, 1f],
        [0.25f, 0.40f, 0.85f, 1f],
        [0.90f, 0.80f, 0.25f, 0.6f],
    ];

    private static float[] Tile(int x, int y, float u, float v, int layer)
    {
        float[] baseColor = Palette[layer % Palette.Length];
        bool edge = x % 16 == 0 || y % 16 == 0 || x % 16 == 15 || y % 16 == 15;
        bool stripe = ((x + y + layer * 5) / 4) % 4 == 0;
        float k = edge ? 0.55f : stripe ? 0.8f : 1f;
        return [baseColor[0] * k, baseColor[1] * k, baseColor[2] * k, baseColor[3]];
    }

    /// <summary>Bytes per texel for a format.</summary>
    public static int BytesPerTexel(PixelFormat f) => f switch
    {
        PixelFormat.R8 => 1,
        PixelFormat.Rgba8 => 4,
        PixelFormat.R32F => 4,
        _ => 16,
    };

    /// <summary>Converts RGBA floats to the format's byte layout (8-bit formats round to nearest).</summary>
    public static byte[] Encode(float[] rgba, PixelFormat format)
    {
        int texels = rgba.Length / 4;
        byte[] bytes = new byte[texels * BytesPerTexel(format)];
        for (int i = 0; i < texels; i++)
        {
            switch (format)
            {
                case PixelFormat.R8:
                    bytes[i] = ToByte(rgba[i * 4]);
                    break;
                case PixelFormat.Rgba8:
                    for (int k = 0; k < 4; k++)
                    {
                        bytes[(i * 4) + k] = ToByte(rgba[(i * 4) + k]);
                    }
                    break;
                case PixelFormat.R32F:
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), rgba[i * 4]);
                    break;
                default:
                    for (int k = 0; k < 4; k++)
                    {
                        BitConverter.TryWriteBytes(bytes.AsSpan((i * 16) + (k * 4)), rgba[(i * 4) + k]);
                    }
                    break;
            }
        }
        return bytes;
    }

    private static byte ToByte(float f) => (byte)MathF.Round(Math.Clamp(f, 0f, 1f) * 255f);
}
