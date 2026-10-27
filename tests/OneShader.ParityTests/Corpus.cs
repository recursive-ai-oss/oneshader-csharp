using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace OneShader.ParityTests;

/// <summary>Access to the corpus directory: case files and shaders.</summary>
public static class Corpus
{
    private static string CasesDir => Path.Combine(Tools.CorpusDir, "cases");

    /// <summary>
    /// Names (file names without extension) of every case, in alphabetical order. The environment variable
    /// ONESHADER_CASES (comma-separated name prefixes) narrows the list for a quick run on one area.
    /// </summary>
    public static IReadOnlyList<string> CaseNames { get; } = Directory.GetFiles(CasesDir, "*.case")
        .Select(p => Path.GetFileNameWithoutExtension(p))
        .Where(n => Environment.GetEnvironmentVariable("ONESHADER_CASES") is not { Length: > 0 } only || only.Split(',').Any(prefix => n.StartsWith(prefix, StringComparison.Ordinal)))
        .Order(StringComparer.Ordinal)
        .ToArray();

    /// <summary>Every shader (.hlsl) path relative to the corpus directory, with forward slashes; the single-construct probes are listed separately.</summary>
    public static IReadOnlyList<string> ShaderPaths { get; } = Directory.GetFiles(Tools.CorpusDir, "*.hlsl", SearchOption.AllDirectories)
        .Select(p => Path.GetRelativePath(Tools.CorpusDir, p).Replace('\\', '/'))
        .Where(p => !p.StartsWith("probes/", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal)
        .ToArray();

    /// <summary>Absolute paths of the single-construct probe shaders in <c>probes/</c>.</summary>
    public static IReadOnlyList<string> ProbePaths { get; } = Directory.GetFiles(Path.Combine(Tools.CorpusDir, "probes"), "*.hlsl")
        .Order(StringComparer.Ordinal)
        .ToArray();

    /// <summary>Probe file names as xunit theory data.</summary>
    public static TheoryData<string> Probes()
    {
        var d = new TheoryData<string>();
        foreach (string p in ProbePaths)
        {
            d.Add(Path.GetFileName(p));
        }
        return d;
    }

    public static CaseModel Load(string name) => CaseFile.Load(Path.Combine(CasesDir, name + ".case"));

    /// <summary>Case names as xunit theory data.</summary>
    public static TheoryData<string> Cases()
    {
        var d = new TheoryData<string>();
        foreach (string n in CaseNames)
        {
            d.Add(n);
        }
        return d;
    }

    /// <summary>Names of the compute cases that have a CPU reference, as xunit theory data.</summary>
    public static TheoryData<string> ComputeCases()
    {
        var d = new TheoryData<string>();
        foreach (string n in CaseNames.Where(n => Load(n) is { IsCompute: true, Expects.Count: > 0 }))
        {
            d.Add(n);
        }
        return d;
    }

    /// <summary>Shader paths as xunit theory data.</summary>
    public static TheoryData<string> Shaders()
    {
        var d = new TheoryData<string>();
        foreach (string s in ShaderPaths)
        {
            d.Add(s);
        }
        return d;
    }
}

/// <summary>One shared Vulkan context for the whole test run; null when the machine has no usable device.</summary>
public static class Gpu
{
    private static readonly Lazy<(VkContext? Context, string Reason)> Shared = new(() =>
    {
        var ctx = VkContext.TryCreate(out string reason);
        if (ctx is not null)
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => ctx.Dispose();
        }
        return (ctx, reason);
    });

    public static VkContext? Context => Shared.Value.Context;

    public static string UnavailableReason => Shared.Value.Reason;

    /// <summary>Skips the calling test when there is no usable Vulkan device (or the layer is missing).</summary>
    public static VkContext RequireOrSkip()
    {
        Assert.SkipWhen(Context is null, "no usable Vulkan device: " + UnavailableReason);
        return Context!;
    }
}

/// <summary>Skip helpers for tests that need the reference tools.</summary>
public static class ToolGate
{
    public static void RequireDxc() =>
        Assert.SkipWhen(Tools.Dxc is null, "dxc not found: run tools/fetch-oracles.sh and set ONESHADER_TOOLS");

    public static void RequireSpirvVal() =>
        Assert.SkipWhen(Tools.SpirvVal is null, "spirv-val not found: run tools/fetch-oracles.sh and set ONESHADER_TOOLS");
}
