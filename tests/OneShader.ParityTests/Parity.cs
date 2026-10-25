using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace OneShader.ParityTests;

/// <summary>Outcome of a comparison: empty <see cref="Failures"/> means parity.</summary>
public sealed class ParityReport
{
    public List<string> Failures { get; } = new();

    /// <summary>Observations worth keeping even when the comparison passes, such as the largest float error seen.</summary>
    public List<string> Notes { get; } = new();

    public override string ToString() => string.Join(Environment.NewLine, Failures);
}

/// <summary>Compiles, runs and compares corpus cases.</summary>
public static class Parity
{
    /// <summary>Compiles the case's entry points with <paramref name="compiler"/> and runs it on the GPU.</summary>
    public static RunResult Run(VkContext ctx, CaseModel model, CaseData data, ParityCompiler compiler)
    {
        string source = Path.Combine(Tools.CorpusDir, model.Shader);
        return VkRunner.Run(ctx, model, data, (entry, stage) =>
            compiler.Compile(new ShaderRequest(source, entry, stage, model.Defines)));
    }

    /// <summary>Runs the case with both compilers and compares: buffers exactly, images within the case tolerance.</summary>
    public static ParityReport Compare(VkContext ctx, CaseModel model, ParityCompiler reference, ParityCompiler candidate)
    {
        var data = CaseData.Build(model);
        var report = new ParityReport();
        var a = Run(ctx, model, data, reference);
        var b = Run(ctx, model, data, candidate);
        AddValidation(report, reference.Name, a);
        AddValidation(report, candidate.Name, b);
        if (a.Outputs.Count != b.Outputs.Count)
        {
            report.Failures.Add($"{model.Name}: output count differs ({a.Outputs.Count} vs {b.Outputs.Count})");
            return report;
        }
        int tolerance = model.Graphics?.Tolerance ?? 0;
        foreach (var x in a.Outputs)
        {
            var y = b.Get(x.Key);
            if (x.IsImage && IsFloatImage(x.Format))
            {
                CompareFloatImages(report, model, x, y, reference.Name, candidate.Name, model.FloatTolerance);
                continue;
            }
            CompareBlobs(report, model, x, y, reference.Name, candidate.Name, x.IsImage ? tolerance : 0);
        }
        if (Environment.GetEnvironmentVariable("ONESHADER_PARITY_DUMP") == "1")
        {
            foreach (var o in a.Outputs.Where(o => o.IsImage))
            {
                WriteImage(model, o, "dump", o.Bytes);
            }
        }
        return report;
    }

    /// <summary>Runs the case with one compiler and checks the outputs named by its <c>expect</c> lines against the CPU.</summary>
    public static ParityReport CheckExpected(VkContext ctx, CaseModel model, ParityCompiler compiler)
    {
        var data = CaseData.Build(model);
        var report = new ParityReport();
        var run = Run(ctx, model, data, compiler);
        AddValidation(report, compiler.Name, run);
        foreach (var expect in model.Expects)
        {
            foreach (var e in CpuReferences.Compute(expect.Name, model, data))
            {
                CompareExpected(report, model, e, run.Get(e.Key), compiler.Name);
            }
        }
        return report;
    }

    private static void AddValidation(ParityReport report, string compiler, RunResult run)
    {
        foreach (string m in run.ValidationMessages)
        {
            report.Failures.Add($"validation layer ({compiler}): {m}");
        }
    }

    private static void CompareBlobs(ParityReport report, CaseModel model, OutputBlob a, OutputBlob b, string nameA, string nameB, int tolerance)
    {
        int maxDiff = 0;
        int differing = 0;
        int first = -1;
        for (int i = 0; i < a.Bytes.Length; i++)
        {
            int diff = Math.Abs(a.Bytes[i] - b.Bytes[i]);
            // Float images and buffers are compared bit for bit, so the tolerance only applies to 8-bit texels.
            bool bytewise = a.IsImage && a.Format is PixelFormat.R8 or PixelFormat.Rgba8;
            if (diff > (bytewise ? tolerance : 0))
            {
                differing++;
                if (first < 0)
                {
                    first = i;
                }
            }
            maxDiff = Math.Max(maxDiff, diff);
        }
        if (differing == 0)
        {
            return;
        }
        string where = a.IsImage
            ? $"first at pixel ({first / ImageGen.BytesPerTexel(a.Format) % a.Width},{first / ImageGen.BytesPerTexel(a.Format) / a.Width})"
            : $"first at byte offset {first}";
        report.Failures.Add($"{model.Name} {a.Key}: {nameA} and {nameB} differ in {differing} bytes (max difference {maxDiff}, tolerance {tolerance}), {where}");
        if (a.IsImage)
        {
            WriteImage(model, a, nameA, a.Bytes);
            WriteImage(model, b, nameB, b.Bytes);
            if (a.Format is PixelFormat.R8 or PixelFormat.Rgba8)
            {
                byte[] diff = new byte[a.Bytes.Length];
                for (int i = 0; i < diff.Length; i++)
                {
                    diff[i] = (byte)Math.Min(255, Math.Abs(a.Bytes[i] - b.Bytes[i]) * 16);
                }
                WriteImage(model, a, "diff", diff);
            }
        }
    }

    /// <summary>Writes every image output of a run as a PNG when <c>ONESHADER_PARITY_DUMP=1</c>.</summary>
    public static void DumpImages(CaseModel model, RunResult run, string label)
    {
        if (Environment.GetEnvironmentVariable("ONESHADER_PARITY_DUMP") == "1")
        {
            foreach (var o in run.Outputs.Where(o => o.IsImage))
            {
                WriteImage(model, o, label, o.Bytes);
            }
        }
    }

