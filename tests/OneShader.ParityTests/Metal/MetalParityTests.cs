using System;
using System.IO;
using System.Linq;
using Xunit;

namespace OneShader.ParityTests;

/// <summary>
/// The two machine-bound halves of the Metal parity suite (see <c>tools/run-metal-parity.sh</c>). Both do nothing unless
/// ONESHADER_METAL_BUNDLE names the exchange directory.
/// </summary>
public class MetalParityTests
{
    private static string? Bundle => Environment.GetEnvironmentVariable("ONESHADER_METAL_BUNDLE") is { Length: > 0 } d ? Path.GetFullPath(d) : null;

    /// <summary>Compiles every case to MSL and runs the DXC reference on Vulkan, writing the bundle the Metal runner consumes.</summary>
    [Fact]
    public void Export()
    {
        string? bundle = Bundle;
        Assert.SkipWhen(bundle is null, "ONESHADER_METAL_BUNDLE is not set");
        ToolGate.RequireDxc();
        var ctx = Gpu.RequireOrSkip();
        Directory.CreateDirectory(bundle!);
        string only = Environment.GetEnvironmentVariable("ONESHADER_METAL_CASES") ?? "";
        foreach (string name in Corpus.CaseNames.Where(n => only.Length == 0 || only.Split(',').Contains(n)))
        {
            string status;
            try
            {
                status = MetalBundle.Export(bundle!, Corpus.Load(name), ctx);
            }
            catch (Exception ex)
            {
                Directory.CreateDirectory(Path.Combine(bundle!, name));
                status = "reference-failed: " + ex.Message.Split('\n')[0];
            }
            File.WriteAllText(Path.Combine(bundle!, name, "status.txt"), status + "\n");
        }
    }

    /// <summary>Compares the Metal runner's outputs with the reference outputs and writes <c>table.txt</c> into the bundle.</summary>
    [Fact]
    public void Compare()
    {
        string? bundle = Bundle;
        Assert.SkipWhen(bundle is null, "ONESHADER_METAL_BUNDLE is not set");
        var (rows, table) = MetalCompare.CheckAll(bundle!);
        File.WriteAllText(Path.Combine(bundle!, "table.txt"), table);
        Assert.True(rows.All(r => r.Passed || r.Status == "not-compiled"), table);
    }
}
