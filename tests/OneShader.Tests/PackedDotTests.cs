using System;
using System.IO;
using System.Linq;
using OneShader.Reflection;
using Xunit;

namespace OneShader.Tests;

/// <summary>Packed 8-bit dot products on all three targets, and WGSL's uniformity of the workgroup id.</summary>
public sealed class PackedDotTests
{
    private const string Dot = """
        [[vk::binding(0, 0)]] RWStructuredBuffer<int> S;
        [[vk::binding(1, 0)]] RWStructuredBuffer<uint> U;
        [shader("compute")] [numthreads(8, 1, 1)]
        void CSMain(uint id : SV_DispatchThreadID)
        {
            S[id] = dot4add_i8packed(U[id], 0x80808080u, S[id]);
            U[id] = dot4add_u8packed(U[id], 0x01020304u, 7u);
        }
        """;

    private static CompileResult Compile(string src, params CompileTarget[] targets) =>
        ShaderCompiler.Compile(src, "t.hlsl", new CompileOptions { Targets = targets.Length == 0 ? [CompileTarget.SpirV, CompileTarget.Msl, CompileTarget.Wgsl] : targets }, TestContext.Current.CancellationToken);

    private static string Describe(CompileResult r) => string.Join("\n", r.Diagnostics.Select(d => d.ToString()));

    private static string Text(CompileResult r, CompileTarget t) => r.Blobs.Single(b => b.Target == t).AsText();

    [Fact]
    public void CompilesOnEveryTargetAndReportsTheDeviceFeature()
    {
        var r = Compile(Dot);
        Assert.True(r.Success, Describe(r));
        Assert.Empty(r.Diagnostics);
        Assert.Equal(DeviceFeatures.IntegerDotProduct, r.Reflection!.EntryPoints[0].RequiredFeatures);

        string msl = Text(r, CompileTarget.Msl);
        Assert.Contains("as_type<char4>", msl, StringComparison.Ordinal);
        Assert.Contains("as_type<uchar4>", msl, StringComparison.Ordinal);

        string wgsl = Text(r, CompileTarget.Wgsl);
        Assert.StartsWith("requires packed_4x8_integer_dot_product;", wgsl, StringComparison.Ordinal);
        Assert.Contains("dot4I8Packed(", wgsl, StringComparison.Ordinal);
        Assert.Contains("dot4U8Packed(", wgsl, StringComparison.Ordinal);
    }

    [Fact]
    public void SpirvUsesSdotAndUdotWithTheCapabilitiesDxcDeclares()
    {
        string? val = Oracles.Find("spirv-val");
        string? dis = Oracles.Find("spirv-dis");
        Assert.SkipUnless(val is not null && dis is not null, "spirv-val and spirv-dis are not installed");
        var r = Compile(Dot, CompileTarget.SpirV);
        Assert.True(r.Success, Describe(r));
        string dir = Path.Combine(Path.GetTempPath(), "osh-dot4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "t.spv");
            File.WriteAllBytes(file, r.Blobs[0].ToArray());
            var (code, output) = Oracles.Run(val!, ["--target-env", "vulkan1.2", file], dir);
            Assert.True(code == 0, output);
            string text = Oracles.Run(dis!, [file], dir).Output.Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.Contains("OpCapability DotProduct\n", text, StringComparison.Ordinal);
            Assert.Contains("OpCapability DotProductInput4x8BitPacked\n", text, StringComparison.Ordinal);
            Assert.Contains("OpExtension \"SPV_KHR_integer_dot_product\"", text, StringComparison.Ordinal);
            Assert.Contains("OpSDot %int ", text, StringComparison.Ordinal);
            Assert.Contains("OpUDot %uint ", text, StringComparison.Ordinal);
            Assert.Contains("PackedVectorFormat4x8Bit", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ShadersWithoutItNeedNoFeatureAndNoRequires()
    {
        var r = Compile("""
            [[vk::binding(0, 0)]] RWStructuredBuffer<uint> U;
            [shader("compute")] [numthreads(8, 1, 1)]
            void CSMain(uint id : SV_DispatchThreadID) { U[id] = countbits(U[id]); }
            """);
        Assert.True(r.Success, Describe(r));
        Assert.Equal(DeviceFeatures.None, r.Reflection!.EntryPoints[0].RequiredFeatures);
        Assert.DoesNotContain("requires", Text(r, CompileTarget.Wgsl), StringComparison.Ordinal);
    }

    [Fact]
    public void WrongArgumentTypeIsADiagnosticOnTheArgument()
    {
        var r = Compile("""
            [[vk::binding(0, 0)]] RWStructuredBuffer<uint> U;
            [shader("compute")] [numthreads(8, 1, 1)]
            void CSMain(uint id : SV_DispatchThreadID)
            {
                U[id] = dot4add_u8packed(uint2(1, 2), 3u, 0u);
            }
            """);
        Assert.False(r.Success);
        var d = r.Diagnostics.First(x => x.Message.Contains("dot4add_u8packed", StringComparison.Ordinal));
        Assert.Equal(("t.hlsl", 5, 30), (d.Span.Path, d.Span.Line, d.Span.Column));
        Assert.Single(r.Diagnostics);
    }

    private const string Uniform = """
        cbuffer C : register(b0) { uint RowCount; };
        [[vk::binding(1, 0)]] RWStructuredBuffer<uint> Out;
        groupshared uint sh[64];
        [shader("compute")] [numthreads(64, 1, 1)]
        void CSMain(uint3 gid : SV_GroupID, uint3 dtid : SV_DispatchThreadID, uint tid : SV_GroupIndex)
        {
            if (%COND% >= RowCount) return;
            sh[tid] = tid;
            GroupMemoryBarrierWithGroupSync();
            Out[tid] = sh[63 - tid] + gid.y;
        }
        """;

    [Fact]
    public void WorkgroupIdIsReadFromTheParameterSoTheBarrierStaysUniform()
    {
        var r = Compile(Uniform.Replace("%COND%", "gid.x", StringComparison.Ordinal), CompileTarget.Wgsl);
        Assert.True(r.Success, Describe(r));
        string w = Text(r, CompileTarget.Wgsl);
        string entry = w[w.IndexOf("fn CSMain", StringComparison.Ordinal)..];
        Assert.Matches(@"let \w+ = in_\w*gid;", entry);
        Assert.DoesNotMatch(@"let \w+ = CSMain_gid;", entry);
    }

    [Fact]
    public void ThreadIdsStayCopiedThroughThePrivateVariableAndAreNotPassedOffAsUniform()
    {
        var r = Compile(Uniform.Replace("%COND%", "dtid.x", StringComparison.Ordinal), CompileTarget.Wgsl);
        Assert.True(r.Success, Describe(r));
        string w = Text(r, CompileTarget.Wgsl);
        string entry = w[w.IndexOf("fn CSMain", StringComparison.Ordinal)..];
        Assert.Matches(@"let \w+ = CSMain_dtid;", entry);
        Assert.DoesNotMatch(@"let \w+ = in_\w*dtid;", entry);
    }
}
