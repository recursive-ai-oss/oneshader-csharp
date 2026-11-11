using System;
using System.Linq;
using Xunit;

namespace OneShader.ParityTests;

/// <summary>
/// Cooperative-matrix and bfloat16 kernels executed on a Vulkan device and compared with a CPU result, with the validation layer on.
/// DXC cannot compile this surface, so there is no second compiler to compare with. Devices without VK_KHR_cooperative_matrix (or
/// without the needed shape or element type) skip with the properties they report.
/// </summary>
public class CooperativeMatrixGpuTests
{
    public static TheoryData<string> Names()
    {
        var d = new TheoryData<string>();
        foreach (string n in Corpus.CpuOnlyNames)
        {
            d.Add(n);
        }
        return d;
    }

    [Fact]
    public void CpuOnlyCasesParse()
    {
        Assert.Equal(["coop_bf16", "coop_f16", "coop_layouts"], Corpus.CpuOnlyNames);
        foreach (string n in Corpus.CpuOnlyNames)
        {
            var model = Corpus.LoadCpuOnly(n);
            Assert.NotEmpty(model.Expects);
            foreach (var e in model.Expects)
            {
                Assert.NotEmpty(CpuReferences.Compute(e.Name, model, CaseData.Build(model)));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void KernelMatchesCpuResult(string name)
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("ONESHADER_PARITY_TARGET") is { Length: > 0 }, "SPIR-V candidate only");
        var ctx = Gpu.RequireOrSkip();
        var shapes = ctx.CooperativeShapes();
        string have = ctx.DeviceName + " cooperative matrix properties: " + (shapes.Count == 0 ? "none" : string.Join("; ", shapes));
        Assert.SkipWhen(!ctx.CooperativeMatrixFeature, "no VK_KHR_cooperative_matrix with the Vulkan memory model on " + have);
        bool bf16 = name.Contains("bf16", StringComparison.Ordinal);
        uint input = bf16 ? CoopShape.BFloat16 : CoopShape.Float16;
        Assert.SkipWhen(bf16 && !ctx.BFloat16CooperativeMatrixFeature, "no bfloat16 cooperative matrices on " + have);
        Assert.SkipWhen(!shapes.Any(s => s.M == 16 && s.N == 16 && s.K == 16 && s.AType == input && s.BType == input && s.CType == CoopShape.Float32 && s.ResultType == CoopShape.Float32),
            "no 16x16x16 " + (bf16 ? "bf16" : "f16") + " with f32 accumulator on " + have);
        var report = Parity.CheckExpected(ctx, Corpus.LoadCpuOnly(name), ParityCompilers.Candidate);
        Assert.True(report.Failures.Count == 0, have + "\n" + report);
    }
}