    // Float images: |a-b| <= tolerance * (1 + |a|), two NaNs count as equal; tolerance 0 means identical values.
    internal static void CompareFloatImages(ParityReport report, CaseModel model, OutputBlob a, OutputBlob b, string nameA, string nameB, double tolerance)
    {
        float[] fa = DecodeFloats(a.Format, a.Bytes);
        float[] fb = DecodeFloats(b.Format, b.Bytes);
        int channels = fa.Length / (a.Width * a.Height);
        int differing = 0;
        int first = -1;
        double worst = 0;
        double maxSeen = 0;
        for (int i = 0; i < fa.Length; i++)
        {
            if (float.IsNaN(fa[i]) && float.IsNaN(fb[i]))
            {
                continue;
            }
            double err = Math.Abs((double)fa[i] - fb[i]) / (1.0 + Math.Abs(fa[i]));
            maxSeen = double.IsNaN(err) ? double.PositiveInfinity : Math.Max(maxSeen, err);
            if (!(err <= tolerance))
            {
                differing++;
                first = first < 0 ? i : first;
                worst = double.IsNaN(err) ? double.PositiveInfinity : Math.Max(worst, err);
            }
        }
        if (differing == 0)
        {
            report.Notes.Add($"{a.Key} largest relative error {maxSeen:G3}");
            return;
        }
        int texel = first / channels;
        report.Failures.Add($"{model.Name} {a.Key}: {nameA} and {nameB} differ in {differing} values (largest relative error {worst:G3}, tolerance {tolerance:G3}), first at pixel ({texel % a.Width},{texel / a.Width}): {fa[first]:R} vs {fb[first]:R}");
        WriteImage(model, a, nameA, a.Bytes);
        WriteImage(model, b, nameB, b.Bytes);
    }

    private static void CompareExpected(ParityReport report, CaseModel model, Expectation e, OutputBlob actual, string compiler)
    {
        int bad = 0;
        string detail = "";
        switch (e.Kind)
        {
            case ExpectKind.FloatRelative:
                for (int i = 0; i + 4 <= e.Bytes.Length; i += 4)
                {
                    float want = BitConverter.ToSingle(e.Bytes, i);
                    float got = BitConverter.ToSingle(actual.Bytes, i);
                    if (!(Math.Abs(got - want) <= e.Tolerance * (1 + Math.Abs(want))))
                    {
                        if (bad++ == 0)
                        {
                            detail = $"first at float {i / 4}: expected {want:R}, got {got:R}";
                        }
                    }
                }
                break;
            default:
                int slack = e.Kind == ExpectKind.ByteAbsolute ? (int)e.Tolerance : 0;
                for (int i = 0; i < e.Bytes.Length; i++)
                {
                    if (Math.Abs(e.Bytes[i] - actual.Bytes[i]) > slack)
                    {
                        if (bad++ == 0)
                        {
                            detail = $"first at byte {i}: expected {e.Bytes[i]}, got {actual.Bytes[i]}";
                        }
                    }
                }
                break;
        }
        if (bad > 0)
        {
            report.Failures.Add($"{model.Name} {e.Key}: {compiler} output differs from the CPU result in {bad} places, {detail}");
        }
    }

    /// <summary>Writes an image as a PNG (alpha dropped; float values are tone mapped) under the artifacts directory.</summary>
    private static void WriteImage(CaseModel model, OutputBlob image, string label, byte[] bytes)
    {
        string dir = Path.Combine(Tools.ArtifactsDir, model.Name);
        Directory.CreateDirectory(dir);
        string safeLabel = new(label.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        string safeKey = new(image.Key.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        File.WriteAllBytes(Path.Combine(dir, $"{safeKey}-{safeLabel}.png"), PngWriter.Encode(image.Width, image.Height, ToRgb8(image, bytes)));
    }

    private static byte[] ToRgb8(OutputBlob image, byte[] bytes)
    {
        int pixels = image.Width * image.Height;
        byte[] rgb = new byte[pixels * 3];
        if (image.Format is PixelFormat.R8 or PixelFormat.Rgba8)
        {
            int bpp = ImageGen.BytesPerTexel(image.Format);
            for (int i = 0; i < pixels; i++)
            {
                for (int c = 0; c < 3; c++)
                {
                    rgb[(i * 3) + c] = bpp == 1 ? bytes[i] : bytes[(i * 4) + c];
                }
            }
            return rgb;
        }
        float[] f = DecodeFloats(image.Format, bytes);
        int channels = f.Length / pixels;
        bool hdr = channels == 4;
        for (int i = 0; i < pixels; i++)
        {
            for (int c = 0; c < 3; c++)
            {
                float v = f[(i * channels) + (channels == 1 ? 0 : c)];
                v = hdr ? v / (1f + Math.Max(v, 0f)) : v;
                rgb[(i * 3) + c] = (byte)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);
            }
        }
        return rgb;
    }

    /// <summary>Decodes the float formats (R32F, depth, RGBA16F, RGBA32F) to floats, texel-major.</summary>
    internal static float[] DecodeFloats(PixelFormat format, byte[] bytes)
    {
        switch (format)
        {
            case PixelFormat.Rgba16F:
                {
                    float[] f = new float[bytes.Length / 2];
                    for (int i = 0; i < f.Length; i++)
                    {
                        f[i] = (float)BitConverter.ToHalf(bytes, i * 2);
                    }
                    return f;
                }
            default:
                {
                    float[] f = new float[bytes.Length / 4];
                    for (int i = 0; i < f.Length; i++)
                    {
                        f[i] = BitConverter.ToSingle(bytes, i * 4);
                    }
                    return f;
                }
        }
    }

    internal static bool IsFloatImage(PixelFormat f) => f is PixelFormat.R32F or PixelFormat.Rgba32F or PixelFormat.Rgba16F or PixelFormat.Depth32F;
}
