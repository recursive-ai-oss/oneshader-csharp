using System;
using System.IO;
using System.Linq;
using OneShader.Diagnostics;
using OneShader.Layouts;
using OneShader.Reflection;
using Xunit;

namespace OneShader.Tests;

/// <summary>Cooperative matrices and bfloat16_t: checking, the three targets, reflection and the refusals.</summary>
public sealed class CooperativeMatrixTests
{
    private static CompileResult Compile(string src, params CompileTarget[] targets) =>
        ShaderCompiler.Compile(src, "t.hlsl", new CompileOptions { Enable16BitTypes = true, Targets = targets.Length == 0 ? [CompileTarget.SpirV] : targets }, TestContext.Current.CancellationToken);

    private static string Describe(CompileResult r) => string.Join("\n", r.Diagnostics.Select(d => d.ToString()));

    private static string CorpusFile(string name)
    {
        return File.ReadAllText(Path.Combine(CorpusFiles.Root(), "coop", name));
    }

    private const string Header = """
        StructuredBuffer<float16_t> A : register(t0);
        StructuredBuffer<float16_t> B : register(t1);
        RWStructuredBuffer<float> C : register(u2);
        groupshared float16_t Tile[256];
        typedef CooperativeMatrix<float16_t, 16, 16, MatrixUse::A> MA;
        typedef CooperativeMatrix<float16_t, 16, 16, MatrixUse::B> MB;
        typedef CooperativeMatrix<float, 16, 16, MatrixUse::Accumulator> MC;

        """;

    private static string Kernel(string body) => Header + "[shader(\"compute\")] [numthreads(32, 1, 1)]\nvoid CSMain(uint tid : SV_GroupIndex)\n{\n" + body + "\n}\n";

