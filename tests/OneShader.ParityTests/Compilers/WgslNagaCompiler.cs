using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace OneShader.ParityTests;

/// <summary>The target backend refused a shader with its documented "not available on this target" error.</summary>
public sealed class UnsupportedOnTargetException(string message) : InvalidOperationException(message);

/// <summary>
/// This repository's WGSL output, translated to SPIR-V by the naga command-line tool (a released binary used as a test oracle),
/// so the same GPU comparison against DXC runs on what the WGSL backend wrote. Selected with ONESHADER_PARITY_TARGET=wgsl.
/// </summary>
public static class WgslNagaCompiler
{
    private static int counter;

    public static ParityCompiler Create(string name)
    {
        var cache = new ConcurrentDictionary<string, byte[]>();
        return new ParityCompiler(name, request =>
        {
            string key = request.SourcePath + "|" + request.Entry + "|" + request.Stage + "|"
                + string.Join(';', request.Defines.Select(d => d.Key + "=" + d.Value));
            return cache.GetOrAdd(key, _ => Compile(request));
        });
    }

    private static byte[] Compile(ShaderRequest request)
    {
        string naga = Tools.ToolsDir is not null && File.Exists(Path.Combine(Tools.ToolsDir, "bin", "naga"))
            ? Path.Combine(Tools.ToolsDir, "bin", "naga")
            : throw new InvalidOperationException("naga not found: run tools/fetch-oracles.sh and set ONESHADER_TOOLS");
        var stage = request.Stage switch
        {
            ShaderStage.Vertex => OneShader.ShaderStage.Vertex,
            ShaderStage.Pixel => OneShader.ShaderStage.Pixel,
            _ => OneShader.ShaderStage.Compute,
        };
        var options = new CompileOptions
        {
            Targets = [CompileTarget.Wgsl],
            Defines = request.Defines.Select(d => new KeyValuePair<string, string?>(d.Key, d.Value)).ToList(),
            IncludeDirectories = [Path.GetDirectoryName(request.SourcePath)!],
            EntryPoints = [new EntryPointRequest(request.Entry, stage)],
            Optimize = Environment.GetEnvironmentVariable("ONESHADER_OPTIMIZE") != "0",
        };
        var result = ShaderCompiler.Compile(File.ReadAllText(request.SourcePath), request.SourcePath, options);
        var blob = result.Find(request.Entry, CompileTarget.Wgsl);
        if (result.Diagnostics.Any(d => d.Code == "OSH5205") || !result.Success && result.Diagnostics.Any(d => d.Code == "OSH5202"))
        {
            // A construct WGSL cannot express is a documented diagnostic (an error, or for precise a warning), not a miscompile: the run reports it as unsupported.
            throw new UnsupportedOnTargetException("OneShader failed for " + request.Entry + " (" + request.Stage + ") in " + request.SourcePath + ":\n"
                + string.Join('\n', result.Diagnostics.Select(d => d.ToString())));
        }

        if (!result.Success || blob is null)
        {
            throw new InvalidOperationException("OneShader failed for " + request.Entry + " (" + request.Stage + ") in " + request.SourcePath + ":\n"
                + string.Join('\n', result.Diagnostics.Select(d => d.ToString())));
        }

        string dir = Path.Combine(Path.GetTempPath(), "oneshader-parity-wgsl-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        int id = Interlocked.Increment(ref counter);
        string wgsl = Path.Combine(dir, id + ".wgsl");
        string spv = Path.Combine(dir, id + ".spv");
        File.WriteAllText(wgsl, blob.AsText());
        var (code, text) = Tools.Run(naga, [wgsl, spv, "--entry-point", request.Entry, "--keep-coordinate-space", "--spirv-version", "1.3"]);
        if (code != 0 || !File.Exists(spv))
        {
            throw new InvalidOperationException("OneShader failed (naga rejected the WGSL) for " + request.Entry + ":\n" + text);
        }

        StripWorkgroupArrayStrides(spv, dir);
        byte[] bytes = File.ReadAllBytes(spv);
        File.Delete(wgsl);
        File.Delete(spv);
        return bytes;
    }

    /// <summary>
    /// naga decorates arrays in workgroup memory with ArrayStride, which Vulkan 1.2 only allows with an optional extension.
    /// The decoration carries no information for such memory, so the harness drops it before the module reaches the driver.
    /// </summary>
    private static void StripWorkgroupArrayStrides(string spv, string dir)
    {
        string? dis = Tools.ToolsDir is not null ? Path.Combine(Tools.ToolsDir, "bin", "spirv-dis") : null;
        string? asm = Tools.ToolsDir is not null ? Path.Combine(Tools.ToolsDir, "bin", "spirv-as") : null;
        if (dis is null || asm is null || !File.Exists(dis) || !File.Exists(asm))
        {
            return;
        }

        string text = Path.Combine(dir, Path.GetFileNameWithoutExtension(spv) + ".spvasm");
        if (Tools.Run(dis, [spv, "-o", text]).ExitCode != 0)
        {
            return;
        }

        string source = File.ReadAllText(text);
        var arrays = new HashSet<string>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(source, @"OpTypePointer Workgroup (%\S+)"))
        {
            arrays.Add(m.Groups[1].Value);
        }

        if (arrays.Count == 0)
        {
            File.Delete(text);
            return;
        }

        var kept = new List<string>();
        foreach (string line in source.Split('\n'))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^\s*OpDecorate (%\S+) ArrayStride \d+\s*$");
            if (m.Success && arrays.Contains(m.Groups[1].Value))
            {
                continue;
            }

            kept.Add(line);
        }

        File.WriteAllText(text, string.Join('\n', kept));
        Tools.Run(asm, [text, "-o", spv, "--target-env", "spv1.3"]);
        File.Delete(text);
    }
}
