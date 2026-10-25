using System.IO;
using System.Linq;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace OneShader.ParityTests;

/// <summary>Every corpus shader entry point compiles with the reference compiler and passes spirv-val for Vulkan 1.2.</summary>
public class CorpusCompileTests
{
    [Theory]
    [MemberData(nameof(Corpus.Cases), MemberType = typeof(Corpus))]
    public void ReferenceOutputPassesSpirvVal(string name)
    {
        ToolGate.RequireDxc();
        ToolGate.RequireSpirvVal();
        var model = Corpus.Load(name);
        string source = Path.Combine(Tools.CorpusDir, model.Shader);
        foreach (var (entry, stage) in ParityCompilers.Entries(model))
        {
            byte[] spirv = ParityCompilers.Reference.Compile(new ShaderRequest(source, entry, stage, model.Defines));
            string file = Path.Combine(Path.GetTempPath(), $"oneshader-parity-{Path.GetRandomFileName()}.spv");
            File.WriteAllBytes(file, spirv);
            try
            {
                var (code, output) = Tools.Run(Tools.SpirvVal!, ["--target-env", "vulkan1.2", file]);
                Assert.True(code == 0, $"{name}/{entry} ({stage}): spirv-val failed:\n{output}");
            }
            finally
            {
                File.Delete(file);
            }
        }
    }
}

/// <summary>Runs on a Vulkan device with the validation layer on.</summary>
public class GpuParityTests
{
    [Theory]
    [MemberData(nameof(Corpus.Cases), MemberType = typeof(Corpus))]
    public void CandidateMatchesReference(string name)
    {
        ToolGate.RequireDxc();
        var ctx = Gpu.RequireOrSkip();
        KnownGaps.Check(name, () =>
        {
            ParityReport report;
            try
            {
                report = Parity.Compare(ctx, Corpus.Load(name), ParityCompilers.Reference, ParityCompilers.Candidate);
            }
            catch (System.Exception ex)
            {
                ParityTable.Record(name, "vs-dxc", ex);
                throw;
            }
            ParityTable.Record(name, "vs-dxc", report);
            Assert.True(report.Failures.Count == 0, report.ToString());
        });
    }

    [Theory]
    [MemberData(nameof(Corpus.ComputeCases), MemberType = typeof(Corpus))]
    public void ComputeMatchesCpuResult(string name)
    {
        ToolGate.RequireDxc();
        var ctx = Gpu.RequireOrSkip();
        var model = Corpus.Load(name);
        Assert.NotEmpty(model.Expects);
        KnownGaps.Check(name, () =>
        {
            ParityReport report;
            try
            {
                report = Parity.CheckExpected(ctx, model, ParityCompilers.Candidate);
            }
            catch (System.Exception ex)
            {
                ParityTable.Record(name, "vs-cpu", ex);
                throw;
            }
            ParityTable.Record(name, "vs-cpu", report);
            Assert.True(report.Failures.Count == 0, report.ToString());
        });
    }
}

/// <summary>Checks on the reference (DXC) side alone, so a bad case is caught before it can blame a compiler.</summary>
public class ReferenceSanityTests
{
    /// <summary>The reference run raises no validation message and every output holds more than one distinct value.</summary>
    [Theory]
    [MemberData(nameof(Corpus.Cases), MemberType = typeof(Corpus))]
    public void ReferenceRunIsCleanAndNonTrivial(string name)
    {
        ToolGate.RequireDxc();
        var ctx = Gpu.RequireOrSkip();
        var model = Corpus.Load(name);
        var data = CaseData.Build(model);
        var run = Parity.Run(ctx, model, data, ParityCompilers.Reference);
        Parity.DumpImages(model, run, "dxc");
        Assert.True(run.ValidationMessages.Count == 0, "validation layer (dxc): " + string.Join("\n", run.ValidationMessages));
        foreach (var o in run.Outputs)
        {
            if (o.Key == "target" && model.Graphics is { PixelEntry.Length: 0 })
            {
                continue; // a depth-only draw leaves the colour target alone
            }
            Assert.True(o.Bytes.Distinct().Count() > 1, $"{name} {o.Key}: the reference output is a single repeated byte, so the case proves nothing");
        }
    }

