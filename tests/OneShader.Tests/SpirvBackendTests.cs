using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using OneShader.Diagnostics;
using OneShader.IR;
using OneShader.SpirV;
using Xunit;

namespace OneShader.Tests;

public sealed partial class SpirvBackendTests
{
    public static TheoryData<string> CaseNames()
    {
        var d = new TheoryData<string>();
        foreach (var c in SpirvTestModules.All())
        {
            d.Add(c.Name);
        }

        return d;
    }

    private static SpirvTestModules.Case Get(string name) => SpirvTestModules.All().First(c => c.Name == name);

    private static byte[] Compile(SpirvTestModules.Case c)
    {
        Assert.Empty(IrVerifier.Verify(c.Module));
        var diagnostics = new DiagnosticBag();
        var bytes = SpirvBackend.Emit(c.Module, c.Entry, null, diagnostics);
        Assert.True(bytes is not null, string.Join("\n", diagnostics.Items.Select(x => x.ToString())));
        Assert.Empty(diagnostics.Items);
        if (Environment.GetEnvironmentVariable("ONESHADER_SPV_DUMP") is { Length: > 0 } dump)
        {
            File.WriteAllBytes(Path.Combine(dump, c.Name + ".spv"), bytes!);
        }

        return bytes!;
    }

    private static string WorkDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "oneshader-spv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void PassesSpirvVal(string name)
    {
        string? val = Oracles.Find("spirv-val");
        Assert.SkipUnless(val is not null, "spirv-val is not installed");
        var bytes = Compile(Get(name));
        string dir = WorkDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "t.spv"), bytes);
            var (code, output) = Oracles.Run(val!, ["--target-env", "vulkan1.2", "t.spv"], dir);
            Assert.True(code == 0, "spirv-val rejected " + name + ":\n" + output + "\n" + Disassemble(dir) );
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static string Disassemble(string dir)
    {
        string? dis = Oracles.Find("spirv-dis");
        return dis is null ? "" : Oracles.Run(dis, ["t.spv"], dir).Output;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void OutputIsDeterministic(string name)
    {
        var c = Get(name);
        var first = Compile(c);
        var second = Compile(Get(name));
        var third = Compile(c);
        Assert.Equal(first, second);
        Assert.Equal(first, third);
    }

    [Fact]
    public void ReportsMissingEntryPoint()
    {
        var c = SpirvTestModules.SpriteVertex();
        var diagnostics = new DiagnosticBag();
        Assert.Null(SpirvBackend.Emit(c.Module, "Nope", null, diagnostics));
        var d = Assert.Single(diagnostics.Items);
        Assert.Equal("OSH5001", d.Code);
    }

    [Fact]
    public void UnsupportedIntrinsicIsADiagnosticNotAnException()
    {
        var c = SpirvTestModules.FullscreenTriangle();
        var f = c.Module.EntryPoints[0].Function;
        var b = new IrBuilder(f) { Block = f.Entry };
        // Insert a wave intrinsic before the terminator by rebuilding the tail.
        var term = f.Entry!.Instructions[^1];
        f.Entry.Instructions.RemoveAt(f.Entry.Instructions.Count - 1);
        b.CallIntrinsic(Intrinsic.WaveGetLaneIndex, Types.ScalarType.UInt);
        f.Entry.Instructions.Add(term);
        var diagnostics = new DiagnosticBag();
        Assert.Null(SpirvBackend.Emit(c.Module, c.Entry, null, diagnostics));
        Assert.Contains(diagnostics.Items, x => x.Code == "OSH5002" && x.Message.Contains("WaveGetLaneIndex", StringComparison.Ordinal));
    }

    // ---- structural comparison with DXC ---------------------------------------------------------

    /// <summary>The decoration lines of a disassembly, with ids shown by name, sorted so ordering differences do not matter.</summary>
    internal static SortedSet<string> Decorations(string disassembly)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in disassembly.Split('\n'))
        {
            var line = Regex.Replace(raw.Trim(), @"\s+", " ");
            if (line.StartsWith("OpDecorate ", StringComparison.Ordinal) || line.StartsWith("OpMemberDecorate ", StringComparison.Ordinal))
            {
                set.Add(line);
            }
        }

        return set;
    }

    [Theory]
    [InlineData("sprite-vs")]
    [InlineData("sprite-ps")]
    [InlineData("fullscreen-vs")]
    [InlineData("loop-ps")]
    [InlineData("compute-structured")]
    [InlineData("matrix-packing")]
    [InlineData("compute-shared")]
    public void DecorationsMatchDxc(string name)
    {
        string? dxc = Oracles.Find("dxc");
        string? dis = Oracles.Find("spirv-dis");
        Assert.SkipUnless(dxc is not null && dis is not null, "dxc and spirv-dis are not installed");
        var c = Get(name);
        var ours = Compile(c);
        string dir = WorkDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "ours.spv"), ours);
            File.WriteAllText(Path.Combine(dir, "t.hlsl"), c.Hlsl);
            var (code, output) = Oracles.Run(dxc!, ["-spirv", "-fspv-target-env=vulkan1.2", "-T", c.Profile, "-E", c.Entry, "t.hlsl", "-Fo", "dxc.spv"], dir);
            Assert.True(code == 0, "dxc failed: " + output);
            string ourText = Oracles.Run(dis!, ["ours.spv"], dir).Output;
            string dxcText = Oracles.Run(dis!, ["dxc.spv"], dir).Output;
            var a = Decorations(ourText);
            var d = Decorations(dxcText);
            var onlyOurs = a.Except(d).ToList();
            var onlyDxc = d.Except(a).ToList();
            Assert.True(
                onlyOurs.Count == 0 && onlyDxc.Count == 0,
                name + " decorations differ.\n  only in ours:\n    " + string.Join("\n    ", onlyOurs) + "\n  only in DXC:\n    " + string.Join("\n    ", onlyDxc));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