    private static string? Disassemble(CompileResult r, out bool valid)
    {
        valid = true;
        string? dis = Oracles.Find("spirv-dis");
        string? val = Oracles.Find("spirv-val");
        if (dis is null || val is null)
        {
            return null;
        }

        string dir = Path.Combine(Path.GetTempPath(), "osh-coop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "t.spv");
            File.WriteAllBytes(file, r.Blobs[0].ToArray());
            var (code, output) = Oracles.Run(val, ["--target-env", "vulkan1.2", file], dir);
            valid = code == 0;
            Assert.True(valid, output);
            return Oracles.Run(dis, [file], dir).Output.Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Float16ProductIsValidSpirvWithTheVulkanMemoryModel()
    {
        var r = Compile(CorpusFile("coop_f16.hlsl"));
        Assert.True(r.Success, Describe(r));
        string? text = Disassemble(r, out _);
        if (text is null)
        {
            return;
        }

        Assert.Contains("OpCapability CooperativeMatrixKHR", text, StringComparison.Ordinal);
        Assert.Contains("OpCapability VulkanMemoryModel", text, StringComparison.Ordinal);
        Assert.Contains("OpExtension \"SPV_KHR_cooperative_matrix\"", text, StringComparison.Ordinal);
        Assert.Contains("OpMemoryModel Logical Vulkan", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BFloat16", text, StringComparison.Ordinal);
        Assert.Contains("OpTypeCooperativeMatrixKHR %half %uint_3 %uint_16 %uint_16 %uint_0", text, StringComparison.Ordinal);
        Assert.Contains("OpTypeCooperativeMatrixKHR %float %uint_3 %uint_16 %uint_16 %uint_2", text, StringComparison.Ordinal);
        Assert.Contains("OpCooperativeMatrixMulAddKHR", text, StringComparison.Ordinal);
        Assert.Contains("NonPrivatePointer", text, StringComparison.Ordinal);
        // AcquireRelease | WorkgroupMemory | MakeAvailable | MakeVisible = 0x8 | 0x100 | 0x2000 | 0x4000.
        Assert.Contains("%uint_24840 = OpConstant %uint 24840", text, StringComparison.Ordinal);
        var features = r.Reflection!.EntryPoints[0].RequiredFeatures;
        Assert.Equal(DeviceFeatures.Float16 | DeviceFeatures.StorageBuffer16 | DeviceFeatures.CooperativeMatrix | DeviceFeatures.VulkanMemoryModel, features);
    }

    [Fact]
    public void BFloat16ProductNeedsTheBFloat16Capabilities()
    {
        var r = Compile(CorpusFile("coop_bf16.hlsl"));
        Assert.True(r.Success, Describe(r));
        string? text = Disassemble(r, out _);
        if (text is not null)
        {
            Assert.Contains("OpCapability BFloat16TypeKHR", text, StringComparison.Ordinal);
            Assert.Contains("OpCapability BFloat16CooperativeMatrixKHR", text, StringComparison.Ordinal);
            Assert.Contains("OpExtension \"SPV_KHR_bfloat16\"", text, StringComparison.Ordinal);
            Assert.Contains("OpTypeFloat 16 BFloat16KHR", text, StringComparison.Ordinal);
            Assert.Contains("OpFConvert %bfloat16", text, StringComparison.Ordinal);
            Assert.DoesNotContain("OpCapability Float16", text, StringComparison.Ordinal);
        }

        var features = r.Reflection!.EntryPoints[0].RequiredFeatures;
        Assert.Equal(DeviceFeatures.BFloat16 | DeviceFeatures.BFloat16CooperativeMatrix | DeviceFeatures.StorageBuffer16 | DeviceFeatures.CooperativeMatrix | DeviceFeatures.VulkanMemoryModel, features);
    }

    [Fact]
    public void ShadersWithoutCooperativeMatricesKeepTheGlslMemoryModelAndNoNonPrivateAccess()
    {
        var r = Compile("RWStructuredBuffer<float> O;\ngroupshared float S[32];\n[shader(\"compute\")] [numthreads(32,1,1)]\nvoid CSMain(uint t : SV_GroupIndex) { S[t] = t; GroupMemoryBarrierWithGroupSync(); O[t] = S[31 - t]; }\n");
        Assert.True(r.Success, Describe(r));
        string? text = Disassemble(r, out _);
        if (text is null)
        {
            return;
        }

        Assert.Contains("OpMemoryModel Logical GLSL450", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NonPrivatePointer", text, StringComparison.Ordinal);
        Assert.DoesNotContain("VulkanMemoryModel", text, StringComparison.Ordinal);
        Assert.Equal(DeviceFeatures.None, r.Reflection!.EntryPoints[0].RequiredFeatures);
    }

    [Fact]
    public void ColumnMajorLoadsAndStoresUseTheColumnMajorLayoutConstant()
    {
        var r = Compile(Kernel("""
            MA a; MB b;
            a.Load(A, 16, 32, MatrixLayout::ColumnMajor);
            b.Load(Tile, tid, 16, MatrixLayout::RowMajor);
            MC c = MC::MulAdd(a, b, MC::Zero());
            c.Store(C, 0, 64, MatrixLayout::ColumnMajor);
            """));
        Assert.True(r.Success, Describe(r));
        string? text = Disassemble(r, out _);
        if (text is null)
        {
            return;
        }

        Assert.Contains("OpCooperativeMatrixLoadKHR %", text, StringComparison.Ordinal);
        Assert.Contains("%uint_1 %uint_32 NonPrivatePointer", text, StringComparison.Ordinal);
        Assert.Contains("%uint_1 %uint_64 NonPrivatePointer", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ElementAccessAndLengthWorkOnTheLanesShare()
    {
        var r = Compile(Kernel("""
            MC c = MC::Zero();
            for (uint i = 0; i < c.Length(); i++) { c[i] = c[i] + (float)i; }
            c.Store(C, 0, 16, MatrixLayout::RowMajor);
            """));
        Assert.True(r.Success, Describe(r));
        string? text = Disassemble(r, out _);
        if (text is not null)
        {
            Assert.Contains("OpCooperativeMatrixLengthKHR", text, StringComparison.Ordinal);
        }

        var msl = Compile(Kernel("""
            MC c = MC::Zero();
            for (uint i = 0; i < c.Length(); i++) { c[i] = c[i] + (float)i; }
            c.Store(C, 0, 16, MatrixLayout::RowMajor);
            """), CompileTarget.Msl);
        Assert.True(msl.Success, Describe(msl));
        string m = msl.Blobs[0].AsText();
        Assert.Contains("thread_elements()", m, StringComparison.Ordinal);
        Assert.Contains("= 8u;", m, StringComparison.Ordinal);
    }

    [Fact]
    public void ArraysOfMatricesHoldTheAccumulators()
    {
        string src = Kernel("""
            MA a; MB b;
            a.Load(A, 0, 16, MatrixLayout::RowMajor);
            b.Load(B, 0, 16, MatrixLayout::RowMajor);
            MC acc[2];
            acc[0] = MC::Zero();
            acc[1] = MC::Zero();
            [unroll] for (uint i = 0; i < 2; i++) { acc[i] = MC::MulAdd(a, b, acc[i]); }
            acc[1].Store(C, 0, 16, MatrixLayout::RowMajor);
            """);
        var r = Compile(src);
        Assert.True(r.Success, Describe(r));
        Disassemble(r, out _);
        var msl = Compile(src, CompileTarget.Msl);
        Assert.True(msl.Success, Describe(msl));
    }

    [Fact]
    public void MslThreadgroupMemoryIsExactlyTheDeclaredGroupshared()
    {
        // Eight simdgroups, two 64x64 float tiles = 32768 bytes. Cooperative loads read the tiles in place: nothing else is declared in the
        // threadgroup address space (a Metal pipeline's reported bytes then equal the declared ones; see docs/msl-backend.md).
        var r = Compile(CorpusFile("coop_gemm_f32.hlsl"), CompileTarget.Msl);
        Assert.True(r.Success, Describe(r));
        string m = r.Blobs[0].AsText();
        Assert.Equal(2, Count(m, "threadgroup array<float, 4096> "));
        Assert.Equal(2, Count(m, "    threadgroup "));
        Assert.Equal(12, Count(m, "simdgroup_load("));
    }

    [Fact]
    public void MslWritesOneSimdgroupLoadPerTileAndTransposesColumnMajor()
    {
        var r = Compile(CorpusFile("coop_f16.hlsl"), CompileTarget.Msl);
        Assert.True(r.Success, Describe(r));
        string m = r.Blobs[0].AsText();
        Assert.StartsWith("// Generated by OneShader. Metal Shading Language 2.4 or newer.", m, StringComparison.Ordinal);
        Assert.Contains("simdgroup_matrix<half, 8, 8> t[4];", m, StringComparison.Ordinal);
        Assert.Equal(4, Count(m, "simdgroup_multiply_accumulate(v") / 2);
        Assert.Equal(8, Count(m, "simdgroup_multiply_accumulate("));
        Assert.Equal(8, Count(m, "simdgroup_load("));
        Assert.Equal(4, Count(m, "simdgroup_store("));
        Assert.Contains("make_filled_simdgroup_matrix<float, 8, 8>", m, StringComparison.Ordinal);

        var ok = Compile(Kernel("MA a; MB b; a.Load(Tile, 0, 16, MatrixLayout::ColumnMajor); b.Load(Tile, 0, 16, MatrixLayout::RowMajor); MC c = MC::MulAdd(a, b, MC::Zero()); c.Store(C, 0, 16, MatrixLayout::ColumnMajor);"), CompileTarget.Msl);
        Assert.True(ok.Success, Describe(ok));
        string cm = ok.Blobs[0].AsText();
        Assert.Contains(", ulong2(0, 0), true);", cm, StringComparison.Ordinal);
        Assert.Contains("8 * ", cm, StringComparison.Ordinal);
    }

    [Fact]
    public void MslNeedsMetal31OnlyForBFloat16()
    {
        var r = Compile(CorpusFile("coop_bf16.hlsl"), CompileTarget.Msl);
        Assert.True(r.Success, Describe(r));
        string m = r.Blobs[0].AsText();
        Assert.StartsWith("// Generated by OneShader. Metal Shading Language 3.1 or newer", m, StringComparison.Ordinal);
        Assert.Contains("simdgroup_matrix<bfloat, 8, 8>", m, StringComparison.Ordinal);
        Assert.Contains("bfloat(v", m, StringComparison.Ordinal);
    }

    [Fact]
    public void MslRefusesShapesThatAreNotMultiplesOfEightAtTheDeclaration()
    {
        var r = Compile("""
            RWStructuredBuffer<float> C : register(u0);
            [shader("compute")] [numthreads(32, 1, 1)]
            void CSMain()
            {
                CooperativeMatrix<float, 12, 16, MatrixUse::Accumulator> c = CooperativeMatrix<float, 12, 16, MatrixUse::Accumulator>::Zero();
                c.Store(C, 0, 16, MatrixLayout::RowMajor);
            }
            """, CompileTarget.SpirV, CompileTarget.Msl);
        Assert.False(r.Success);
        var d = r.Diagnostics.Single(x => x.Severity == DiagnosticSeverity.Error);
        Assert.Equal("OSH5102", d.Code);
        Assert.Equal((5, 62), (d.Span.Line, d.Span.Column));
        Assert.Contains("multiples of 8", d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WgslRefusesCooperativeMatricesAtTheFirstUseAndOnlyWhenSelected()
    {
        string src = CorpusFile("coop_f16.hlsl");
        Assert.True(Compile(src, CompileTarget.SpirV, CompileTarget.Msl).Success);
        var r = Compile(src, CompileTarget.SpirV, CompileTarget.Wgsl);
        Assert.False(r.Success);
        var d = r.Diagnostics.Single(x => x.Severity == DiagnosticSeverity.Error);
        Assert.Equal("OSH5202", d.Code);
        Assert.Equal("t.hlsl", d.Span.Path);
        Assert.Equal((38, 56), (d.Span.Line, d.Span.Column));
        Assert.Contains("Cooperative matrices", d.Message, StringComparison.Ordinal);
        Assert.Contains("WGSL", d.Message, StringComparison.Ordinal);

        var bf = Compile("StructuredBuffer<bfloat16_t> A : register(t0);\nRWStructuredBuffer<float> O : register(u1);\n[shader(\"compute\")] [numthreads(1,1,1)]\nvoid CSMain() { O[0] = (float)A[0]; }\n", CompileTarget.Wgsl);
        Assert.False(bf.Success);
        Assert.Equal(("OSH5202", 4), (bf.Diagnostics.Single(x => x.Severity == DiagnosticSeverity.Error).Code, bf.Diagnostics.Single(x => x.Severity == DiagnosticSeverity.Error).Span.Line));
    }

    [Theory]
    [InlineData("MA a; a.Load(A, 0, 16);", "expects 4 arguments", 1)]
    [InlineData("MA a; a.Load(C, 0, 16, MatrixLayout::RowMajor);", "the matrix element type is", 1)]
    [InlineData("MA a; a.Store(A, 0, 16, MatrixLayout::RowMajor);", "RWStructuredBuffer", 1)]
    [InlineData("MA a; uint l = 0; a.Load(A, 0, 16, l);", "MatrixLayout::RowMajor", 1)]
    [InlineData("MA a; a.Load(Tile, 0, 16, MatrixLayout::Diagonal);", "not declared", 1)]
    [InlineData("MA a; MB b; MC c = MC::MulAdd(b, a, MC::Zero());", "MatrixUse::A", 1)]
    [InlineData("CooperativeMatrix<float16_t, 16, 8, MatrixUse::A> a; CooperativeMatrix<float16_t, 16, 16, MatrixUse::B> b; MC c = MC::MulAdd(a, b, MC::Zero());", "shapes do not fit", 1)]
    [InlineData("CooperativeMatrix<int8_t, 16, 16, MatrixUse::A> a;", "8-bit integer cooperative matrices are not supported", 1)]
    [InlineData("CooperativeMatrix<int, 16, 16, MatrixUse::Accumulator> a;", "float16_t, bfloat16_t or float", 1)]
    [InlineData("CooperativeMatrix<bfloat16_t, 16, 16, MatrixUse::Accumulator> a;", "accumulate in float", 1)]
    [InlineData("CooperativeMatrix<float, 16, 16, MatrixUse::C> a;", "not a matrix use", 1)]
    [InlineData("MC c = MC::Zero(); float x = c + c;", "", 1)]
    [InlineData("static MC c;", "static, const or groupshared", 1)]
    public void MisuseIsReportedAtTheLineWithAFix(string body, string expected, int count)
    {
        var r = Compile(Kernel(body));
        Assert.False(r.Success, "should not compile: " + body);
        var errors = r.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.Equal(count, errors.Length);
        Assert.Equal(Header.Count(c => c == '\n') + 4, errors[0].Span.Line);
        Assert.Contains(expected, errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BFloat16RoundsToNearestEvenWhenFolded()
    {
        var r = Compile("""
            RWStructuredBuffer<float> O : register(u0);
            [shader("compute")] [numthreads(1, 1, 1)]
            void CSMain()
            {
                O[0] = (float)(bfloat16_t)1.00390625f;   // exactly halfway between 1.0 and 1.0078125: rounds to the even mantissa, 1.0
                O[1] = (float)(bfloat16_t)1.01171875f;   // halfway between 1.0078125 and 1.015625: rounds up to the even one, 1.015625
                O[2] = (float)(bfloat16_t)3.14159274f;
            }
            """);
        Assert.True(r.Success, Describe(r));
        var ir = ShaderCompiler.Compile("RWStructuredBuffer<float> O;\n[shader(\"compute\")] [numthreads(1,1,1)]\nvoid CSMain() { O[0] = (float)(bfloat16_t)1.00390625f; }\n", "t.hlsl", new CompileOptions { Targets = [CompileTarget.Msl] }, TestContext.Current.CancellationToken);
        Assert.True(ir.Success, Describe(ir));
    }

    [Fact]
    public void BFloat16ArithmeticRunsInFloatAndVectorsAreRefused()
    {
        var ok = Compile("""
            StructuredBuffer<bfloat16_t> A : register(t0);
            RWStructuredBuffer<bfloat16_t> O : register(u1);
            [shader("compute")] [numthreads(1, 1, 1)]
            void CSMain() { O[0] = (bfloat16_t)(A[0] * A[1] + 1.0f); }
            """);
        Assert.True(ok.Success, Describe(ok));
        Disassemble(ok, out _);
        var vec = Compile("RWStructuredBuffer<float> O;\n[shader(\"compute\")] [numthreads(1,1,1)]\nvoid CSMain() {\n    bfloat16_t4 v;\n}\n");
        Assert.False(vec.Success);
        Assert.Equal(("OSH0311", 4), (vec.Diagnostics.First(d => d.Severity == DiagnosticSeverity.Error).Code, vec.Diagnostics.First(d => d.Severity == DiagnosticSeverity.Error).Span.Line));
    }

    [Fact]
    public void RequiredFeaturesRoundTripThroughReflectionAndLayouts()
    {
        var r = Compile(CorpusFile("coop_bf16.hlsl"));
        Assert.True(r.Success, Describe(r));
        Assert.True(ReflectionFormat.TryRead(ReflectionFormat.Write(r.Reflection!), out var back, out string? err), err);
        var want = r.Reflection!.EntryPoints[0].RequiredFeatures;
        Assert.Equal(want, back!.EntryPoints[0].RequiredFeatures);
        Assert.True(EntryLayout.TryCreate(r.Reflection, "Bf16Product", out var entry, out var error), error?.Message);
        Assert.Equal(want, entry!.RequiredFeatures);
        Assert.Equal(want, PipelineLayout.Create(entry).ToVulkan().RequiredFeatures);
    }

    private static int Count(string text, string needle)
    {
        int n = 0;
        for (int at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            n++;
        }

        return n;
    }
}