    /// <summary>The CPU reference of every compute case agrees with DXC's binary, so the CPU code itself is checked.</summary>
    [Theory]
    [MemberData(nameof(Corpus.ComputeCases), MemberType = typeof(Corpus))]
    public void ReferenceMatchesCpuResult(string name)
    {
        ToolGate.RequireDxc();
        var ctx = Gpu.RequireOrSkip();
        var report = Parity.CheckExpected(ctx, Corpus.Load(name), ParityCompilers.Reference);
        ParityTable.Record(name, "dxc-vs-cpu", report);
        Assert.True(report.Failures.Count == 0, report.ToString());
    }
}

/// <summary>The comparisons must be able to fail: a deliberately different compile has to be caught.</summary>
public class SelfCheckTests
{
    [Fact]
    public void DifferentBufferResultIsDetected()
    {
        ToolGate.RequireDxc();
        var ctx = Gpu.RequireOrSkip();
        var changed = DxcCompiler.Create("dxc with weaker gravity", "PARTICLE_GRAVITY=3.0");
        var report = Parity.Compare(ctx, Corpus.Load("particles"), ParityCompilers.Reference, changed);
        Assert.Contains(report.Failures, f => f.Contains("differ in", System.StringComparison.Ordinal) && f.Contains("buffer:0.1", System.StringComparison.Ordinal));
        Assert.DoesNotContain(report.Failures, f => f.StartsWith("validation layer", System.StringComparison.Ordinal));
    }

    [Fact]
    public void DifferentImageResultIsDetectedBeyondTolerance()
    {
        ToolGate.RequireDxc();
        var ctx = Gpu.RequireOrSkip();
        var changed = DxcCompiler.Create("dxc with wider anti-aliasing", "UI_AA_WIDTH=3.0");
        var report = Parity.Compare(ctx, Corpus.Load("ui_rect"), ParityCompilers.Reference, changed);
        Assert.Contains(report.Failures, f => f.Contains("ui_rect target", System.StringComparison.Ordinal) && f.Contains("differ in", System.StringComparison.Ordinal));
        Assert.True(Directory.Exists(Path.Combine(Tools.ArtifactsDir, "ui_rect")), "mismatching images are saved");
    }

    [Fact]
    public void CpuReferenceCatchesAWrongShader()
    {
        ToolGate.RequireDxc();
        var ctx = Gpu.RequireOrSkip();
        var changed = DxcCompiler.Create("dxc with weaker gravity", "PARTICLE_GRAVITY=3.0");
        var report = Parity.CheckExpected(ctx, Corpus.Load("particles"), changed);
        Assert.Contains(report.Failures, f => f.Contains("differs from the CPU result", System.StringComparison.Ordinal));
    }

    [Fact]
    public void ValidationLayerReportsMisuse()
    {
        ToolGate.RequireDxc();
        var ctx = Gpu.RequireOrSkip();
        TestContext.Current.SendDiagnosticMessage("device: " + ctx.DeviceName + ", validation layer: " + ctx.ValidationEnabled);
        Assert.SkipUnless(ctx.ValidationEnabled, "validation disabled by ONESHADER_VK_NO_VALIDATION");

        // Bind the input buffer as a uniform buffer although the shader declares a storage buffer:
        // the layer must object, and the run must hand the message back.
        var model = CaseFile.Parse(File.ReadAllText(Path.Combine(Tools.CorpusDir, "cases", "prefix_sum.case"))
            .Replace("storage 0 0 :", "uniform 0 0 :", System.StringComparison.Ordinal), "prefix_sum-misuse");
        var run = Parity.Run(ctx, model, CaseData.Build(model), ParityCompilers.Reference);
        Assert.NotEmpty(run.ValidationMessages);
    }
}
