using System;
using System.IO;
using System.Linq;
using OneShader.Layouts;
using OneShader.Reflection;
using Xunit;

namespace OneShader.Tests;

/// <summary>Wave (subgroup) and quad intrinsics on all three targets: output shape, validity, reflection and diagnostics.</summary>
public sealed class WaveTests
{
    private const string AllOps = """
        [[vk::binding(0, 0)]] RWStructuredBuffer<uint> Out;
        [[vk::binding(1, 0)]] RWStructuredBuffer<float4> OutF;
        uint Helper(uint x) { return WaveGetLaneIndex() + x; }
        [shader("compute")] [numthreads(64, 1, 1)]
        void CSMain(uint id : SV_DispatchThreadID)
        {
            bool p = id > 3;
            uint r = Helper(WaveGetLaneCount()) + (WaveIsFirstLane() ? 1u : 0u);
            r += (WaveActiveAnyTrue(p) ? 1u : 0u) + (WaveActiveAllTrue(p) ? 1u : 0u) + (WaveActiveAllEqual(id) ? 1u : 0u);
            uint4 b = WaveActiveBallot(p);
            r += b.x + WaveActiveCountBits(p) + WavePrefixCountBits(p);
            r += WaveActiveSum(id) + WaveActiveProduct(id) + WaveActiveMin(id) + WaveActiveMax(id);
            r += WaveActiveBitAnd(id) + WaveActiveBitOr(id) + WaveActiveBitXor(id);
            r += WavePrefixSum(id) + WavePrefixProduct(id) + WaveReadLaneAt(id, 3) + WaveReadLaneAt(id, id & 7) + WaveReadLaneFirst(id);
            r += (uint)WaveActiveSum((int)id) + (uint)WaveActiveMin((int)id);
            float f = (float)id;
            OutF[id] = float4(WaveActiveSum(f) + WavePrefixSum(f), WaveActiveMax(f), WaveActiveMin(f) + QuadReadAcrossX(f), QuadReadAcrossY(f) + QuadReadAcrossDiagonal(f) + QuadReadLaneAt(f, id & 3));
            OutF[id + 64] = float4(WaveActiveSum(float3(f, f, f)), 0) + float4(WaveActiveAllEqual(float2(f, 1.0)), 0, 0);
            Out[id] = r;
        }
        """;

    private static CompileResult Compile(string src, params CompileTarget[] targets) =>
        ShaderCompiler.Compile(src, "w.hlsl", new CompileOptions { Targets = targets.Length == 0 ? [CompileTarget.SpirV, CompileTarget.Msl, CompileTarget.Wgsl] : targets }, TestContext.Current.CancellationToken);

    private static string Describe(CompileResult r) => string.Join("\n", r.Diagnostics.Select(d => d.ToString()));

    private static string Text(CompileResult r, CompileTarget t) => r.Blobs.Single(b => b.Target == t).AsText();

    [Fact]
    public void EveryIntrinsicCompilesOnEveryTarget()
    {
        var r = Compile(AllOps);
        Assert.True(r.Success, Describe(r));
        Assert.Empty(r.Diagnostics);
        Assert.Equal(3, r.Blobs.Count);
    }

