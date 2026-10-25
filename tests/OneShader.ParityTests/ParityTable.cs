using System;
using System.IO;
using System.Linq;

namespace OneShader.ParityTests;

/// <summary>
/// Appends one tab-separated row per checked case to <c>parity-table.tsv</c> in the artifacts directory, so a full run
/// leaves a per-case table (match, mismatch, a compiler failing to compile, validation messages) next to the test log.
/// </summary>
public static class ParityTable
{
    private static readonly object Gate = new();

    /// <summary>Records the outcome of <paramref name="check"/> (<c>vs-dxc</c> or <c>vs-cpu</c>) for a case.</summary>
    public static void Record(string name, string check, ParityReport report)
    {
        int dxcValidation = report.Failures.Count(f => f.StartsWith("validation layer (dxc)", StringComparison.Ordinal));
        int candidateValidation = report.Failures.Count(f => f.StartsWith("validation layer (", StringComparison.Ordinal)) - dxcValidation;
        var others = report.Failures.Where(f => !f.StartsWith("validation layer", StringComparison.Ordinal)).ToList();
        string status = others.Count > 0 ? "mismatch" : dxcValidation + candidateValidation > 0 ? "validation" : "match";
        string detail = others.Count > 0 ? others[0] : report.Failures.Count > 0 ? report.Failures[0] : string.Join("; ", report.Notes);
        Write(name, check, status, dxcValidation, candidateValidation, detail);
    }

    /// <summary>Records an exception thrown while compiling or running a case.</summary>
    public static void Record(string name, string check, Exception ex)
    {
        string message = ex.Message;
        string status = ex is UnsupportedOnTargetException ? "unsupported-on-target"
            : message.StartsWith("OneShader failed", StringComparison.Ordinal) ? "candidate-compile-fail"
            : message.StartsWith("dxc failed", StringComparison.Ordinal) ? "dxc-compile-fail"
            : "error";
        Write(name, check, status, 0, 0, message);
    }

    private static void Write(string name, string check, string status, int dxcValidation, int candidateValidation, string detail)
    {
        string oneLine = string.Join(" | ", detail.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Take(4)).Replace('\t', ' ');
        string row = string.Join('\t', name, check, status, dxcValidation, candidateValidation, oneLine);
        lock (Gate)
        {
            Directory.CreateDirectory(Tools.ArtifactsDir);
            File.AppendAllText(Path.Combine(Tools.ArtifactsDir, "parity-table.tsv"), row + "\n");
        }
    }
}
