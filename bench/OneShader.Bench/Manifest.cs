using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OneShader.Bench;

/// <summary>One entry point of one corpus file, as the execution-parity cases compile it.</summary>
/// <param name="File">Path relative to the corpus directory, with forward slashes.</param>
/// <param name="Entry">Entry point name.</param>
/// <param name="Stage"><c>vs</c>, <c>ps</c> or <c>cs</c>.</param>
/// <param name="Defines">Preprocessor definitions the case asks for.</param>
public sealed record Unit(string File, string Entry, string Stage, IReadOnlyList<KeyValuePair<string, string>> Defines)
{
    /// <summary>A stable label: file, entry and stage, plus defines when there are any.</summary>
    public string Id => File + ":" + Entry + "(" + Stage + ")" + DefineSuffix;

    /// <summary>Empty without defines, otherwise <c>[A=1,B=2]</c>.</summary>
    public string DefineSuffix => Defines.Count == 0 ? "" : "[" + string.Join(',', Defines.Select(d => d.Key + "=" + d.Value)) + "]";

    /// <summary>The ShaderStage for the compiler API.</summary>
    public ShaderStage ApiStage => Stage switch { "vs" => ShaderStage.Vertex, "ps" => ShaderStage.Pixel, _ => ShaderStage.Compute };

    /// <summary>The DXC profile for the stage (Shader Model 6.0).</summary>
    public string DxcProfile => Stage switch { "vs" => "vs_6_0", "ps" => "ps_6_0", _ => "cs_6_0" };
}

/// <summary>All entry points of one file (with one set of defines): what one in-process compile does.</summary>
/// <param name="Name">File path, plus the defines when there are any.</param>
/// <param name="File">Path relative to the corpus directory.</param>
/// <param name="Defines">Definitions shared by the units.</param>
/// <param name="Units">The entry points, in case-file order.</param>
public sealed record FileGroup(string Name, string File, IReadOnlyList<KeyValuePair<string, string>> Defines, IReadOnlyList<Unit> Units);

/// <summary>
/// The set of (file, entry point) pairs to time: every pair the execution-parity cases compile, read from the
/// <c>shader</c>, <c>define</c>, <c>graphics</c> and <c>dispatch</c> lines of <c>corpus/cases/*.case</c>, without duplicates.
/// </summary>
public static class Manifest
{
    /// <summary>Finds the repository's corpus directory (ONESHADER_CORPUS, or by walking up from the current or program directory).</summary>
    public static string FindCorpus()
    {
        string? env = Environment.GetEnvironmentVariable("ONESHADER_CORPUS");
        if (!string.IsNullOrEmpty(env))
        {
            return Path.GetFullPath(env);
        }

        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (string? dir = start; dir is not null; dir = Path.GetDirectoryName(dir))
            {
                if (Directory.Exists(Path.Combine(dir, "corpus", "cases")))
                {
                    return Path.Combine(dir, "corpus");
                }
            }
        }

        throw new DirectoryNotFoundException("corpus directory not found; set ONESHADER_CORPUS");
    }

    /// <summary>Reads every case under <paramref name="corpusDir"/>.</summary>
    public static IReadOnlyList<Unit> Load(string corpusDir)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var units = new List<Unit>();
        foreach (string path in Directory.GetFiles(Path.Combine(corpusDir, "cases"), "*.case").Order(StringComparer.Ordinal))
        {
            string? shader = null;
            var defines = new List<KeyValuePair<string, string>>();
            var entries = new List<(string Entry, string Stage)>();
            foreach (string raw in File.ReadAllLines(path))
            {
                if (raw.Length == 0 || char.IsWhiteSpace(raw[0]))
                {
                    continue;
                }

                int hash = raw.IndexOf('#', StringComparison.Ordinal);
                string[] t = (hash >= 0 ? raw[..hash] : raw).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length < 2)
                {
                    continue;
                }

                switch (t[0])
                {
                    case "shader":
                        shader = t[1];
                        break;
                    case "define":
                        {
                            int eq = t[1].IndexOf('=', StringComparison.Ordinal);
                            defines.Add(eq < 0 ? new(t[1], "1") : new(t[1][..eq], t[1][(eq + 1)..]));
                            break;
                        }
                    case "graphics":
                        foreach (string option in t.Skip(1))
                        {
                            if (option.StartsWith("vs=", StringComparison.Ordinal))
                            {
                                entries.Add((option[3..], "vs"));
                            }
                            else if (option.StartsWith("ps=", StringComparison.Ordinal) && option.Length > 3)
                            {
                                entries.Add((option[3..], "ps"));
                            }
                        }

                        break;
                    case "dispatch":
                        entries.Add((t[1], "cs"));
                        break;
                    default:
                        break;
                }
            }

            if (shader is null)
            {
                continue;
            }

            foreach (var (entry, stage) in entries)
            {
                var unit = new Unit(shader, entry, stage, defines.ToArray());
                if (seen.Add(unit.Id))
                {
                    units.Add(unit);
                }
            }
        }

        return units.OrderBy(u => u.File, StringComparer.Ordinal).ThenBy(u => u.DefineSuffix, StringComparer.Ordinal)
            .ThenBy(u => u.Entry, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Groups units by file and defines.</summary>
    public static IReadOnlyList<FileGroup> Groups(IReadOnlyList<Unit> units)
    {
        var result = new List<FileGroup>();
        foreach (var g in units.GroupBy(u => u.File + u.DefineSuffix))
        {
            var first = g.First();
            result.Add(new FileGroup(g.Key, first.File, first.Defines, g.ToArray()));
        }

        return result;
    }

    /// <summary>The group's compile options for the in-process benchmark.</summary>
    public static CompileOptions Options(FileGroup group, string corpusDir, CompileTarget target, bool optimize) => new()
    {
        Targets = [target],
        Optimize = optimize,
        IncludeDirectories = [Path.GetDirectoryName(Path.Combine(corpusDir, group.File))!],
        Defines = group.Defines.Select(d => new KeyValuePair<string, string?>(d.Key, d.Value)).ToArray(),
        EntryPoints = group.Units.Select(u => new EntryPointRequest(u.Entry, u.ApiStage)).ToArray(),
    };
}
