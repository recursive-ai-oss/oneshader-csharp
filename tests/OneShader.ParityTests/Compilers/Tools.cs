using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace OneShader.ParityTests;

/// <summary>Locates the corpus, the reference tools and the output directory.</summary>
public static class Tools
{
    /// <summary>Directory holding corpus shaders and case files.</summary>
    public static string CorpusDir { get; } =
        Environment.GetEnvironmentVariable("ONESHADER_CORPUS")
        ?? typeof(Tools).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "CorpusDir").Value!;

    /// <summary>Tool directory from ONESHADER_TOOLS (see tools/fetch-oracles.sh), or null.</summary>
    public static string? ToolsDir { get; } = Environment.GetEnvironmentVariable("ONESHADER_TOOLS") is { Length: > 0 } t ? t : null;

    /// <summary>Where mismatching images and logs are written.</summary>
    public static string ArtifactsDir { get; } =
        Path.Combine(Environment.GetEnvironmentVariable("ONESHADER_ARTIFACTS") is { Length: > 0 } a ? a : Path.GetTempPath(), "parity");

    public static string? Dxc { get; } = Find("dxc");

    public static string? SpirvVal { get; } = Find("spirv-val");

    /// <summary>Folder with the validation layer manifest, or null.</summary>
    public static string? ValidationLayerDir { get; } =
        ToolsDir is not null && File.Exists(Path.Combine(ToolsDir, "vklayers", "VkLayer_khronos_validation.json"))
            ? Path.Combine(ToolsDir, "vklayers")
            : null;

    private static string? Find(string tool)
    {
        if (ToolsDir is not null)
        {
            string p = Path.Combine(ToolsDir, "bin", tool);
            if (File.Exists(p))
            {
                return p;
            }
        }
        string? path = Environment.GetEnvironmentVariable("PATH");
        foreach (string dir in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string p = Path.Combine(dir, tool);
            if (File.Exists(p))
            {
                return p;
            }
        }
        return null;
    }

    /// <summary>Runs a tool and returns exit code and combined output.</summary>
    public static (int ExitCode, string Output) Run(string exe, IEnumerable<string> args)
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
        if (ToolsDir is not null)
        {
            string lib = Path.Combine(ToolsDir, "lib");
            string? old = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
            psi.Environment["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(old) ? lib : lib + ":" + old;
        }
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
        var stderr = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(true);
            throw new TimeoutException($"{exe} did not finish within 120 s");
        }
        return (process.ExitCode, stdout + stderr.GetAwaiter().GetResult());
    }
}
