using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace OneShader.ParityTests;

/// <summary>
/// The list in <c>corpus/known-gaps.txt</c>: cases and probes the compiler does not pass yet. A listed entry that
/// fails is skipped with its reason; a listed entry that passes fails, asking for the line to be removed.
/// </summary>
public static class KnownGaps
{
    /// <summary>Entry name to reason.</summary>
    public static IReadOnlyDictionary<string, string> Entries { get; } = Load();

    private static Dictionary<string, string> Load()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string raw in File.ReadAllLines(Path.Combine(Tools.CorpusDir, "known-gaps.txt")))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon <= 0)
            {
                throw new FormatException($"known-gaps.txt: '{line}' is not 'NAME: reason'");
            }
            map[line[..colon]] = line[(colon + 2)..];
        }
        return map;
    }

    /// <summary>
    /// Runs <paramref name="check"/> (an assertion about the compiler under test). For a listed name a failure becomes a skip
    /// and a pass becomes a failure. Do any tool or device gating before calling, since a skip raised inside would count as a gap.
    /// </summary>
    public static void Check(string name, Action check)
    {
        if (!Entries.TryGetValue(name, out string? reason))
        {
            check();
            return;
        }
        try
        {
            check();
        }
        catch (Exception)
        {
            Assert.Skip($"known gap: {reason}");
            return;
        }
        Assert.Fail($"{name} is listed in corpus/known-gaps.txt but now passes: remove it from known-gaps.txt");
    }
}

public class KnownGapsTests
{
    [Fact]
    public void EveryEntryNamesACaseOrProbe()
    {
        var names = Corpus.CaseNames.Concat(Corpus.ProbePaths.Select(p => Path.GetFileName(p))).ToHashSet(StringComparer.Ordinal);
        foreach (string entry in KnownGaps.Entries.Keys)
        {
            Assert.True(names.Contains(entry), $"known-gaps.txt lists '{entry}', which is not a case or probe");
        }
    }
}
