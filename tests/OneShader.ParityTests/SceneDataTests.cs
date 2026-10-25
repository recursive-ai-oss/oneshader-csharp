using System;
using System.Buffers.Binary;
using System.Linq;
using Xunit;

namespace OneShader.ParityTests;

/// <summary>The scene-data generators and the case directives of the 3D cases; no GPU or tools needed.</summary>
public class SceneDataTests
{
    private static float[] Floats(string expression)
    {
        byte[] b = DataGen.Materialize(expression);
        float[] f = new float[b.Length / 4];
        Buffer.BlockCopy(b, 0, f, 0, b.Length);
        return f;
    }

    [Fact]
    public void MeshesHaveTheDeclaredShape()
    {
        // 12 floats per vertex; (segments + 1) * (rings + 1) sphere vertices; 6 indices per quad.
        Assert.Equal(33 * 25 * 12, Floats("@sphere(32,24)").Length);
        uint[] indices = DataGen.Materialize("@sphereindex(32,24)").Chunk(4).Select(c => BitConverter.ToUInt32(c)).ToArray();
        Assert.Equal(32 * 24 * 6, indices.Length);
        Assert.True(indices.Max() < 33 * 25);
        Assert.Equal(24 * 12, Floats("@cube(0.5)").Length);
        Assert.Equal(36, DataGen.Materialize("@cubeindex()").Length / 4);
        // Sphere normals are unit length and point away from the centre.
        float[] v = Floats("@sphere(8,6,2.0)");
        for (int i = 0; i < v.Length; i += 12)
        {
            Assert.Equal(1.0, Math.Sqrt((v[i + 3] * v[i + 3]) + (v[i + 4] * v[i + 4]) + (v[i + 5] * v[i + 5])), 4);
            Assert.Equal(v[i + 3] * 2f, v[i], 4);
        }
    }

