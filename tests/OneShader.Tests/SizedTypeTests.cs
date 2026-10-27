using System;
using System.IO;
using System.Linq;
using OneShader.Diagnostics;
using OneShader.Layouts;
using OneShader.Reflection;
using Xunit;

namespace OneShader.Tests;

/// <summary>16-bit and 64-bit scalar types: diagnostics with positions, refusals per target, folding widths, reflection and layouts.</summary>
public sealed class SizedTypeTests
{
    private static CompileResult Compile(string src, bool sixteen, params CompileTarget[] targets) =>
        ShaderCompiler.Compile(src, "t.hlsl", new CompileOptions { Enable16BitTypes = sixteen, Targets = targets.Length == 0 ? [CompileTarget.SpirV] : targets }, TestContext.Current.CancellationToken);

    private static string Describe(CompileResult r) => string.Join("\n", r.Diagnostics.Select(d => d.ToString()));

    private static (string Code, int Line, int Column) First(CompileResult r, string code)
    {
        var d = r.Diagnostics.First(x => x.Code == code);
        return (d.Code, d.Span.Line, d.Span.Column);
    }

    private const string Half = """
        [[vk::binding(0, 0)]] RWStructuredBuffer<half4> Out;
        [shader("compute")] [numthreads(8, 1, 1)]
        void CSMain(uint id : SV_DispatchThreadID)
        {
            half x = (half)id * (half)0.5;
            Out[id] = half4(x, x + (half)1, x * x, (half)2);
        }
        """;

    private const string Doubles = """
        [[vk::binding(0, 0)]] RWStructuredBuffer<double> Out;
        [shader("compute")] [numthreads(8, 1, 1)]
        void CSMain(uint id : SV_DispatchThreadID)
        {
            double d = (double)id * 0.1;
            Out[id] = d + 1.0;
        }
        """;

    private const string Int64 = """
        [[vk::binding(0, 0)]] RWStructuredBuffer<uint64_t> Out;
        [shader("compute")] [numthreads(8, 1, 1)]
        void CSMain(uint id : SV_DispatchThreadID)
        {
            uint64_t v = (uint64_t)id << 33;
            Out[id] = v + 5;
        }
        """;

    [Fact]
    public void SixteenBitTypeWithoutTheSwitchIsAnErrorAtTheType()
    {
        var r = Compile("RWStructuredBuffer<float> O;\n[shader(\"compute\")] [numthreads(1,1,1)]\nvoid CSMain() {\n    int16_t x = 3;\n    O[0] = x;\n}\n", false);
        Assert.False(r.Success);
        Assert.Equal(("OSH0360", 4, 5), First(r, "OSH0360"));
        Assert.Contains("OneShaderEnable16BitTypes", r.Diagnostics.First(d => d.Code == "OSH0360").Message, StringComparison.Ordinal);
        Assert.True(Compile("RWStructuredBuffer<float> O;\n[shader(\"compute\")] [numthreads(1,1,1)]\nvoid CSMain() { int16_t x = 3; O[0] = x; }\n", true).Success);
    }

    [Fact]
    public void DoubleArgumentToAFloatOnlyIntrinsicWarnsAtTheCall()
    {
        var r = Compile("RWStructuredBuffer<double> O;\n[shader(\"compute\")] [numthreads(1,1,1)]\nvoid CSMain() {\n    O[0] = floor(O[1]);\n}\n", false);
        Assert.True(r.Success, Describe(r));
        Assert.Equal(("OSH0361", 4, 12), First(r, "OSH0361"));
    }

    [Fact]
    public void MslRefusesDoubleAtTheFirstUseAndOnlyWhenSelected()
    {
        Assert.True(Compile(Doubles, false).Success);
        var r = Compile(Doubles, false, CompileTarget.SpirV, CompileTarget.Msl);
        Assert.False(r.Success);
        var d = r.Diagnostics.First(x => x.Code == "OSH5102");
        Assert.Equal(("t.hlsl", 5), (d.Span.Path, d.Span.Line));
                Assert.Contains("float", d.Message, StringComparison.Ordinal);
        Assert.Single(r.Diagnostics, x => x.Code == "OSH5102");
    }