    [Fact]
    public void SpirvIsValidAndUsesTheGroupNonUniformCapabilities()
    {
        string? val = Oracles.Find("spirv-val");
        string? dis = Oracles.Find("spirv-dis");
        Assert.SkipUnless(val is not null && dis is not null, "spirv-val and spirv-dis are not installed");
        var r = Compile(AllOps, CompileTarget.SpirV);
        Assert.True(r.Success, Describe(r));
        string dir = Path.Combine(Path.GetTempPath(), "osh-wave-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "w.spv");
            File.WriteAllBytes(file, r.Blobs[0].ToArray());
            var (code, output) = Oracles.Run(val!, ["--target-env", "vulkan1.2", file], dir);
            Assert.True(code == 0, output);
            string text = Oracles.Run(dis!, [file], dir).Output;
            foreach (string cap in new[] { "GroupNonUniform", "GroupNonUniformVote", "GroupNonUniformArithmetic", "GroupNonUniformBallot", "GroupNonUniformShuffle", "GroupNonUniformQuad" })
            {
                Assert.Contains("OpCapability " + cap + "\n", text, StringComparison.Ordinal);
            }

            // Lane index and count come from the two built-ins, read once by the entry point.
            Assert.Contains("BuiltIn SubgroupLocalInvocationId", text, StringComparison.Ordinal);
            Assert.Contains("BuiltIn SubgroupSize", text, StringComparison.Ordinal);
            // DXC's choices: a shuffle for WaveReadLaneAt, a ballot bit count for the count-bits family, a quad swap for QuadReadAcross.
            Assert.Contains("OpGroupNonUniformShuffle", text, StringComparison.Ordinal);
            Assert.Contains("OpGroupNonUniformBallotBitCount", text, StringComparison.Ordinal);
            Assert.Contains("OpGroupNonUniformQuadSwap", text, StringComparison.Ordinal);
            Assert.Contains("ExclusiveScan", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void MslUsesSimdAndQuadGroupFunctionsAndTheLaneAttributes()
    {
        var r = Compile(AllOps, CompileTarget.Msl);
        Assert.True(r.Success, Describe(r));
        string msl = Text(r, CompileTarget.Msl);
        foreach (string expected in new[]
        {
            "[[thread_index_in_simdgroup]]", "[[threads_per_simdgroup]]", "simd_is_first()", "simd_any(", "simd_all(", "simd_ballot(",
            "simd_sum(", "simd_product(", "simd_min(", "simd_max(", "simd_and(", "simd_or(", "simd_xor(", "simd_prefix_exclusive_sum(",
            "simd_prefix_exclusive_product(", "simd_shuffle(", "simd_broadcast_first(", "quad_shuffle_xor(", "quad_shuffle(",
        })
        {
            Assert.Contains(expected, msl, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WgslEnablesSubgroupsAndUsesTheSubgroupFunctions()
    {
        var r = Compile(AllOps, CompileTarget.Wgsl);
        Assert.True(r.Success, Describe(r));
        string wgsl = Text(r, CompileTarget.Wgsl);
        Assert.StartsWith("enable subgroups;\n", wgsl, StringComparison.Ordinal);
        foreach (string expected in new[]
        {
            "@builtin(subgroup_invocation_id)", "@builtin(subgroup_size)", "subgroupElect()", "subgroupAny(", "subgroupAll(", "subgroupBallot(",
            "subgroupAdd(", "subgroupMul(", "subgroupMin(", "subgroupMax(", "subgroupAnd(", "subgroupOr(", "subgroupXor(",
            "subgroupExclusiveAdd(", "subgroupExclusiveMul(", "subgroupShuffle(", "subgroupBroadcastFirst(", "quadSwapX(", "quadSwapY(",
            "quadSwapDiagonal(", "quadBroadcast(",
        })
        {
            Assert.Contains(expected, wgsl, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ShadersWithoutWaveOperationsStayFree()
    {
        var r = Compile("[shader(\"compute\")] [numthreads(1,1,1)] void CS() { }\n");
        Assert.True(r.Success, Describe(r));
        Assert.False(r.Reflection!.UsesSubgroups);
        Assert.Equal(SubgroupOperations.None, r.Reflection.EntryPoints[0].SubgroupOperations);
        Assert.DoesNotContain("subgroup", Text(r, CompileTarget.Wgsl), StringComparison.Ordinal);
        Assert.DoesNotContain("simd", Text(r, CompileTarget.Msl), StringComparison.Ordinal);
    }

    [Fact]
    public void ReflectionRecordsTheOperationsPerEntryPoint()
    {
        const string Src = """
            [[vk::binding(0, 0)]] RWStructuredBuffer<uint> Out;
            uint Reduce(uint v) { return WaveActiveSum(v); }
            [shader("compute")] [numthreads(32, 1, 1)] void Plain(uint id : SV_DispatchThreadID) { Out[id] = id; }
            [shader("compute")] [numthreads(32, 1, 1)] void Reducing(uint id : SV_DispatchThreadID) { Out[id] = Reduce(id); }
            [shader("compute")] [numthreads(32, 1, 1)] void Everything(uint id : SV_DispatchThreadID)
            {
                Out[id] = WaveReadLaneAt(id, 1) + WaveActiveBallot(id > 2).x + (WaveActiveAnyTrue(id > 1) ? 1u : 0u) + WaveGetLaneIndex() + (uint)QuadReadAcrossX((float)id);
            }
            """;
        var r = Compile(Src, CompileTarget.SpirV);
        Assert.True(r.Success, Describe(r));
        var e = r.Reflection!.EntryPoints;
        Assert.Equal(SubgroupOperations.None, e[0].SubgroupOperations);
        Assert.False(e[0].UsesSubgroups);
        Assert.Equal(SubgroupOperations.Arithmetic, e[1].SubgroupOperations);
        Assert.Equal(
            SubgroupOperations.Basic | SubgroupOperations.Vote | SubgroupOperations.Ballot | SubgroupOperations.Shuffle | SubgroupOperations.Quad,
            e[2].SubgroupOperations);
        Assert.True(r.Reflection.UsesSubgroups);

        string text = ReflectionFormat.Write(r.Reflection);
        Assert.True(ReflectionFormat.TryRead(text, out var back, out string? error), error);
        Assert.Equal(text, ReflectionFormat.Write(back!));
        Assert.Equal(e[2].SubgroupOperations, back!.EntryPoints[2].SubgroupOperations);
        Assert.DoesNotContain("subgroup", text.Split('\n').First(l => l.Contains("\"Plain\"", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public void HelperFunctionsSeeTheLaneIndexOfTheEntryThatCallsThem()
    {
        // Two entry points share a helper that reads the lane index; each must get its own copy of the built-in.
        const string Src = """
            [[vk::binding(0, 0)]] RWStructuredBuffer<uint> Out;
            uint Where(uint x) { return x * 100u + WaveGetLaneIndex(); }
            [shader("compute")] [numthreads(32, 1, 1)] void A(uint id : SV_DispatchThreadID) { Out[id] = Where(1); }
            [shader("compute")] [numthreads(32, 1, 1)] void B(uint id : SV_DispatchThreadID) { Out[id] = Where(2); }
            """;
        foreach (bool optimize in new[] { false, true })
        {
            var r = ShaderCompiler.Compile(Src, "w.hlsl", new CompileOptions { Targets = [CompileTarget.SpirV, CompileTarget.Msl, CompileTarget.Wgsl], Optimize = optimize }, TestContext.Current.CancellationToken);
            Assert.True(r.Success, Describe(r));
            Assert.All(r.Reflection!.EntryPoints, x => Assert.Equal(SubgroupOperations.Basic, x.SubgroupOperations));
            Assert.Equal(6, r.Blobs.Count);
        }
    }

    [Fact]
    public void WaveMatchAndMultiPrefixAreDiagnosedAtTheCall()
    {
        const string Src = "[shader(\"compute\")] [numthreads(8,1,1)]\nvoid CS(uint id : SV_DispatchThreadID)\n{\n    uint4 m = WaveMatch(id);\n}\n";
        var r = Compile(Src);
        Assert.False(r.Success);
        var d = Assert.Single(r.Diagnostics);
        Assert.Equal(("OSH0350", "w.hlsl", 4, 15), (d.Code, d.Span.Path, d.Span.Line, d.Span.Column));
        Assert.Contains("WaveMatch", d.Message, StringComparison.Ordinal);

        var r2 = Compile("[shader(\"compute\")] [numthreads(8,1,1)]\nvoid CS(uint id : SV_DispatchThreadID)\n{\n    uint s = WaveMultiPrefixSum(id, uint4(1,0,0,0));\n}\n");
        var d2 = Assert.Single(r2.Diagnostics);
        Assert.Equal(("OSH0350", 4, 14), (d2.Code, d2.Span.Line, d2.Span.Column));
    }

    [Theory]
    [InlineData("WaveActiveSum((double)id)", "float, int and uint")]
    [InlineData("WaveActiveBitAnd(1.5)", "int and uint")]
    [InlineData("WaveActiveSum(float2x2(1,2,3,4))", "scalars and vectors")]
    [InlineData("WaveActiveAnyTrue(true2)", "single bool")]
    public void WrongArgumentTypesAreDiagnosed(string call, string expected)
    {
        string src = call.Contains("true2", StringComparison.Ordinal)
            ? "[shader(\"compute\")] [numthreads(8,1,1)]\nvoid CS(uint id : SV_DispatchThreadID)\n{\n    bool2 t = bool2(true, false);\n    bool b = WaveActiveAnyTrue(t);\n}\n"
            : "[shader(\"compute\")] [numthreads(8,1,1)]\nvoid CS(uint id : SV_DispatchThreadID)\n{\n    var x = " + call + ";\n}\n";
        var r = Compile(src.Replace("var x", "float x", StringComparison.Ordinal));
        Assert.False(r.Success);
        var d = r.Diagnostics.First(x => x.Severity == OneShader.Diagnostics.DiagnosticSeverity.Error);
        Assert.Equal("OSH0302", d.Code);
        Assert.Contains(expected, d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArgumentCountIsChecked()
    {
        var r = Compile("[shader(\"compute\")] [numthreads(8,1,1)]\nvoid CS(uint id : SV_DispatchThreadID)\n{\n    uint s = WaveReadLaneAt(id);\n}\n");
        var d = Assert.Single(r.Diagnostics);
        Assert.Equal(("OSH0303", 4, 14), (d.Code, d.Span.Line, d.Span.Column));
    }

    [Fact]
    public void VertexShadersKeepTheOperationsButNotTheLaneBuiltInsWhereTargetsLackThem()
    {
        const string Src = "[shader(\"vertex\")]\nfloat4 VS(uint v : SV_VertexID) : SV_Position\n{\n    return float4(WaveGetLaneIndex(), 0, 0, 1);\n}\n";
        Assert.True(Compile(Src, CompileTarget.SpirV).Success);
        var msl = Compile(Src, CompileTarget.Msl);
        Assert.False(msl.Success);
        Assert.Contains(msl.Diagnostics, d => d.Code == "OSH5102" && d.Message.Contains("vertex", StringComparison.Ordinal));
        var wgsl = Compile(Src, CompileTarget.Wgsl);
        Assert.False(wgsl.Success);
        Assert.Contains(wgsl.Diagnostics, d => d.Code == "OSH5202" && d.Message.Contains("vertex", StringComparison.Ordinal));

        const string Op = "[shader(\"vertex\")]\nfloat4 VS(uint v : SV_VertexID) : SV_Position\n{\n    return float4(WaveActiveSum((float)v), 0, 0, 1);\n}\n";
        Assert.True(Compile(Op, CompileTarget.SpirV, CompileTarget.Msl).Success);
        var op = Compile(Op, CompileTarget.Wgsl);
        Assert.Contains(op.Diagnostics, d => d.Code == "OSH5202" && d.Message.Contains("WaveActiveSum", StringComparison.Ordinal));
    }

    [Fact]
    public void WaveOperationsAreNotSpeculatedOrMergedAcrossBranches()
    {
        // The same reduction in the two arms of an if runs for different sets of lanes, so the optimizer must keep both.
        const string Src = """
            [[vk::binding(0, 0)]] RWStructuredBuffer<uint> Out;
            [shader("compute")] [numthreads(32, 1, 1)]
            void CS(uint id : SV_DispatchThreadID)
            {
                uint r;
                if ((id & 1) == 0) { r = WaveActiveSum(id); } else { r = WaveActiveSum(id) + 1; }
                bool c = id < 8;
                uint s = c ? WaveActiveSum(id) : 0u;
                Out[id] = r + s;
            }
            """;
        var r = Compile(Src, CompileTarget.Msl);
        Assert.True(r.Success, Describe(r));
        Assert.Equal(3, Text(r, CompileTarget.Msl).Split("simd_sum(", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void LayoutsCarryTheSubgroupRequirementToEveryHost()
    {
        const string Src = """
            [[vk::binding(0, 0)]] RWStructuredBuffer<uint> Out;
            [shader("compute")] [numthreads(32, 1, 1)] void Wave(uint id : SV_DispatchThreadID) { Out[id] = WaveActiveSum(id) + WaveReadLaneAt(id, 0); }
            [shader("compute")] [numthreads(32, 1, 1)] void Plain(uint id : SV_DispatchThreadID) { Out[id] = id; }
            """;
        var r = Compile(Src);
        Assert.True(r.Success, Describe(r));
        Assert.True(EntryLayout.TryCreate(r.Reflection!, "Wave", out var wave, out var error), error?.Message);
        Assert.True(EntryLayout.TryCreate(r.Reflection!, "Plain", out var plain, out error), error?.Message);
        Assert.Equal(SubgroupOperations.Arithmetic | SubgroupOperations.Shuffle, wave!.SubgroupOperations);
        Assert.Equal(SubgroupOperations.None, plain!.SubgroupOperations);

        var withWave = PipelineLayout.Create(wave);
        Assert.Equal(wave.SubgroupOperations, withWave.SubgroupOperations);
        Assert.Equal(wave.SubgroupOperations, withWave.ToVulkan().SubgroupOperations);
        Assert.True(withWave.TryToWebGpu(out var web, out error), error?.Message);
        Assert.Equal(["subgroups"], web!.RequiredFeatures);
        Assert.True(withWave.TryToMetal(out var metal, out error), error?.Message);
        Assert.Equal(wave.SubgroupOperations, metal!.SubgroupOperations);

        var without = PipelineLayout.Create(plain);
        Assert.Equal(SubgroupOperations.None, without.ToVulkan().SubgroupOperations);
        Assert.True(without.TryToWebGpu(out var plainWeb, out error), error?.Message);
        Assert.Empty(plainWeb!.RequiredFeatures!);
    }
}
