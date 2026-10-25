using System;
using System.Collections.Generic;
using System.Globalization;

namespace OneShader.ParityTests;

/// <summary>
/// Generators for <see cref="DataGen"/> that produce structured scene data: record patterns, meshes,
/// camera matrices, frustum planes and joint chains. All values are computed in double precision and
/// rounded to float once, so every platform produces the same bytes.
///
/// Vertex layout shared by <c>sphere</c>, <c>cube</c> and <c>plane</c> (12 floats, stride 48):
/// position (3), normal (3), uv (2), tangent (4, w = bitangent sign).
///
/// Matrices are emitted in memory order for the HLSL layout asked for: <c>row</c> writes the math
/// matrix row by row (an HLSL <c>row_major</c> matrix), <c>col</c> column by column (the HLSL default).
/// Math matrices multiply column vectors (<c>clip = M * v</c>); depth range is 0..1 and clip-space y
/// points down, as in Vulkan.
/// </summary>
internal static class MeshGen
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    /// <summary>Appends the words of generator <paramref name="name"/>; returns false for an unknown name.</summary>
    public static bool TryGenerate(string name, string[] a, List<uint> words)
    {
        switch (name)
        {
            case "pattern":
                Pattern(a, words);
                return true;
            case "gridboxes":
                GridBoxes(a, words);
                return true;
            case "zeros":
                words.AddRange(new uint[Int(a, 0)]);
                return true;
            case "sphere":
                Sphere(Int(a, 0), Int(a, 1), a.Length > 2 ? Dbl(a, 2) : 1.0, words);
                return true;
            case "sphereindex":
                SphereIndex(Int(a, 0), Int(a, 1), a.Length > 2 ? Int(a, 2) : 0, words);
                return true;
            case "sphereskin":
                SphereSkin(Int(a, 0), Int(a, 1), Int(a, 2), words);
                return true;
            case "cube":
                Cube(Dbl(a, 0), words);
                return true;
            case "cubeindex":
                CubeIndex(a.Length > 0 && a[0].Length > 0 ? Int(a, 0) : 0, words);
                return true;
            case "plane":
                Plane(Dbl(a, 0), Dbl(a, 1), words);
                return true;
            case "planeindex":
                PlaneIndex(Int(a, 0), words);
                return true;
            case "view":
                Emit(LookAt(Dbl(a, 0), Dbl(a, 1), Dbl(a, 2), Dbl(a, 3), Dbl(a, 4), Dbl(a, 5)), a[6], words);
                return true;
            case "perspective":
                Emit(Perspective(Dbl(a, 0), Dbl(a, 1), Dbl(a, 2), Dbl(a, 3)), a[4], words);
                return true;
            case "invproj":
                Emit(Invert(Perspective(Dbl(a, 0), Dbl(a, 1), Dbl(a, 2), Dbl(a, 3))), a[4], words);
                return true;
            case "viewproj":
                Emit(Mul(Perspective(Dbl(a, 6), Dbl(a, 7), Dbl(a, 8), Dbl(a, 9)), LookAt(Dbl(a, 0), Dbl(a, 1), Dbl(a, 2), Dbl(a, 3), Dbl(a, 4), Dbl(a, 5))), a[10], words);
                return true;
            case "invviewproj":
                Emit(Invert(Mul(Perspective(Dbl(a, 6), Dbl(a, 7), Dbl(a, 8), Dbl(a, 9)), LookAt(Dbl(a, 0), Dbl(a, 1), Dbl(a, 2), Dbl(a, 3), Dbl(a, 4), Dbl(a, 5)))), a[10], words);
                return true;
            case "orthoviewproj":
                Emit(Mul(Ortho(Dbl(a, 6), Dbl(a, 7), Dbl(a, 8)), LookAt(Dbl(a, 0), Dbl(a, 1), Dbl(a, 2), Dbl(a, 3), Dbl(a, 4), Dbl(a, 5))), a[9], words);
                return true;
            case "world":
                Emit(World(Dbl(a, 0), Dbl(a, 1), Dbl(a, 2), Dbl(a, 3), Dbl(a, 4)), a[5], words);
                return true;
            case "normalmat":
                NormalMat(Dbl(a, 0), Dbl(a, 1), words);
                return true;
            case "jointchain":
                JointChain(Int(a, 0), Dbl(a, 1), Dbl(a, 2), Dbl(a, 3), a[4], words);
                return true;
            case "frustumplanes":
                FrustumPlanes(a, words);
                return true;
            case "hemisphere":
                Hemisphere(uint.Parse(a[0], C), Int(a, 1), words);
                return true;
            default:
                return false;
        }
    }

    private static int Int(string[] a, int i) => int.Parse(a[i], C);

    private static double Dbl(string[] a, int i) => double.Parse(a[i], NumberStyles.Float, C);

    private static void Add(List<uint> w, double v) => w.Add(BitConverter.SingleToUInt32Bits((float)v));

    // --- record patterns -------------------------------------------------------------------

    // pattern(seed,count,field,...): count records; each field is lo~hi (float in [lo,hi)), uN (uint in [0,N))
    // or sN (the record index times N, a running offset).
    private static void Pattern(string[] a, List<uint> words)
    {
        var rng = new DataGen.Rng(uint.Parse(a[0], C));
        int count = Int(a, 1);
        for (int r = 0; r < count; r++)
        {
            for (int f = 2; f < a.Length; f++)
            {
                string spec = a[f];
                if (spec[0] == 'u')
                {
                    words.Add(rng.Next() % uint.Parse(spec[1..], C));
                }
                else if (spec[0] == 's')
                {
                    words.Add((uint)r * uint.Parse(spec[1..], C));
                }
                else
                {
                    int tilde = spec.IndexOf('~', 1);
                    if (tilde < 0)
                    {
                        throw new FormatException($"pattern field '{spec}' must be lo~hi, uN or sN");
                    }
                    double lo = double.Parse(spec[..tilde], NumberStyles.Float, C);
                    double hi = double.Parse(spec[(tilde + 1)..], NumberStyles.Float, C);
                    Add(words, lo + ((hi - lo) * rng.NextFloat01()));
                }
            }
        }
    }

    // gridboxes(nx,ny,nz,cell,ox,oy,oz): axis-aligned boxes of a regular grid, x fastest, as (min xyz, 0) (max xyz, 0).
    private static void GridBoxes(string[] a, List<uint> words)
    {
        int nx = Int(a, 0);
        int ny = Int(a, 1);
        int nz = Int(a, 2);
        double cell = Dbl(a, 3);
        double ox = Dbl(a, 4);
        double oy = Dbl(a, 5);
        double oz = Dbl(a, 6);
        for (int z = 0; z < nz; z++)
        {
            for (int y = 0; y < ny; y++)
            {
                for (int x = 0; x < nx; x++)
                {
                    foreach (var (v, o) in new[] { (x, ox), (y, oy), (z, oz) })
                    {
                        Add(words, o + (v * cell));
                    }
                    Add(words, 0);
                    foreach (var (v, o) in new[] { (x, ox), (y, oy), (z, oz) })
                    {
                        Add(words, o + ((v + 1) * cell));
                    }
                    Add(words, 0);
                }
            }
        }
    }

    // --- meshes ------------------------------------------------------------------------------

    private static void Vertex(List<uint> w, double px, double py, double pz, double nx, double ny, double nz, double u, double v, double tx, double ty, double tz, double tw)
    {
        foreach (double d in new[] { px, py, pz, nx, ny, nz, u, v, tx, ty, tz, tw })
        {
            Add(w, d);
        }
    }

    private static void Sphere(int segments, int rings, double radius, List<uint> w)
    {
        for (int r = 0; r <= rings; r++)
        {
            double theta = Math.PI * r / rings;
            for (int s = 0; s <= segments; s++)
            {
                double phi = 2 * Math.PI * s / segments;
                double nx = Math.Sin(theta) * Math.Cos(phi);
                double ny = Math.Cos(theta);
                double nz = Math.Sin(theta) * Math.Sin(phi);
                Vertex(w, nx * radius, ny * radius, nz * radius, nx, ny, nz, (double)s / segments, (double)r / rings, -Math.Sin(phi), 0, Math.Cos(phi), 1);
            }
        }
    }

    private static void SphereIndex(int segments, int rings, int baseVertex, List<uint> w)
    {
        for (int r = 0; r < rings; r++)
        {
            for (int s = 0; s < segments; s++)
            {
                uint a = (uint)(baseVertex + (r * (segments + 1)) + s);
                uint b = a + (uint)segments + 1;
                w.AddRange([a, a + 1, b, a + 1, b + 1, b]);
            }
        }
    }

    // Joint indices (4 uints) and weights (4 floats) per sphere vertex: two joints blended by height.
    private static void SphereSkin(int segments, int rings, int joints, List<uint> w)
    {
        for (int r = 0; r <= rings; r++)
        {
            double t = (Math.Cos(Math.PI * r / rings) * 0.5 + 0.5) * (joints - 1);
            int j = Math.Min((int)Math.Floor(t), joints - 2);
            double f = t - j;
            for (int s = 0; s <= segments; s++)
            {
                w.AddRange([(uint)j, (uint)j + 1, 0u, 0u]);
                Add(w, 1 - f);
                Add(w, f);
                Add(w, 0);
                Add(w, 0);
            }
        }
    }

    private static void Cube(double half, List<uint> w)
    {
        (double[] N, double[] T)[] faces =
        [
            ([1, 0, 0], [0, 0, -1]),
            ([-1, 0, 0], [0, 0, 1]),
            ([0, 1, 0], [1, 0, 0]),
            ([0, -1, 0], [1, 0, 0]),
            ([0, 0, 1], [1, 0, 0]),
            ([0, 0, -1], [-1, 0, 0]),
        ];
        foreach (var (n, t) in faces)
        {
            double[] b = Cross(n, t);
            foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
            {
                Vertex(w,
                    half * (n[0] + (sx * t[0]) + (sy * b[0])), half * (n[1] + (sx * t[1]) + (sy * b[1])), half * (n[2] + (sx * t[2]) + (sy * b[2])),
                    n[0], n[1], n[2], (sx * 0.5) + 0.5, (sy * 0.5) + 0.5, t[0], t[1], t[2], 1);
            }
        }
    }

    private static void CubeIndex(int baseVertex, List<uint> w)
    {
        for (int f = 0; f < 6; f++)
        {
            uint o = (uint)(baseVertex + (f * 4));
            w.AddRange([o, o + 1, o + 2, o + 2, o + 1, o + 3]);
        }
    }

    private static void Plane(double half, double y, List<uint> w)
    {
        foreach (var (sx, sz) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
        {
            Vertex(w, sx * half, y, sz * half, 0, 1, 0, (sx * 0.5) + 0.5, (sz * 0.5) + 0.5, 1, 0, 0, 1);
        }
    }

    private static void PlaneIndex(int baseVertex, List<uint> w)
    {
        uint o = (uint)baseVertex;
        w.AddRange([o, o + 2, o + 1, o + 1, o + 2, o + 3]);
    }

    private static double[] Cross(double[] a, double[] b) =>
        [(a[1] * b[2]) - (a[2] * b[1]), (a[2] * b[0]) - (a[0] * b[2]), (a[0] * b[1]) - (a[1] * b[0])];

    // --- matrices ----------------------------------------------------------------------------

    private static double[,] Identity()
    {
        var m = new double[4, 4];
        for (int i = 0; i < 4; i++)
        {
            m[i, i] = 1;
        }
        return m;
    }

    private static double[,] Mul(double[,] a, double[,] b)
    {
        var m = new double[4, 4];
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                double s = 0;
                for (int k = 0; k < 4; k++)
                {
                    s += a[r, k] * b[k, c];
                }
                m[r, c] = s;
            }
        }
        return m;
    }

    private static double[,] LookAt(double ex, double ey, double ez, double tx, double ty, double tz)
    {
        double[] f = Normalize([tx - ex, ty - ey, tz - ez]);
        double[] up = Math.Abs(f[1]) > 0.999 ? [0, 0, 1] : [0, 1, 0];
        double[] s = Normalize(Cross(f, up));
        double[] u = Cross(s, f);
        var m = Identity();
        for (int i = 0; i < 3; i++)
        {
            m[0, i] = s[i];
            m[1, i] = u[i];
            m[2, i] = -f[i];
        }
        m[0, 3] = -((s[0] * ex) + (s[1] * ey) + (s[2] * ez));
        m[1, 3] = -((u[0] * ex) + (u[1] * ey) + (u[2] * ez));
        m[2, 3] = (f[0] * ex) + (f[1] * ey) + (f[2] * ez);
        return m;
    }

    private static double[] Normalize(double[] v)
    {
        double l = Math.Sqrt((v[0] * v[0]) + (v[1] * v[1]) + (v[2] * v[2]));
        return [v[0] / l, v[1] / l, v[2] / l];
    }

    private static double[,] Perspective(double fovDegrees, double aspect, double near, double far)
    {
        double f = 1.0 / Math.Tan(fovDegrees * Math.PI / 360.0);
        var m = new double[4, 4];
        m[0, 0] = f / aspect;
        m[1, 1] = -f; // Vulkan clip space has y pointing down
        m[2, 2] = far / (near - far);
        m[2, 3] = near * far / (near - far);
        m[3, 2] = -1;
        return m;
    }

    private static double[,] Ortho(double half, double near, double far)
    {
        var m = new double[4, 4];
        m[0, 0] = 1 / half;
        m[1, 1] = -1 / half;
        m[2, 2] = -1 / (far - near);
        m[2, 3] = -near / (far - near);
        m[3, 3] = 1;
        return m;
    }

    private static double[,] World(double tx, double ty, double tz, double scale, double rotYDegrees)
    {
        double c = Math.Cos(rotYDegrees * Math.PI / 180) * scale;
        double s = Math.Sin(rotYDegrees * Math.PI / 180) * scale;
        var m = Identity();
        m[0, 0] = c;
        m[0, 2] = s;
        m[1, 1] = scale;
        m[2, 0] = -s;
        m[2, 2] = c;
        m[0, 3] = tx;
        m[1, 3] = ty;
        m[2, 3] = tz;
        return m;
    }

    private static double[,] Invert(double[,] m)
    {
        var a = new double[4, 8];
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                a[r, c] = m[r, c];
            }
            a[r, 4 + r] = 1;
        }
        for (int col = 0; col < 4; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < 4; r++)
            {
                if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col]))
                {
                    pivot = r;
                }
            }
            if (Math.Abs(a[pivot, col]) < 1e-300)
            {
                throw new FormatException("matrix is singular");
            }
            for (int c = 0; c < 8; c++)
            {
                (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
            }
            double d = a[col, col];
            for (int c = 0; c < 8; c++)
            {
                a[col, c] /= d;
            }
            for (int r = 0; r < 4; r++)
            {
                if (r != col)
                {
                    double f = a[r, col];
                    for (int c = 0; c < 8; c++)
                    {
                        a[r, c] -= f * a[col, c];
                    }
                }
            }
        }
        var inv = new double[4, 4];
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                inv[r, c] = a[r, 4 + c];
            }
        }
        return inv;
    }

    private static void Emit(double[,] m, string layout, List<uint> w)
    {
        bool rowMajor = layout switch
        {
            "row" => true,
            "col" => false,
            _ => throw new FormatException($"matrix layout must be 'row' or 'col', not '{layout}'"),
        };
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                Add(w, rowMajor ? m[i, j] : m[j, i]);
            }
        }
    }

    // Inverse transpose of a uniform-scale Y rotation, as the rows of a row_major float3x3 with each row padded to 16 bytes.
    private static void NormalMat(double rotYDegrees, double scale, List<uint> w)
    {
        double c = Math.Cos(rotYDegrees * Math.PI / 180) / scale;
        double s = Math.Sin(rotYDegrees * Math.PI / 180) / scale;
        double[][] rows = [[c, 0, s], [0, 1 / scale, 0], [-s, 0, c]];
        foreach (double[] row in rows)
        {
            Add(w, row[0]);
            Add(w, row[1]);
            Add(w, row[2]);
            Add(w, 0);
        }
    }

    // Joint j rotates about the z axis through (0, y_j, 0) by j * angle; y_j runs from bottomY to topY.
    private static void JointChain(int count, double angleDegrees, double bottomY, double topY, string layout, List<uint> w)
    {
        for (int j = 0; j < count; j++)
        {
            double y = bottomY + ((topY - bottomY) * j / (count - 1));
            double angle = angleDegrees * j * Math.PI / 180;
            var m = Identity();
            m[0, 0] = Math.Cos(angle);
            m[0, 1] = -Math.Sin(angle);
            m[1, 0] = Math.Sin(angle);
            m[1, 1] = Math.Cos(angle);
            m[1, 3] = y - (m[1, 1] * y);
            m[0, 3] = -(m[0, 1] * y);
            Emit(m, layout, w);
        }
    }

    // Six inside-facing planes (left, right, bottom, top, near, far) with unit normals.
    private static void FrustumPlanes(string[] a, List<uint> w)
    {
        var vp = Mul(Perspective(Dbl(a, 6), Dbl(a, 7), Dbl(a, 8), Dbl(a, 9)), LookAt(Dbl(a, 0), Dbl(a, 1), Dbl(a, 2), Dbl(a, 3), Dbl(a, 4), Dbl(a, 5)));
        double[][] planes =
        [
            Combine(vp, 3, 0, 1),
            Combine(vp, 3, 0, -1),
            Combine(vp, 3, 1, 1),
            Combine(vp, 3, 1, -1),
            Combine(vp, 2, 0, 0),
            Combine(vp, 3, 2, -1),
        ];
        foreach (double[] p in planes)
        {
            double len = Math.Sqrt((p[0] * p[0]) + (p[1] * p[1]) + (p[2] * p[2]));
            foreach (double v in p)
            {
                Add(w, v / len);
            }
        }
    }

    private static double[] Combine(double[,] m, int rowA, int rowB, int sign)
    {
        var p = new double[4];
        for (int c = 0; c < 4; c++)
        {
            p[c] = m[rowA, c] + (sign * m[rowB, c]);
        }
        return p;
    }

    // Kernel for ambient occlusion: unit-hemisphere directions (z >= 0), longer ones more common nearer the end.
    private static void Hemisphere(uint seed, int count, List<uint> w)
    {
        var rng = new DataGen.Rng(seed);
        for (int i = 0; i < count; i++)
        {
            double x = (rng.NextFloat01() * 2.0) - 1.0;
            double y = (rng.NextFloat01() * 2.0) - 1.0;
            double z = rng.NextFloat01() + 0.05;
            double len = Math.Sqrt((x * x) + (y * y) + (z * z));
            double t = (double)i / count;
            double scale = 0.1 + (0.9 * t * t);
            Add(w, x / len * scale);
            Add(w, y / len * scale);
            Add(w, z / len * scale);
            Add(w, 0);
        }
    }
}