    [Fact]
    public void MslRefusesSixtyFourBitAtomicsOtherThanUnsignedMaxMin()
    {
        const string Src = """
            [[vk::binding(0, 0)]] RWStructuredBuffer<uint64_t> A;
            [shader("compute")] [numthreads(8, 1, 1)]
            void CSMain(uint id : SV_DispatchThreadID)
            {
                InterlockedAdd(A[0], (uint64_t)id);
            }
            """;
        var r = Compile(Src, false, CompileTarget.Msl);
        Assert.False(r.Success);
        Assert.Equal(("OSH5102", 5, 5), First(r, "OSH5102"));

        const string Ok = """
            [[vk::binding(0, 0)]] RWStructuredBuffer<uint64_t> A;
            [shader("compute")] [numthreads(8, 1, 1)]
            void CSMain(uint id : SV_DispatchThreadID)
            {
                InterlockedMax(A[0], (uint64_t)id);
            }
            """;
        var ok = Compile(Ok, false, CompileTarget.Msl);
        Assert.True(ok.Success, Describe(ok));
        string msl = ok.Blobs.Single().AsText();
        Assert.Contains("atomic_max_explicit((device atomic_ulong*)", msl, StringComparison.Ordinal);
        Assert.Equal(DeviceFeatures.Int64 | DeviceFeatures.Int64Atomics, ok.Reflection!.EntryPoints[0].RequiredFeatures);
    }

    [Fact]
    public void WgslRefusesInt16Int64AndDoubleWithPositions()
    {
        var sixteen = Compile("RWStructuredBuffer<int> O;\n[shader(\"compute\")] [numthreads(1,1,1)]\nvoid CSMain() {\n    int16_t x = (int16_t)O[1];\n    O[0] = x;\n}\n", true, CompileTarget.Wgsl);
        Assert.False(sixteen.Success);
        Assert.Equal("OSH5202", sixteen.Diagnostics.First(d => d.Severity == DiagnosticSeverity.Error).Code);
        Assert.Equal(4, sixteen.Diagnostics.First(d => d.Code == "OSH5202").Span.Line);

        var i64 = Compile(Int64, false, CompileTarget.Wgsl);
        Assert.False(i64.Success);
        Assert.Equal(5, First(i64, "OSH5202").Line);
        Assert.Contains("64-bit integer", i64.Diagnostics.First(d => d.Code == "OSH5202").Message, StringComparison.Ordinal);

        var dbl = Compile(Doubles, false, CompileTarget.Wgsl);
        Assert.False(dbl.Success);
        Assert.Equal(5, dbl.Diagnostics.First(d => d.Code == "OSH5202").Span.Line);
        Assert.Contains("64-bit floating", dbl.Diagnostics.First(d => d.Code == "OSH5202").Message, StringComparison.Ordinal);

        // SPIR-V alone is unaffected.
        Assert.True(Compile(Int64, false).Success);
    }

    [Fact]
    public void WgslHalfEnablesF16AndAsksForTheFeature()
    {
        var r = Compile(Half, true, CompileTarget.Wgsl);
        Assert.True(r.Success, Describe(r));
        string wgsl = r.Blobs.Single().AsText();
        Assert.StartsWith("enable f16;\n", wgsl, StringComparison.Ordinal);
        Assert.Contains("vec4<f16>", wgsl, StringComparison.Ordinal);
        Assert.Contains("pack2x16float", wgsl, StringComparison.Ordinal);

        // Without 16-bit types half is a plain f32 and needs no feature.
        var plain = Compile(Half, false, CompileTarget.Wgsl);
        Assert.True(plain.Success, Describe(plain));
        Assert.DoesNotContain("f16", plain.Blobs.Single().AsText(), StringComparison.Ordinal);
        Assert.Equal(DeviceFeatures.None, plain.Reflection!.EntryPoints[0].RequiredFeatures);
    }

    [Fact]
    public void ReflectionAndLayoutsNameTheDeviceFeatures()
    {
        var r = Compile(Half, true);
        Assert.True(r.Success, Describe(r));
        Assert.Equal(DeviceFeatures.Float16 | DeviceFeatures.StorageBuffer16, r.Reflection!.EntryPoints[0].RequiredFeatures);

        Assert.True(ReflectionFormat.TryRead(ReflectionFormat.Write(r.Reflection), out var back, out string? readError), readError);
        Assert.Equal(r.Reflection.EntryPoints[0].RequiredFeatures, back!.EntryPoints[0].RequiredFeatures);

        Assert.True(EntryLayout.TryCreate(r.Reflection, "CSMain", out var entry, out var error), error?.Message);
        var pipeline = PipelineLayout.Create(entry!);
        Assert.Equal(entry!.RequiredFeatures, pipeline.RequiredFeatures);
        Assert.Equal(entry.RequiredFeatures, pipeline.ToVulkan().RequiredFeatures);
        Assert.True(pipeline.TryToWebGpu(out var web, out error), error?.Message);
        Assert.Equal(["shader-f16"], web!.RequiredFeatures);

        var d = Compile(Doubles, false);
        Assert.Equal(DeviceFeatures.Float64, d.Reflection!.EntryPoints[0].RequiredFeatures);
    }

