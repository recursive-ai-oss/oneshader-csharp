using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OneShader.IR;
using OneShader.Semantics;
using Xunit;

namespace OneShader.Tests;

public class FrontendCorpusTests
{
    internal static FrontendResult Run(string text, string name = "a.hlsl", CompileOptions? options = null) =>
        Frontend.Run(text, name, options ?? new CompileOptions());

    public static IEnumerable<object[]> GraphicsFiles() => CorpusFiles.Shaders("2d").Select(p => new object[] { p });

    [Theory]
    [MemberData(nameof(GraphicsFiles))]
    public void GraphicsCorpusCompilesToVerifiedIr(string path)
    {
        var r = Run(File.ReadAllText(path), Path.GetFileName(path));
        Assert.Empty(r.Diagnostics.Items);
        Assert.NotNull(r.Module);
        Assert.Empty(IrVerifier.Verify(r.Module!));
        Assert.NotNull(r.Reflection);
        string a = IrPrinter.Print(r.Module!);
        string b = IrPrinter.Print(Run(File.ReadAllText(path), Path.GetFileName(path)).Module!);
        Assert.Equal(a, b);
        if (Environment.GetEnvironmentVariable("ONESHADER_DUMP") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + ".ir"), a);
        }
    }

    [Theory]
    [MemberData(nameof(GraphicsFiles))]
    public void GraphicsCorpusIrPassesTheSpirvBackendAndSpirvVal(string path)
    {
        string? val = Oracles.Find("spirv-val");
        Assert.SkipUnless(val is not null, "spirv-val is not installed");
        var r = Run(File.ReadAllText(path), Path.GetFileName(path));
        Assert.NotNull(r.Module);
        string dir = Path.Combine(Path.GetTempPath(), "osh-u4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var ep in r.Module!.EntryPoints)
            {
                var bag = new OneShader.Diagnostics.DiagnosticBag();
                var bytes = OneShader.SpirV.SpirvBackend.Emit(r.Module, ep.Function.Name, null, bag);
                Assert.True(bytes is not null, string.Join("\n", bag.Items.Select(d => d.ToString())));
                string file = Path.Combine(dir, ep.Function.Name + ".spv");
                File.WriteAllBytes(file, bytes!);
                var (code, output) = Oracles.Run(val!, ["--target-env", "vulkan1.2", file], dir);
                Assert.True(code == 0, Path.GetFileName(path) + " " + ep.Function.Name + ": " + output);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
