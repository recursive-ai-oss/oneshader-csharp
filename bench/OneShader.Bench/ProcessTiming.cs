using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace OneShader.Bench;

/// <summary>
/// Times whole processes: DXC (a released binary), and this repository's compiler as a normal build (JIT) and as a
/// NativeAOT binary, each compiling one entry point of one file to SPIR-V and writing it to disk.
/// </summary>
public static class ProcessTiming
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed record Tool(string Name, string Path, Func<Unit, string, string, string[]> Args);

    /// <summary>Runs the measurement and writes the results as JSON.</summary>
    public static int Run(string[] args)
    {
        string? dxc = Opt(args, "--dxc");
        string? jit = Opt(args, "--jit");
        string? aot = Opt(args, "--aot");
        string? r2r = Opt(args, "--r2r");
        string outFile = Opt(args, "--out") ?? "process.json";
        int warm = int.Parse(Opt(args, "--warm") ?? "10", System.Globalization.CultureInfo.InvariantCulture);
        int limit = int.Parse(Opt(args, "--limit") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        string work = Opt(args, "--work") ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oneshader-bench-" + Environment.ProcessId);
        if (dxc is null || jit is null || aot is null)
        {
            Console.Error.WriteLine("process: --dxc, --jit and --aot are required");
            return 2;
        }

        // The "files are written" step: a fresh copy of the corpus, so every first run reads files this run wrote.
        string source = Manifest.FindCorpus();
        string corpus = System.IO.Path.Combine(work, "corpus");
        string outDir = System.IO.Path.Combine(work, "out");
        if (Directory.Exists(work))
        {
            Directory.Delete(work, true);
        }

        Directory.CreateDirectory(outDir);
        CopyDirectory(source, corpus);
        var units = Manifest.Load(corpus);
        if (limit > 0)
        {
            units = units.Take(limit).ToArray();
        }

        string dxcVersion = Capture(dxc, "--version");
        var tools = new List<Tool>
        {
            new("dxc", dxc, (u, file, o) => DxcArgs(u, file, o)),
            new("jit", jit, (u, file, o) => OursArgs(u, file, o, false, false)),
            new("aot", aot, (u, file, o) => OursArgs(u, file, o, false, false)),
        };
        if (r2r is not null)
        {
            // Information row: the framework-dependent build with precompiled (ReadyToRun) code, which still starts the runtime.
            tools.Insert(2, new Tool("r2r", r2r, (u, file, o) => OursArgs(u, file, o, false, false)));
        }


        using var sampler = new LoadSampler(5);
        var startLoad = ReadLoad();
        var rows = new List<Dictionary<string, object?>>();
        foreach (var u in units)
        {
            rows.Add(new Dictionary<string, object?> { ["id"] = u.Id, ["file"] = u.File, ["entry"] = u.Entry, ["stage"] = u.Stage });
        }

        string file(Unit u) => System.IO.Path.Combine(corpus, u.File);
        string outPath(string tool) => System.IO.Path.Combine(outDir, tool + ".spv");

        // Cold: the first run of each unit with each tool, straight after the corpus was written. Sizes come from these runs.
        Console.WriteLine("cold pass: " + units.Count + " units");
        for (int i = 0; i < units.Count; i++)
        {
            foreach (var t in tools)
            {
                var (ms, code, err) = Time(t.Path, t.Args(units[i], file(units[i]), outPath(t.Name)));
                rows[i][t.Name + "_cold_ms"] = ms;
                if (code != 0)
                {
                    rows[i][t.Name + "_error"] = err;
                    Console.Error.WriteLine(t.Name + " failed on " + units[i].Id + ": " + err);
                    continue;
                }

                rows[i][t.Name + "_bytes"] = new FileInfo(outPath(t.Name)).Length;
            }

            // Size only: the same compile with debug names off, not timed.
            var (_, c2, _) = Time(jit, OursArgs(units[i], file(units[i]), outPath("names"), false, true));
            if (c2 == 0)
            {
                rows[i]["ours_nonames_bytes"] = new FileInfo(outPath("names")).Length;
            }
        }

        // Warm: repeated runs, tools interleaved within each round so drift hits all of them alike.
        var warmSamples = new Dictionary<string, List<double>>[units.Count];
        for (int i = 0; i < units.Count; i++)
        {
            warmSamples[i] = tools.ToDictionary(t => t.Name, _ => new List<double>());
        }

        for (int round = 0; round < warm; round++)
        {
            Console.WriteLine("warm round " + (round + 1) + "/" + warm);
            for (int i = 0; i < units.Count; i++)
            {
                foreach (var t in tools)
                {
                    var (ms, code, _) = Time(t.Path, t.Args(units[i], file(units[i]), outPath(t.Name)));
                    if (code == 0)
                    {
                        warmSamples[i][t.Name].Add(ms);
                    }
                }
            }
        }

        for (int i = 0; i < units.Count; i++)
        {
            foreach (var t in tools)
            {
                rows[i][t.Name + "_warm_ms"] = warmSamples[i][t.Name];
            }
        }

        sampler.Sample();
        var endLoad = ReadLoad();
        var load = sampler.Loads;
        var doc = new Dictionary<string, object?>
        {
            ["host"] = HostInfo(dxcVersion),
            ["warmRounds"] = warm,
            ["loadStart"] = startLoad,
            ["loadEnd"] = endLoad,
            ["loadDuring"] = load,
            ["units"] = rows,
        };
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outFile))!);
        File.WriteAllText(outFile, JsonSerializer.Serialize(doc, JsonOptions));
        Directory.Delete(work, true);
        Console.WriteLine("wrote " + outFile);
        return rows.Any(r => r.Keys.Any(k => k.EndsWith("_error", StringComparison.Ordinal))) ? 1 : 0;
    }

    private static string[] DxcArgs(Unit u, string file, string output)
    {
        var a = new List<string>
        {
            "-spirv", "-fspv-target-env=vulkan1.2", "-HV", "2021", "-T", u.DxcProfile, "-E", u.Entry,
            "-I", System.IO.Path.GetDirectoryName(file)!, "-Fo", output,
        };
        foreach (var d in u.Defines)
        {
            a.Add("-D");
            a.Add(d.Key + "=" + d.Value);
        }

        a.Add(file);
        return a.ToArray();
    }

    private static string[] OursArgs(Unit u, string file, string output, bool noOpt, bool noNames)
    {
        var a = new List<string> { file, "-E", u.Entry, "-T", u.Stage, "-t", "spirv", "-I", System.IO.Path.GetDirectoryName(file)!, "-o", output };
        foreach (var d in u.Defines)
        {
            a.Add("-D");
            a.Add(d.Key + "=" + d.Value);
        }

        if (noOpt)
        {
            a.Add("--no-opt");
        }

        if (noNames)
        {
            a.Add("--no-names");
        }

        return a.ToArray();
    }

    // Wall-clock time from just before the process is created to its exit, output discarded.
    private static (double Ms, int Code, string Error) Time(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi)!;
        var stderr = new StringBuilder();
        p.ErrorDataReceived += (_, e) => stderr.AppendLine(e.Data);
        p.OutputDataReceived += (_, _) => { };
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        p.WaitForExit();
        sw.Stop();
        return (sw.Elapsed.TotalMilliseconds, p.ExitCode, stderr.ToString().Trim());
    }

    private static string Capture(string exe, string arg)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, arg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            string text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return text.Trim().ReplaceLineEndings(" ");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "unknown";
        }
    }

    private static string ReadLoad()
    {
        try
        {
            return File.ReadAllText("/proc/loadavg").Trim();
        }
        catch (IOException)
        {
            return "";
        }
    }

    /// <summary>Hardware and software description for the results file.</summary>
    public static Dictionary<string, object?> HostInfo(string dxcVersion)
    {
        string cpu = "unknown";
        try
        {
            cpu = File.ReadLines("/proc/cpuinfo").First(l => l.StartsWith("model name", StringComparison.Ordinal)).Split(':')[1].Trim();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // Not Linux.
        }

        long ramKb = 0;
        try
        {
            ramKb = long.Parse(File.ReadLines("/proc/meminfo").First(l => l.StartsWith("MemTotal", StringComparison.Ordinal)).Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FormatException)
        {
            // Not Linux.
        }

        return new Dictionary<string, object?>
        {
            ["cpu"] = cpu,
            ["logicalCores"] = Environment.ProcessorCount,
            ["ramGiB"] = Math.Round(ramKb / 1048576.0),
            ["os"] = RuntimeInformation.OSDescription,
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["dxc"] = dxcVersion,
        };
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from))
        {
            File.Copy(f, System.IO.Path.Combine(to, System.IO.Path.GetFileName(f)));
        }

        foreach (string d in Directory.GetDirectories(from))
        {
            CopyDirectory(d, System.IO.Path.Combine(to, System.IO.Path.GetFileName(d)));
        }
    }

    private static string? Opt(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
