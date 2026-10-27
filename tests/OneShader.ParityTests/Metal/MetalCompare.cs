using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace OneShader.ParityTests;

/// <summary>One line of <c>corpus/metal-tolerances.txt</c>.</summary>
/// <param name="IsHalf">With <paramref name="IsFloat"/>: the buffer holds 16-bit floats.</param>
/// <param name="IsFloat">Compare a storage buffer as 32-bit floats within <paramref name="Tolerance"/> (relative) instead of exactly.</param>
/// <param name="Tolerance">8-bit images: largest allowed difference in steps; floats: <c>|a-b| / (1+|a|)</c>.</param>
/// <param name="MaxBad">How many values may exceed the tolerance (rasterization differences along triangle edges).</param>
public sealed record MetalTolerance(bool IsFloat, double Tolerance, int MaxBad, bool IsHalf = false);

/// <summary>
/// Reads what the Metal runner produced for each exported case, compares it with the reference outputs and builds a
/// per-case table: match, mismatch with the worst error, not compiled with the diagnostic, validation messages.
/// </summary>
public static class MetalCompare
{
    /// <summary>The Metal device wrote these lines to stderr that are not validation output.</summary>
    private static readonly string[] Noise = ["objc[", "Validation Enabled", "Metal GPU Frame Capture"];

