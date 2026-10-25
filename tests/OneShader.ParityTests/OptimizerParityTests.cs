using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace OneShader.ParityTests;

/// <summary>
/// Optimizer stress shaders (tests/OneShader.ParityTests/OptimizerCases): control flow and call shapes the
/// optimizer rewrites. Each must give the same bytes with the optimizer on, with it off, and from the reference compiler.
/// </summary>
public class OptimizerParityTests
{
    private static string CaseDir([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "OptimizerCases");

    public static TheoryData<string> Cases()
    {
        var d = new TheoryData<string>();
        foreach (string p in Directory.GetFiles(CaseDir(), "*.case").Order(System.StringComparer.Ordinal))
        {
            d.Add(Path.GetFileNameWithoutExtension(p));
        }

        return d;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void OptimizedMatchesUnoptimizedAndReference(string name)
    {
        ToolGate.RequireDxc();
        var ctx = Gpu.RequireOrSkip();
        var model = CaseFile.Load(Path.Combine(CaseDir(), name + ".case"));
        var on = OneShaderCompiler.Create("oneshader (optimizer on)", true);
        var off = OneShaderCompiler.Create("oneshader (optimizer off)", false);
        var vsReference = Parity.Compare(ctx, model, ParityCompilers.Reference, on);
        var vsOff = Parity.Compare(ctx, model, off, on);
        var offVsReference = Parity.Compare(ctx, model, ParityCompilers.Reference, off);
        Assert.True(
            vsReference.Failures.Count == 0 && vsOff.Failures.Count == 0 && offVsReference.Failures.Count == 0,
            "against the reference compiler:\n" + vsReference + "\nagainst the unoptimized build:\n" + vsOff + "\nunoptimized against the reference compiler:\n" + offVsReference);
    }
}
