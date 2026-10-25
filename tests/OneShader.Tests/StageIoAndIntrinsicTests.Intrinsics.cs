using System;
using System.Linq;
using System.Threading;
using OneShader.Diagnostics;
using Xunit;

namespace OneShader.Tests;

public sealed partial class StageIoAndIntrinsicTests
{
    // ------------------------------------------------------------------ intrinsics

    [Theory]
    [InlineData("return float4(faceforward(n, v, r), 1);", "FaceForward")]
    [InlineData("return float4(faceforward(n.xy, v.xy, r.xy), 0, 1);", "FaceForward")]
    [InlineData("return faceforward(n.x, v.x, r.x).xxxx;", "FaceForward")]
    [InlineData("return float4(f16tof32(asuint(v.x)), f16tof32(uint2(1, 2)), 1);", "UnpackHalf2x16")]
    [InlineData("return asfloat(uint4(f32tof16(v.x), f32tof16(v.xy), 7));", "PackHalf2x16")]
    [InlineData("return float4(log10(n + 2.0), log10(v.x + 3.0));", "Log2")]
    [InlineData("return float4(log10(float2(n.x + 2, 1)), 0, 0);", "Log2")]
    public void IntrinsicsMatchDxc(string body, string extInst)
    {
        string src = "float4 PS(float3 v : TEXCOORD0, float3 n : TEXCOORD1, float3 r : TEXCOORD2) : SV_Target0 { " + body + " }\n";
        string dis = Disassemble(Valid(src, "PS", ShaderStage.Pixel));
        string dxc = DxcDisassemble(src, "PS", "ps_6_0");
        Assert.Equal(Count(dxc, "GLSL_std_450 " + extInst), Count(dis, "GLSL_std_450 " + extInst));
        Assert.Contains(extInst, dis, StringComparison.Ordinal);
    }

    [Fact]
    public void HalfConversionsCoverTheirRange()
    {
        // The probe's own shape: packed halves unpacked in a vertex shader.
        const string Src = """
            float4 VS(float3 p : POSITION, uint2 packed : NORMAL0) : SV_Position
            {
                float3 n = float3(f16tof32(packed.x & 0xFFFF), f16tof32(packed.x >> 16), f16tof32(packed.y));
                uint bits = f32tof16(n.x);
                return float4(p + n * bits, 1);
            }
            """;
        Valid(Src, "VS", ShaderStage.Vertex);
    }

    [Fact]
    public void HalfConversionRejectsNonVectors()
    {
        string src = "float4 PS(float2x2 m : TEXCOORD0) : SV_Target0 { return f32tof16(m)[0].xxxx; }\n";
        var r = Compile(src, "PS", ShaderStage.Pixel);
        Assert.Contains(r.Diagnostics, d => d.Message.Contains("needs a scalar or vector", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("lit")]
    [InlineData("noise")]
    [InlineData("msad4")]
    public void LegacyIntrinsicsStayUnsupportedWithADiagnostic(string name)
    {
        string src = "float4 PS(float4 v : TEXCOORD0) : SV_Target0 { return " + name + "(v, v, v).xxxx; }\n";
        var r = Compile(src, "PS", ShaderStage.Pixel);
        Assert.Contains(r.Diagnostics, d => d.Code == "OSH0350" && d.Message.Contains(name, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ compute stores of bool

    [Fact]
    public void BoolStoresAndLoadsInAReadWriteStructuredBuffer()
    {
        const string Src = """
            struct P { bool alive; float x; bool2 q; };
            [[vk::binding(0, 0)]] RWStructuredBuffer<P> Items;
            [[vk::binding(1, 0)]] RWStructuredBuffer<bool> Flags;
            [numthreads(8, 1, 1)] void CS(uint3 id : SV_DispatchThreadID)
            {
                P p = Items[id.x];
                p.alive = !p.alive;
                p.q = p.q.yx;
                Items[id.x] = p;
                Flags[id.x] = p.alive && p.q.x;
            }
            """;
        string dis = Disassemble(Valid(Src, "CS", ShaderStage.Compute));
        Assert.DoesNotContain("OpTypePointer StorageBuffer %bool", dis, StringComparison.Ordinal);
        Assert.Equal(Strides(DxcDisassemble(Src, "CS", "cs_6_0")), Strides(dis));
    }

    // ------------------------------------------------------------------ the compiler never throws

    private const string TinyShader = "float4 PS() : SV_Target0 { return 0; }\n";

    [Fact]
    public void AnInternalFailureBecomesOneLocatedDiagnostic()
    {
        var options = new CompileOptions { EntryPoints = [new EntryPointRequest("PS", ShaderStage.Pixel)] };
        var r = ShaderCompiler.CompileGuarded(TinyShader, "dir/tiny.hlsl", options, _ => throw new InvalidOperationException("forced failure"), CancellationToken.None);
        var d = Assert.Single(r.Diagnostics);
        Assert.Equal("OSH7000", d.Code);
        Assert.Equal(ApiDiagnosticCodes.InternalError, d.Code);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal(("dir/tiny.hlsl", 1, 1), (d.Span.Path, d.Span.Line, d.Span.Column));
        Assert.Contains("InvalidOperationException: forced failure", d.Message, StringComparison.Ordinal);
        Assert.Contains("please report", d.Message, StringComparison.Ordinal);
        Assert.False(r.Success);
        Assert.Empty(r.Blobs);
        Assert.Null(r.Reflection);
    }

    [Fact]
    public void CancellationStillPropagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => ShaderCompiler.Compile(TinyShader, "t.hlsl", null, cts.Token));
        var options = new CompileOptions { EntryPoints = [new EntryPointRequest("PS", ShaderStage.Pixel)] };
        using var late = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => ShaderCompiler.CompileGuarded(TinyShader, "t.hlsl", options, _ => late.Cancel(), late.Token));
    }

    [Fact]
    public void AThrowingIncludeResolverBecomesADiagnostic()
    {
        var options = new CompileOptions
        {
            IncludeResolver = (_, _, _) => throw new InvalidOperationException("resolver failed"),
        };
        var r = ShaderCompiler.Compile("#include \"x.hlsli\"\n" + TinyShader, "t.hlsl", options, TestContext.Current.CancellationToken);
        Assert.False(r.Success);
        Assert.Contains(r.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains("resolver failed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("float4 a[60000]")]
    [InlineData("float4x4 a[1000][1000]")]
    public void HugeStageArraysAreRejectedQuickly(string declaration)
    {
        string src = "struct O { float4 p : SV_Position; " + declaration + " : TEXCOORD0; };\nO VS(float4 p : POSITION) { O o; o.p = p; return o; }\n";
        var r = Compile(src, "VS", ShaderStage.Vertex);
        var d = Assert.Single(r.Diagnostics);
        Assert.Equal("OSH0321", d.Code);
        Assert.Contains("more than 1024", d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnexpectedExceptionHasADistinctStableCode()
    {
        // The codes of the compiler-bug diagnostics: the front end's own (OSH0399) and the API-level net (OSH7000).
        Assert.NotEqual("OSH0399", ApiDiagnosticCodes.InternalError);
        Assert.Matches(@"^OSH\d{4}$", ApiDiagnosticCodes.InternalError);
        Assert.Equal(["OSH0001", "OSH0002", "OSH6001", "OSH7000"], new[]
        {
            ApiDiagnosticCodes.NotImplemented,
            ApiDiagnosticCodes.TargetNotAvailable,
            ApiDiagnosticCodes.OptimizerFallback,
            ApiDiagnosticCodes.InternalError,
        }.OrderBy(c => c, StringComparer.Ordinal).ToArray());
    }
}