    [Fact]
    public void SixteenBitVertexInputsGetSixteenBitFormats()
    {
        const string Src = """
            struct VSIn { half2 uv : TEXCOORD0; half4 col : COLOR0; int16_t2 a : A0; uint16_t4 b : B0; };
            [shader("vertex")] float4 VSMain(VSIn i) : SV_Position { return float4(i.uv, i.col.x, (float)i.a.x + (float)i.b.y); }
            """;
        var r = ShaderCompiler.Compile(Src, "v.hlsl", new CompileOptions { Enable16BitTypes = true }, TestContext.Current.CancellationToken);
        Assert.True(r.Success, Describe(r));
        var formats = r.Reflection!.EntryPoints[0].Inputs.Select(v => v.Format).ToArray();
        Assert.Equal([VertexFormat.Float16x2, VertexFormat.Float16x4, VertexFormat.Sint16x2, VertexFormat.Uint16x4], formats);
    }

    private static string? Disassemble(CompileResult r)
    {
        string? dis = Oracles.Find("spirv-dis");
        if (dis is null)
        {
            return null;
        }

        string dir = Path.Combine(Path.GetTempPath(), "osh-sized-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "t.spv");
            File.WriteAllBytes(file, r.Blobs[0].ToArray());
            return Oracles.Run(dis, [file], dir).Output.Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ConstantFoldingWrapsAtTheTypesOwnWidth()
    {
        const string Src = """
            [[vk::binding(0, 0)]] RWStructuredBuffer<int> Out;
            [shader("compute")] [numthreads(1, 1, 1)]
            void CSMain()
            {
                int16_t a = (int16_t)32767 + (int16_t)1;
                uint16_t b = (uint16_t)0 - (uint16_t)1;
                int64_t c = (int64_t)9223372036854775807 + (int64_t)1;
                uint64_t d = ((uint64_t)1 << 40) * (uint64_t)3;
                Out[0] = a;
                Out[1] = b;
                Out[2] = (int)(c >> 32);
                Out[3] = (int)(d >> 32);
            }
            """;
        var r = Compile(Src, true);
        Assert.True(r.Success, Describe(r));
        string? text = Disassemble(r);
        Assert.SkipWhen(text is null, "spirv-dis is not installed");
        // 32767 + 1 wraps to -32768 and 0 - 1 to 65535 at 16 bits; 2^63 - 1 + 1 wraps to the minimum; 3 << 40 >> 32 is 768.
        Assert.Contains("OpConstant %int -32768", text, StringComparison.Ordinal);
        Assert.Contains("OpConstant %int 65535", text, StringComparison.Ordinal);
        Assert.Contains("OpConstant %int -2147483648", text, StringComparison.Ordinal);
        Assert.Contains("OpConstant %int 768", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OpIAdd", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DoubleSplitAndJoinUseTheBitwiseForms()
    {
        const string Src = """
            [[vk::binding(0, 0)]] RWStructuredBuffer<double> D;
            [[vk::binding(1, 0)]] RWStructuredBuffer<uint2> U;
            [shader("compute")] [numthreads(1, 1, 1)]
            void CSMain()
            {
                uint lo, hi;
                asuint(D[0], lo, hi);
                U[0] = uint2(lo, hi);
                D[1] = asdouble(lo + 1, hi);
            }
            """;
        var r = Compile(Src, false);
        Assert.True(r.Success, Describe(r));
        string? text = Disassemble(r);
        Assert.SkipWhen(text is null, "spirv-dis is not installed");
        Assert.Contains("OpBitcast %v2uint", text, StringComparison.Ordinal);
        Assert.Contains("OpBitcast %double", text, StringComparison.Ordinal);
        Assert.Contains("OpCapability Float64", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MinPrecisionWithoutTheSwitchIsRelaxedThirtyTwoBit()
    {
        const string Src = """
            [[vk::binding(0, 0)]] RWStructuredBuffer<min16float> O;
            [shader("compute")] [numthreads(1, 1, 1)]
            void CSMain() { O[0] = O[1] * (min16float)2; }
            """;
        var r = Compile(Src, false);
        Assert.True(r.Success, Describe(r));
        Assert.Equal(DeviceFeatures.None, r.Reflection!.EntryPoints[0].RequiredFeatures);
        string? text = Disassemble(r);
        Assert.SkipWhen(text is null, "spirv-dis is not installed");
        Assert.Contains("RelaxedPrecision", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OpCapability Float16", text, StringComparison.Ordinal);
    }
}
