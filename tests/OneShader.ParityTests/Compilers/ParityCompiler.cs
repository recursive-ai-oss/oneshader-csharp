using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace OneShader.ParityTests;

/// <summary>One shader entry point to compile.</summary>
/// <param name="SourcePath">Absolute path of the HLSL file.</param>
/// <param name="Entry">HLSL entry point name.</param>
/// <param name="Stage">Stage the entry point is compiled for.</param>
/// <param name="Defines">Preprocessor definitions (name, value) the case asks for.</param>
public sealed record ShaderRequest(string SourcePath, string Entry, ShaderStage Stage, IReadOnlyList<KeyValuePair<string, string>> Defines);

/// <summary>
/// A case that defines <see cref="Define"/> is compiled with 16-bit types on (DXC's <c>-enable-16bit-types</c>, shader model 6.2);
/// the macro is also visible to the shader.
/// </summary>
public static class SizedTypes
{
    /// <summary>The case-level macro that switches 16-bit types on.</summary>
    public const string Define = "ONESHADER_16BIT";

    /// <summary>True when <paramref name="defines"/> asks for 16-bit types.</summary>
    public static bool Enabled(IEnumerable<KeyValuePair<string, string>> defines) => defines.Any(d => d.Key == Define && d.Value != "0");
}

/// <summary>Compiles one entry point to SPIR-V (Vulkan 1.2) bytes; throws with the compiler's message on failure.</summary>
public delegate byte[] ShaderCompile(ShaderRequest request);

/// <summary>A named compiler: the unit the parity comparison is made between.</summary>
public sealed class ParityCompiler
{
    public ParityCompiler(string name, ShaderCompile compile)
    {
        Name = name;
        Compile = compile;
    }

    public string Name { get; }

    public ShaderCompile Compile { get; }
}

/// <summary>DXC's SPIR-V path, run as an external process (a released binary used as a test oracle).</summary>
public static class DxcCompiler
{
    private static int counter;

    /// <summary>Creates a DXC compiler. <paramref name="extraDefines"/> are added after the case's own defines, so they win.</summary>
    public static ParityCompiler Create(string name, params string[] extraDefines)
    {
        var cache = new ConcurrentDictionary<string, byte[]>();
        return new ParityCompiler(name, request =>
        {
            string key = request.SourcePath + "|" + request.Entry + "|" + request.Stage + "|"
                + string.Join(';', request.Defines.Select(d => d.Key + "=" + d.Value));
            return cache.GetOrAdd(key, _ => Compile(request, extraDefines));
        });
    }

    /// <summary>Profile string DXC wants for a stage.</summary>
    public static string Profile(ShaderStage stage, bool sixTwo = false) => stage switch
    {
        ShaderStage.Vertex => sixTwo ? "vs_6_2" : "vs_6_0",
        ShaderStage.Pixel => sixTwo ? "ps_6_2" : "ps_6_0",
        _ => sixTwo ? "cs_6_2" : "cs_6_0",
    };

    private static byte[] Compile(ShaderRequest request, string[] extraDefines)
    {
        string dxc = Tools.Dxc ?? throw new InvalidOperationException("dxc not found: run tools/fetch-oracles.sh and set ONESHADER_TOOLS");
        string dir = Path.Combine(Path.GetTempPath(), "oneshader-parity-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        string output = Path.Combine(dir, $"{Interlocked.Increment(ref counter)}.spv");
        var args = new List<string>
        {
            "-spirv", "-fspv-target-env=vulkan1.2", "-HV", "2021",
            "-T", Profile(request.Stage, SizedTypes.Enabled(request.Defines)), "-E", request.Entry,
            "-I", Path.GetDirectoryName(request.SourcePath)!,
            "-Fo", output,
        };
        if (SizedTypes.Enabled(request.Defines))
        {
            args.Add("-enable-16bit-types");
        }
        foreach (var d in request.Defines)
        {
            args.Add("-D");
            args.Add(d.Key + "=" + d.Value);
        }
        foreach (string d in extraDefines)
        {
            args.Add("-D");
            args.Add(d);
        }
        args.Add(request.SourcePath);
        var (code, text) = Tools.Run(dxc, args);
        if (code != 0 || !File.Exists(output))
        {
            throw new InvalidOperationException($"dxc failed for {request.Entry} ({request.Stage}) in {request.SourcePath}:\n{text}");
        }
        byte[] bytes = File.ReadAllBytes(output);
        File.Delete(output);
        return bytes;
    }
}

/// <summary>
/// The two sides of every parity comparison. To compare our compiler against DXC, point
/// <see cref="Candidate"/> at a <see cref="ParityCompiler"/> that wraps it; nothing else changes.
/// </summary>
public static class ParityCompilers
{
    /// <summary>The reference: DXC's SPIR-V output.</summary>
    public static ParityCompiler Reference { get; } = DxcCompiler.Create("dxc");

    /// <summary>The compiler under test: this repository's own.</summary>
    public static ParityCompiler Candidate { get; } =
        Environment.GetEnvironmentVariable("ONESHADER_PARITY_TARGET") == "wgsl" ? WgslNagaCompiler.Create("oneshader-wgsl") : OneShaderCompiler.Create("oneshader");

    /// <summary>Entry names a case needs, per stage, in the order they are compiled.</summary>
    public static IEnumerable<(string Entry, ShaderStage Stage)> Entries(CaseModel c)
    {
        if (c.Graphics is { } g)
        {
            yield return (g.VertexEntry, ShaderStage.Vertex);
            if (g.PixelEntry.Length > 0)
            {
                yield return (g.PixelEntry, ShaderStage.Pixel);
            }
        }
        foreach (string entry in c.Dispatches.Select(d => d.Entry).Distinct())
        {
            yield return (entry, ShaderStage.Compute);
        }
    }
}