    [Fact]
    public void MatricesFollowTheRequestedLayoutAndInvert()
    {
        float[] row = Floats("@viewproj(0,0.6,3.2,0,0,0,60,1,0.1,50,row)");
        float[] col = Floats("@viewproj(0,0.6,3.2,0,0,0,60,1,0.1,50,col)");
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                Assert.Equal(row[(r * 4) + c], col[(c * 4) + r]);
            }
        }
        // The origin lies on the view axis, in front of the camera: it projects to the screen centre at a depth in (0, 1).
        float[] clip = [row[3], row[7], row[11], row[15]];
        Assert.Equal(0f, clip[0] / clip[3], 5);
        Assert.Equal(0f, clip[1] / clip[3], 5);
        Assert.InRange(clip[2] / clip[3], 0f, 1f);

        // inverse(view-projection) times view-projection is the identity.
        float[] inv = Floats("@invviewproj(0,0.6,3.2,0,0,0,60,1,0.1,50,row)");
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                float sum = 0;
                for (int k = 0; k < 4; k++)
                {
                    sum += inv[(r * 4) + k] * row[(k * 4) + c];
                }
                Assert.Equal(r == c ? 1f : 0f, sum, 3);
            }
        }
    }

    [Fact]
    public void FrustumPlanesHaveUnitNormalsAndKeepTheTargetInside()
    {
        float[] p = Floats("@frustumplanes(0,2,8,0,0,-10,50,1.6,0.5,60)");
        Assert.Equal(24, p.Length);
        for (int i = 0; i < 6; i++)
        {
            double len = Math.Sqrt((p[i * 4] * p[i * 4]) + (p[(i * 4) + 1] * p[(i * 4) + 1]) + (p[(i * 4) + 2] * p[(i * 4) + 2]));
            Assert.Equal(1.0, len, 4);
            // The look-at target (0, 0, -10) is inside every plane.
            Assert.True((p[(i * 4) + 2] * -10f) + p[(i * 4) + 3] > 0f, $"plane {i}");
        }
    }

    [Fact]
    public void PatternsAreDeterministicAndRespectFieldKinds()
    {
        byte[] a = DataGen.Materialize("@pattern(3,10,1.0~2.0,u5,s8)");
        Assert.Equal(a, DataGen.Materialize("@pattern(3,10,1.0~2.0,u5,s8)"));
        Assert.Equal(10 * 3 * 4, a.Length);
        for (int r = 0; r < 10; r++)
        {
            Assert.InRange(BitConverter.ToSingle(a, r * 12), 1.0f, 2.0f);
            Assert.True(BinaryPrimitives.ReadUInt32LittleEndian(a.AsSpan((r * 12) + 4)) < 5);
            Assert.Equal((uint)(r * 8), BinaryPrimitives.ReadUInt32LittleEndian(a.AsSpan((r * 12) + 8)));
        }
        Assert.Throws<FormatException>(() => DataGen.Materialize("@pattern(1,1,oops)"));
        Assert.All(DataGen.Materialize("@zeros(5)"), x => Assert.Equal(0, x));
    }

    [Fact]
    public void MipChainsAreStoredLevelAfterLevelAtTheirOwnSize()
    {
        var model = CaseFile.Parse("case x\nshader a.hlsl\ndispatch CSMain 1 1 1\ntexture 0 0 64x64x6 rgba16f sky(-1) mips=5 cube\n", "mip.case");
        var r = model.Resources[0];
        Assert.True(r.Cube);
        Assert.Equal(5, r.MipLevels);
        byte[] bytes = CaseData.Build(model).Images[0];
        int texels = (64 * 64) + (32 * 32) + (16 * 16) + (8 * 8) + (4 * 4);
        Assert.Equal(texels * 6 * 8, bytes.Length);
    }

    [Fact]
    public void NewDirectivesParse()
    {
        var model = CaseFile.Parse(
            "case x\nshader a.hlsl\ngraphics vs=VSMain\ntarget 8 8 format=rgba16f\ndepth clear=0.5 out\ncull back\nftolerance 0.01\n"
            + "draw vertices=3\nstorage 0 1 out sorted size=64\nsampler 0 2 linear clamp mips compare\n", "new.case");
        var g = model.Graphics!;
        Assert.Equal(PixelFormat.Rgba16F, g.TargetFormat);
        Assert.Equal(new DepthDecl(0.5f, true, true), g.Depth);
        Assert.Equal(CullMode.Back, g.Cull);
        Assert.Equal(0.01, model.FloatTolerance);
        Assert.Equal(string.Empty, g.PixelEntry);
        Assert.True(model.Resources[0].Sorted);
        Assert.True(model.Resources[1].MipFilter);
        Assert.True(model.Resources[1].Compare);

        var e = Assert.Throws<CaseFormatException>(() => CaseFile.Parse("case x\nshader a.hlsl\ngraphics vs=V\ntarget 8 8 format=hdr\n", "bad.case"));
        Assert.Equal("bad.case(4): unknown target format 'hdr' (use rgba8, rgba16f, rgba32f or r32f)", e.Message);
        e = Assert.Throws<CaseFormatException>(() => CaseFile.Parse("case x\nshader a.hlsl\ngraphics vs=V\ntarget 8 8\ndraw vertices=3\n", "bad.case"));
        Assert.Contains("or a 'depth' line for depth-only draws", e.Message, StringComparison.Ordinal);
    }

    private static OutputBlob Rgba32(params float[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return new OutputBlob("target", bytes, values.Length / 4, 1, PixelFormat.Rgba32F, true);
    }

    [Fact]
    public void FloatImageComparisonHonoursItsTolerance()
    {
        var model = new CaseModel { Name = "synthetic", Shader = "a.hlsl" };
        var reference = Rgba32(1f, 2f, 3f, 4f);

        var report = new ParityReport();
        Parity.CompareFloatImages(report, model, reference, Rgba32(1f, 2f, 3f, 4f), "a", "b", 0);
        Assert.Empty(report.Failures);

        // A difference of 0.03 on a value of 3 is 0.0075 relative to (1 + 3): inside 0.01, outside 0.001.
        report = new ParityReport();
        Parity.CompareFloatImages(report, model, reference, Rgba32(1f, 2f, 3.03f, 4f), "a", "b", 0.01);
        Assert.Empty(report.Failures);
        Assert.Single(report.Notes);
        report = new ParityReport();
        Parity.CompareFloatImages(report, model, reference, Rgba32(1f, 2f, 3.03f, 4f), "a", "b", 0.001);
        Assert.Single(report.Failures);
        Assert.Contains("differ in 1 values", report.Failures[0], StringComparison.Ordinal);

        // Two NaNs agree; a NaN against a number does not, whatever the tolerance.
        report = new ParityReport();
        Parity.CompareFloatImages(report, model, Rgba32(float.NaN, 0f, 0f, 0f), Rgba32(float.NaN, 0f, 0f, 0f), "a", "b", 0);
        Assert.Empty(report.Failures);
        report = new ParityReport();
        Parity.CompareFloatImages(report, model, Rgba32(float.NaN, 0f, 0f, 0f), Rgba32(1f, 0f, 0f, 0f), "a", "b", 1000);
        Assert.Single(report.Failures);
    }

    [Fact]
    public void SortedOutputIsASortedWordList()
    {
        byte[] words = DataGen.Materialize("9u 3u 7u 0u 3u");
        byte[] sorted = VkRunner.SortWords(words);
        Assert.Equal(DataGen.Materialize("0u 3u 3u 7u 9u"), sorted);
    }
}
