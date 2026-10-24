using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace OneShader.Tests;

/// <summary>Finds and runs the released oracle binaries (dxc, spirv-dis); tests skip when they are missing.</summary>
internal static class Oracles
{
    public static string? Find(string name)
    {
        var tools = Environment.GetEnvironmentVariable("ONESHADER_TOOLS");
        if (!string.IsNullOrEmpty(tools))
        {
            var p = Path.Combine(tools, "bin", name);
            if (File.Exists(p))
            {
                return p;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p))
            {
                return p;
            }
        }

        return null;
    }

    public static (int ExitCode, string Output) Run(string exe, IEnumerable<string> args, string workDir)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi)!;
        var err = proc.StandardError.ReadToEndAsync();
        var outText = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, outText + err.Result);
    }
}