    public static Dictionary<(string Case, string Key), MetalTolerance> LoadTolerances()
    {
        var map = new Dictionary<(string, string), MetalTolerance>();
        string path = Path.Combine(Tools.CorpusDir, "metal-tolerances.txt");
        if (!File.Exists(path))
        {
            return map;
        }
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }
            // CASE KEY bytes|float|half TOLERANCE [bad=N]
            string[] t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 4 || (t[2] != "bytes" && t[2] != "float" && t[2] != "half"))
            {
                throw new FormatException($"metal-tolerances.txt: '{line}' is not 'CASE KEY bytes|float|half TOLERANCE [bad=N]'");
            }
            int bad = t.Length > 4 && t[4].StartsWith("bad=", StringComparison.Ordinal) ? int.Parse(t[4][4..], CultureInfo.InvariantCulture) : 0;
            map[(t[0], t[1])] = new MetalTolerance(t[2] is "float" or "half", double.Parse(t[3], CultureInfo.InvariantCulture), bad, t[2] == "half");
        }
        return map;
    }

    /// <summary>Row of the table: case, status, detail.</summary>
    public sealed record Row(string Case, string Status, string Detail)
    {
        public bool Passed => Status == "match";
    }

    public static Row Check(string bundleDir, string name, Dictionary<(string Case, string Key), MetalTolerance> tolerances)
    {
        string dir = Path.Combine(bundleDir, name);
        string status = File.ReadAllText(Path.Combine(dir, "status.txt")).Trim();
        if (status.StartsWith("not-compiled", StringComparison.Ordinal))
        {
            return new Row(name, "not-compiled", status["not-compiled: ".Length..]);
        }
        if (!status.StartsWith("ok", StringComparison.Ordinal))
        {
            return new Row(name, "reference-failed", status);
        }
        string exitFile = Path.Combine(dir, "exit.txt");
        if (!File.Exists(exitFile))
        {
            return new Row(name, "not-run", "the runner produced no result for this case");
        }
        int exit = int.Parse(File.ReadAllText(exitFile).Trim(), CultureInfo.InvariantCulture);
        string stdout = Read(dir, "stdout.log");
        var validation = Read(dir, "stderr.log").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !Noise.Any(n => l.Contains(n, StringComparison.Ordinal))).ToList();
        if (exit == 3)
        {
            return new Row(name, "metal-compile-fail", FirstLine(stdout));
        }
        if (exit != 0)
        {
            string how = validation.Count > 0 ? validation[0] : FirstLine(stdout);
            return new Row(name, validation.Count > 0 ? "validation" : "run-failed", $"exit {exit}: {how}");
        }

        var model = Corpus.Load(name);
        var details = new List<string>();
        var failures = new List<string>();
        foreach (string line in File.ReadAllLines(Path.Combine(dir, "ref.txt")))
        {
            string[] t = line.Split(' ');
            string key = t[0];
            byte[] want = File.ReadAllBytes(Path.Combine(dir, "ref", t[1]));
            string gotPath = Path.Combine(dir, "metal", t[1]);
            if (!File.Exists(gotPath))
            {
                failures.Add($"{key}: no output");
                continue;
            }
            byte[] got = File.ReadAllBytes(gotPath);
            var format = MetalBundle.ParseFormat(t[4]);
            bool isImage = t[5] == "1";
            if (got.Length != want.Length)
            {
                failures.Add($"{key}: {got.Length} bytes, expected {want.Length}");
                continue;
            }
            tolerances.TryGetValue((name, key), out var tol);
            string? problem = isImage
                ? CompareImage(model, key, format, int.Parse(t[2], CultureInfo.InvariantCulture), int.Parse(t[3], CultureInfo.InvariantCulture), want, got, tol, details)
                : CompareBuffer(model, key, want, got, tol, details);
            if (problem is not null)
            {
                failures.Add(problem);
            }
        }

        if (validation.Count > 0)
        {
            return new Row(name, "validation", $"{validation.Count} message(s): {validation[0]}");
        }
        return failures.Count > 0
            ? new Row(name, "mismatch", string.Join("; ", failures))
            : new Row(name, "match", string.Join("; ", details));
    }

    private static string? CompareImage(CaseModel model, string key, PixelFormat format, int w, int h, byte[] want, byte[] got,
        MetalTolerance? tol, List<string> details)
    {
        if (format is PixelFormat.R8 or PixelFormat.Rgba8)
        {
            double allowed = tol?.Tolerance ?? model.Graphics?.Tolerance ?? 0;
            int worst = 0;
            int over = 0;
            for (int i = 0; i < want.Length; i++)
            {
                int d = Math.Abs(want[i] - got[i]);
                worst = Math.Max(worst, d);
                if (d > allowed)
                {
                    over++;
                }
            }
            string text = $"{key} worst {worst}/255 (allowed {allowed:G3}{(tol is { MaxBad: > 0 } ? $", up to {tol.MaxBad} over" : "")}, {over} over)";
            if (over > (tol?.MaxBad ?? 0))
            {
                return text + " at " + Where(want, got, allowed, ImageGen.BytesPerTexel(format), w);
            }
            details.Add(text);
            return null;
        }
        float[] a = Parity.DecodeFloats(format, want);
        float[] b = Parity.DecodeFloats(format, got);
        double allowedRel = tol?.Tolerance ?? model.FloatTolerance;
        double worstRel = 0;
        int overRel = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (float.IsNaN(a[i]) && float.IsNaN(b[i]))
            {
                continue;
            }
            double err = Math.Abs((double)a[i] - b[i]) / (1.0 + Math.Abs(a[i]));
            err = double.IsNaN(err) ? double.PositiveInfinity : err;
            worstRel = Math.Max(worstRel, err);
            if (!(err <= allowedRel))
            {
                overRel++;
            }
        }
        string t2 = $"{key} worst {worstRel:G3} (allowed {allowedRel:G3}{(tol is { MaxBad: > 0 } ? $", up to {tol.MaxBad} over" : "")}, {overRel} over)";
        if (overRel > (tol?.MaxBad ?? 0))
        {
            return t2;
        }
        details.Add(t2);
        return null;
    }

    private static string? CompareBuffer(CaseModel model, string key, byte[] want, byte[] got, MetalTolerance? tol, List<string> details)
    {
        // Lists filled by atomics come back in any order: compare them as multisets.
        bool sorted = model.Resources.Any(r => r.Output && r.OutputKey == key && r.Sorted);
        if (sorted)
        {
            got = VkRunner.SortWords(got);
        }
        if (tol is { IsFloat: true })
        {
            double worst = 0;
            int over = 0;
            int step = tol.IsHalf ? 2 : 4;
            for (int i = 0; i + step <= want.Length; i += step)
            {
                float a = tol.IsHalf ? (float)BitConverter.ToHalf(want, i) : BitConverter.ToSingle(want, i);
                float b = tol.IsHalf ? (float)BitConverter.ToHalf(got, i) : BitConverter.ToSingle(got, i);
                if (float.IsNaN(a) && float.IsNaN(b))
                {
                    continue;
                }
                double err = Math.Abs((double)a - b) / (1.0 + Math.Abs(a));
                err = double.IsNaN(err) ? double.PositiveInfinity : err;
                worst = Math.Max(worst, err);
                if (!(err <= tol.Tolerance))
                {
                    over++;
                }
            }
            string text = $"{key} worst {worst:G3} (allowed {tol.Tolerance:G3}, {over} over)";
            if (over > tol.MaxBad)
            {
                return text;
            }
            details.Add(text);
            return null;
        }
        int differing = 0;
        int first = -1;
        double asFloat = 0;
        for (int i = 0; i < want.Length; i++)
        {
            if (want[i] != got[i])
            {
                differing++;
                first = first < 0 ? i : first;
            }
        }
        if (differing == 0)
        {
            details.Add($"{key} exact");
            return null;
        }
        for (int i = 0; i + 4 <= want.Length; i += 4)
        {
            float a = BitConverter.ToSingle(want, i);
            float b = BitConverter.ToSingle(got, i);
            double err = Math.Abs((double)a - b) / (1.0 + Math.Abs(a));
            asFloat = Math.Max(asFloat, double.IsNaN(err) ? double.PositiveInfinity : err);
        }
        return $"{key} {differing} bytes differ, first at offset {first} (read as floats the worst relative error is {asFloat:G3})";
    }

    private static string Where(byte[] want, byte[] got, double allowed, int bpt, int w)
    {
        for (int i = 0; i < want.Length; i++)
        {
            if (Math.Abs(want[i] - got[i]) > allowed)
            {
                int texel = i / bpt;
                return $"({texel % w},{texel / w}) channel {i % bpt}: want {want[i]}, got {got[i]}";
            }
        }
        return "?";
    }

    private static string Read(string dir, string file)
    {
        string p = Path.Combine(dir, file);
        return File.Exists(p) ? File.ReadAllText(p) : "";
    }

    private static string FirstLine(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    /// <summary>Checks every exported case and renders the table.</summary>
    public static (List<Row> Rows, string Table) CheckAll(string bundleDir)
    {
        var tolerances = LoadTolerances();
        var rows = new List<Row>();
        foreach (string dir in Directory.GetDirectories(bundleDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            if (File.Exists(Path.Combine(dir, "status.txt")))
            {
                rows.Add(Check(bundleDir, Path.GetFileName(dir), tolerances));
            }
        }
        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            sb.Line($"{r.Case,-28} {r.Status,-18} {r.Detail}\n");
        }
        sb.Line($"\n{rows.Count(r => r.Passed)} match, {rows.Count(r => !r.Passed)} not matching, of {rows.Count} cases; ");
        sb.Line($"validation messages in {rows.Count(r => r.Status == "validation")} cases\n");
        return (rows, sb.ToString());
    }
}
