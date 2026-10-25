using System;
using System.IO;
using System.Linq;
using Xunit;

namespace OneShader.ParityTests;

/// <summary>
/// Single-construct shaders in <c>corpus/probes/</c>. Each has a second line <c>// probe: ENTRY ps|vs|cs [expect-error]</c>.
/// DXC must compile it and its output pass spirv-val; the candidate must then do the same, or, for
/// <c>expect-error</c> probes that DXC rejects, must report a diagnostic instead of emitting a module.
/// A probe that fails here is a gap in the candidate; the message says which kind.
/// </summary>
public class ProbeTests
{
    [Theory]
    [MemberData(nameof(Corpus.Probes), MemberType = typeof(Corpus))]
    public void CandidateBehavesLikeReference(string file)
    {
        ToolGate.RequireDxc();
        ToolGate.RequireSpirvVal();
        KnownGaps.Check(file, () => Run(file));
    }

    private static void Run(string file)
    {
        string path = Path.Combine(Tools.CorpusDir, "probes", file);
        string[] head = File.ReadLines(path).ElementAt(1).Replace("// probe:", "", StringComparison.Ordinal).Trim().Split(' ');
        var stage = head[1] switch { "vs" => ShaderStage.Vertex, "ps" => ShaderStage.Pixel, _ => ShaderStage.Compute };
        bool expectError = head.Contains("expect-error");
        var request = new ShaderRequest(path, head[0], stage, []);

        if (expectError)
        {
            Assert.ThrowsAny<Exception>(() => ParityCompilers.Reference.Compile(request));
            string outcome = "compiled although the reference rejects it";
            try
            {
                byte[] spirv = ParityCompilers.Candidate.Compile(request);
                Assert.Fail($"{file}: {outcome}; spirv-val says: {Validate(spirv)}");
            }
            catch (InvalidOperationException e) when (e.Message.StartsWith("OneShader failed", StringComparison.Ordinal))
            {
                // A diagnostic is the expected result.
            }
            return;
        }

        byte[] reference = ParityCompilers.Reference.Compile(request);
        Assert.True(Validate(reference).Length == 0, $"{file}: the reference output does not validate");
        byte[] candidate;
        try
        {
            candidate = ParityCompilers.Candidate.Compile(request);
        }
        catch (UnsupportedOnTargetException e)
        {
            Assert.Skip("unsupported on this target: " + e.Message);
            return;
        }

        string verdict = Validate(candidate);
        Assert.True(verdict.Length == 0, $"{file}: the candidate's module fails spirv-val: {verdict}");
    }

    private static string Validate(byte[] spirv)
    {
        string file = Path.Combine(Path.GetTempPath(), $"oneshader-probe-{Path.GetRandomFileName()}.spv");
        File.WriteAllBytes(file, spirv);
        try
        {
            var (code, output) = Tools.Run(Tools.SpirvVal!, ["--target-env", "vulkan1.2", file]);
            return code == 0 ? "" : output.Trim();
        }
        finally
        {
            File.Delete(file);
        }
    }
}
