using System;

namespace OneShader.ParityTests;

/// <summary>
/// Procedural material, environment and scene images for the 3D corpus cases. Everything is a pure
/// function of the texel position, so no image data is stored. Results are RGBA floats.
/// </summary>
internal static class SceneGen
{
    private static float Smoothstep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - (2f * t));
    }

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);

    // Brick-like base colour: coloured cells, dark grout, thin low-alpha stripes for alpha testing.
    public static float[] Albedo(float u, float v)
    {
        float bx = u * 6f;
        float by = v * 12f;
        int row = (int)MathF.Floor(by);
        if ((row & 1) == 1)
        {
            bx += 0.5f;
        }
        int col = (int)MathF.Floor(bx);
        float fx = bx - col;
        float fy = by - row;
        bool grout = fx < 0.06f || fy < 0.1f;
        uint h = (uint)((col * 73856093) ^ (row * 19349663));
        float tint = 0.75f + (0.25f * ((h >> 8) % 100 / 100f));
        float[] c = grout ? [0.45f, 0.43f, 0.4f] : [0.62f * tint, 0.28f * tint, 0.2f * tint];
        float alpha = MathF.Floor(u * 16f) % 4f == 3f && fy < 0.5f ? 0.3f : 1f;
        return [c[0], c[1], c[2], alpha];
    }

    // Tangent-space normals of a smooth bump field h = sin(8 pi u) * sin(8 pi v) / 16, encoded to [0,1].
    public static float[] NormalMap(float u, float v)
    {
        const float k = 8f * MathF.PI;
        float dhdu = k * MathF.Cos(k * u) * MathF.Sin(k * v) / 16f;
        float dhdv = k * MathF.Sin(k * u) * MathF.Cos(k * v) / 16f;
        float x = -dhdu;
        float y = -dhdv;
        float inv = 1f / MathF.Sqrt((x * x) + (y * y) + 1f);
        return [((x * inv) * 0.5f) + 0.5f, ((y * inv) * 0.5f) + 0.5f, (inv * 0.5f) + 0.5f, 1f];
    }

    // glTF packing: red occlusion, green roughness, blue metallic.
    public static float[] Orm(float u, float v)
    {
        float occlusion = 0.55f + (0.45f * Smoothstep(0f, 1f, 0.5f + (0.5f * MathF.Sin(u * 20f) * MathF.Cos(v * 14f))));
        float roughness = Lerp(0.2f, 0.9f, u);
        float metallic = MathF.Floor(v * 4f) % 2f == 0f ? 1f : 0f;
        return [occlusion, roughness, metallic, 1f];
    }

    // Dim background with bright discs and single hot pixels, so thresholding and averaging both matter.
    public static float[] HdrBlobs(int x, int y, float u, float v)
    {
        float[] cx = [0.25f, 0.7f, 0.5f, 0.82f];
        float[] cy = [0.3f, 0.35f, 0.75f, 0.8f];
        float[] radius = [0.06f, 0.04f, 0.09f, 0.03f];
        float[] power = [12f, 30f, 5f, 80f];
        float[][] tint = [[1f, 0.8f, 0.5f], [0.6f, 0.8f, 1f], [1f, 1f, 1f], [1f, 0.4f, 0.4f]];
        float[] c = [0.02f + (0.03f * u), 0.025f + (0.03f * v), 0.04f];
        for (int i = 0; i < 4; i++)
        {
            float d = MathF.Sqrt(((u - cx[i]) * (u - cx[i])) + ((v - cy[i]) * (v - cy[i])));
            float k = 1f - Smoothstep(radius[i] * 0.6f, radius[i], d);
            for (int ch = 0; ch < 3; ch++)
            {
                c[ch] += tint[i][ch] * power[i] * k;
            }
        }
        if (((x * 7) + (y * 13)) % 97 == 11 && (x + y) % 5 == 0)
        {
            c[0] += 200f;
            c[1] += 200f;
            c[2] += 200f;
        }
        return [c[0], c[1], c[2], 1f];
    }

    // Direction of a cube-map texel, following the face orientation in the Vulkan specification
    // (cube map face selection: +X, -X, +Y, -Y, +Z, -Z with their sc/tc axes).
    private static float[] CubeDirection(int face, float s, float t)
    {
        float sc = (2f * s) - 1f;
        float tc = (2f * t) - 1f;
        float[] d = face switch
        {
            0 => [1f, -tc, -sc],
            1 => [-1f, -tc, sc],
            2 => [sc, 1f, tc],
            3 => [sc, -1f, -tc],
            4 => [sc, -tc, 1f],
            _ => [-sc, -tc, -1f],
        };
        float inv = 1f / MathF.Sqrt((d[0] * d[0]) + (d[1] * d[1]) + (d[2] * d[2]));
        return [d[0] * inv, d[1] * inv, d[2] * inv];
    }

    // Sky with a sun. blur 0 is sharp, 1 is a smooth irradiance-like lobe; prefiltered mip levels use their own blur.
    public static float[] Sky(int face, float s, float t, float blur)
    {
        float[] d = CubeDirection(face, s, t);
        float horizon = 0.03f + (0.6f * blur);
        float w = Smoothstep(-horizon, horizon, d[1]);
        float up = Math.Clamp(d[1], 0f, 1f);
        float[] sky = [Lerp(0.55f, 0.15f, up), Lerp(0.7f, 0.3f, up), Lerp(0.95f, 0.7f, up)];
        float[] ground = [0.12f, 0.1f, 0.09f];
        float[] sun = [0.5f / 0.911f, 0.7f / 0.911f, 0.3f / 0.911f];
        float cosSun = Math.Max((d[0] * sun[0]) + (d[1] * sun[1]) + (d[2] * sun[2]), 0f);
        float exponent = Lerp(600f, 3f, blur);
        float glow = 30f * MathF.Pow(cosSun, exponent) * Lerp(1f, 0.08f, blur);
        return
        [
            Lerp(ground[0], sky[0], w) + (glow * 1.0f),
            Lerp(ground[1], sky[1], w) + (glow * 0.9f),
            Lerp(ground[2], sky[2], w) + (glow * 0.7f),
            1f,
        ];
    }

    // Scale and bias for F0 as a function of N.V (u) and roughness (v): an analytic fit, not an integral.
    public static float[] BrdfLut(float u, float v)
    {
        float nov = u;
        float rough = v;
        float r0 = (rough * -1f) + 1f;
        float r1 = (rough * -0.0275f) + 0.0425f;
        float r2 = (rough * -0.572f) + 1.04f;
        float r3 = (rough * 0.022f) - 0.04f;
        float a004 = (Math.Min(r0 * r0, MathF.Pow(2f, -9.28f * nov)) * r0) + r1;
        return [(-1.04f * a004) + r2, (1.04f * a004) + r3, 0f, 1f];
    }

    // Depth of a cascade: a flat floor with a dome standing on it; farther cascades see a smaller dome.
    public static float[] ShadowMap(int cascade, float u, float v)
    {
        float radius = 0.4f / (1f + cascade);
        float dx = u - 0.5f;
        float dy = v - 0.5f;
        float r2 = ((dx * dx) + (dy * dy)) / (radius * radius);
        float depth = r2 < 1f ? 0.55f - (0.3f * MathF.Sqrt(1f - r2)) : 0.55f;
        return [depth, 0f, 0f, 1f];
    }

    private const float SphereRadius = 1.1f;
    private const float SmallRadius = 0.5f;
    private static readonly float[] SphereCenter = [0f, -0.1f, -3.5f];
    private static readonly float[] SmallCenter = [1.3f, -0.5f, -2.6f];
    private const float FloorY = -1f;

    // Nearest hit along a view ray from the origin; returns t (= linear depth because d.z = -1) and the normal, or t = 0 for a miss.
    private static (float T, float[] N) Trace(float u, float v, float fovDegrees, float aspect)
    {
        float tanY = MathF.Tan(fovDegrees * MathF.PI / 360f);
        float ndcX = (u * 2f) - 1f;
        float ndcY = (v * 2f) - 1f;
        float[] d = [ndcX * tanY * aspect, -ndcY * tanY, -1f];
        float best = float.MaxValue;
        float[] normal = [0f, 0f, 1f];
        if (d[1] < 0f)
        {
            float t = FloorY / d[1];
            if (t > 0f)
            {
                best = t;
                normal = [0f, 1f, 0f];
            }
        }
        foreach (var (center, radius) in new[] { (SphereCenter, SphereRadius), (SmallCenter, SmallRadius) })
        {
            float a = (d[0] * d[0]) + (d[1] * d[1]) + (d[2] * d[2]);
            float b = -2f * ((d[0] * center[0]) + (d[1] * center[1]) + (d[2] * center[2]));
            float c = (center[0] * center[0]) + (center[1] * center[1]) + (center[2] * center[2]) - (radius * radius);
            float disc = (b * b) - (4f * a * c);
            if (disc < 0f)
            {
                continue;
            }
            float t = (-b - MathF.Sqrt(disc)) / (2f * a);
            if (t > 0f && t < best)
            {
                best = t;
                normal = [((d[0] * t) - center[0]) / radius, ((d[1] * t) - center[1]) / radius, ((d[2] * t) - center[2]) / radius];
            }
        }
        return best == float.MaxValue ? (0f, [0f, 0f, 1f]) : (best, normal);
    }

    public static float[] Depth(float u, float v, float fovDegrees, float aspect) => [Trace(u, v, fovDegrees, aspect).T, 0f, 0f, 1f];

    public static float[] Normal(float u, float v, float fovDegrees, float aspect)
    {
        var (t, n) = Trace(u, v, fovDegrees, aspect);
        return [n[0], n[1], n[2], t > 0f ? 1f : 0f];
    }
}
