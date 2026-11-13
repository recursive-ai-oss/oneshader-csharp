using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace OneShader.Tests;

/// <summary><c>precise</c>: which operations are marked, and what each target does with the mark.</summary>
public class PreciseTests
{
    private const string Probe = """
        RWStructuredBuffer<float> O : register(u0);
        StructuredBuffer<float> I : register(t1);
        precise float F(float a, float b, float c) { return a * b + c; }
        [shader("compute")][numthreads(1,1,1)]
        void CSMain(uint3 id : SV_DispatchThreadID)
        {
            float a = I[0], b = I[1], c = I[2];
            precise float p = a * b + c;
            float q = a * b + c;
            precise float r;
            r = (a + b) - a;
            O[0] = p; O[1] = q; O[2] = r;
            precise float t = a * 2.0 + (b * c);
            float u = t * 3.0;
            O[4] = u;
            precise float d = a / b;
            O[5] = d;
        }
        """;

    private static CompileResult Compile(string source, CompileTarget target) =>
        ShaderCompiler.Compile(source, "p.hlsl", new CompileOptions { Targets = [target] }, TestContext.Current.CancellationToken);

    [Fact]
    public void SpirvMarksTheSameArithmeticAsDxc()
    {
        string? dxc = Oracles.Find("dxc");
        string? dis = Oracles.Find("spirv-dis");
        Assert.SkipUnless(dxc is not null && dis is not null, "dxc and spirv-dis are not installed");
        string dir = Path.Combine(Path.GetTempPath(), "osh-precise-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.hlsl"), Probe);
            var (code, output) = Oracles.Run(dxc!, ["-spirv", "-fspv-target-env=vulkan1.2", "-T", "cs_6_0", "-E", "CSMain", "t.hlsl", "-Fo", "dxc.spv"], dir);
            Assert.True(code == 0, output);
            var r = Compile(Probe, CompileTarget.SpirV);
            Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
            File.WriteAllBytes(Path.Combine(dir, "our.spv"), r.Find("CSMain", CompileTarget.SpirV)!.Data);
            int Count(string file)
            {
                var (c, text) = Oracles.Run(dis!, [file], dir);
                Assert.Equal(0, c);
                return text.Split('\n').Count(l => l.Contains("NoContraction", StringComparison.Ordinal));
            }

            int ours = Count("our.spv");
            Assert.Equal(Count("dxc.spv"), ours);
            Assert.True(ours >= 8, "expected the operations of p, r, t and d to be marked, found " + ours);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Products and dot products are the arithmetic that most often gets fused with a following add; a precise value computed
    // with them must carry the decoration too, exactly where DXC puts it.
    private const string ProductProbe = """
        RWStructuredBuffer<float4> O : register(u0);
        StructuredBuffer<float4> I : register(t1);
        cbuffer C : register(b2) { float4x4 M; float4 P; };
        [shader("compute")][numthreads(1,1,1)]
        void CSMain(uint3 id : SV_DispatchThreadID)
        {
            float4 p = I[0];
            precise float4 clip = mul(M, p);
            precise float4 clip2 = mul(p, M);
            precise float4x4 mm = mul(M, M);
            precise float d = dot(p, P);
            float4 plain = mul(M, p) + dot(p, P);
            O[0] = clip; O[1] = clip2; O[2] = mm[0]; O[3] = d; O[5] = plain;
        }
        """;

    [Fact]
    public void PreciseProductsAndDotsAreDecoratedLikeDxcAndValidate()
    {
        string? dxc = Oracles.Find("dxc");
        string? dis = Oracles.Find("spirv-dis");
        string? val = Oracles.Find("spirv-val");
        Assert.SkipUnless(dxc is not null && dis is not null && val is not null, "dxc, spirv-dis and spirv-val are not installed");
        string dir = Path.Combine(Path.GetTempPath(), "osh-precise-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.hlsl"), ProductProbe);
            var (code, output) = Oracles.Run(dxc!, ["-spirv", "-fspv-target-env=vulkan1.2", "-T", "cs_6_0", "-E", "CSMain", "t.hlsl", "-Fo", "dxc.spv"], dir);
            Assert.True(code == 0, output);
            var r = Compile(ProductProbe, CompileTarget.SpirV);
            Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
            File.WriteAllBytes(Path.Combine(dir, "our.spv"), r.Find("CSMain", CompileTarget.SpirV)!.Data);
            var (vc, vout) = Oracles.Run(val!, ["--target-env", "vulkan1.2", "our.spv"], dir);
            Assert.True(vc == 0, vout);

            // The decorated opcodes, in order, from the disassembly: every NoContraction target is named by its defining instruction.
            List<string> Marked(string file)
            {
                var (c, text) = Oracles.Run(dis!, ["--raw-id", file], dir);
                Assert.Equal(0, c);
                string[] lines = text.Split('\n');
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (string l in lines)
                {
                    if (l.Contains("OpDecorate", StringComparison.Ordinal) && l.Contains("NoContraction", StringComparison.Ordinal))
                    {
                        ids.Add(l.Trim().Split(' ')[1]);
                    }
                }

                var ops = new List<string>();
                foreach (string l in lines)
                {
                    string t = l.Trim();
                    int eq = t.IndexOf(" = ", StringComparison.Ordinal);
                    if (eq > 0 && ids.Contains(t[..eq].Trim()))
                    {
                        ops.Add(t[(eq + 3)..].Split(' ')[0]);
                    }
                }

                ops.Sort(StringComparer.Ordinal);
                return ops;
            }

            var ours = Marked("our.spv");
            Assert.Equal(Marked("dxc.spv"), ours);
            Assert.Contains("OpMatrixTimesVector", ours);
            Assert.Contains("OpVectorTimesMatrix", ours);
            Assert.Contains("OpMatrixTimesMatrix", ours);
            Assert.Contains("OpDot", ours);
            // The non-precise product and dot stay undecorated.
            Assert.Equal(1, ours.Count(o => o == "OpDot"));

            // A precise matrix sum is one vector add per row, and every one is marked (DXC drops the rows the shader never reads).
            string sumProbe = ProductProbe.Replace("float4 plain", "precise float4x4 sum = M + M; O[6] = sum[0]; O[7] = sum[1]; O[8] = sum[2]; O[9] = sum[3];\n            float4 plain", StringComparison.Ordinal);
            File.WriteAllBytes(Path.Combine(dir, "sum.spv"), Compile(sumProbe, CompileTarget.SpirV).Find("CSMain", CompileTarget.SpirV)!.Data);
            Assert.Equal(4, Marked("sum.spv").Count(o => o == "OpFAdd"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void PreciseProductsMakeMslAndWgslBehaveAsForOtherPreciseArithmetic()
    {
        var msl = Compile(ProductProbe, CompileTarget.Msl);
        Assert.True(msl.Success, string.Join("\n", msl.Diagnostics.Select(d => d.ToString())));
        Assert.Contains("#pragma clang fp contract(off)", msl.Find("CSMain", CompileTarget.Msl)!.AsText(), StringComparison.Ordinal);
        var wgsl = Compile(ProductProbe, CompileTarget.Wgsl);
        Assert.Single(wgsl.Diagnostics, d => d.Code == "OSH5205");
        const string plain = "RWStructuredBuffer<float4> O : register(u0); cbuffer C : register(b2) { float4x4 M; float4 P; };\n" +
            "[shader(\"compute\")][numthreads(1,1,1)] void CSMain(uint3 id : SV_DispatchThreadID) { O[0] = mul(M, P); O[1] = dot(P, P); }";
        Assert.DoesNotContain(Compile(plain, CompileTarget.Wgsl).Diagnostics, d => d.Code == "OSH5205");
        Assert.DoesNotContain("#pragma clang fp", Compile(plain, CompileTarget.Msl).Find("CSMain", CompileTarget.Msl)!.AsText(), StringComparison.Ordinal);
    }

    [Fact]
    public void MslTurnsOffFusingAndReassociationOnlyWhereTheFunctionIsPrecise()
    {
        string text = Compile(Probe, CompileTarget.Msl).Find("CSMain", CompileTarget.Msl)!.AsText();
        Assert.Contains("#pragma clang fp reassociate(off)", text, StringComparison.Ordinal);
        Assert.Contains("#pragma clang fp contract(off)", text, StringComparison.Ordinal);
        Assert.Contains("precise::divide(", text, StringComparison.Ordinal);
        Assert.DoesNotContain("#pragma clang fp reassociate(off)", ((string)Compile(Probe.Replace("precise ", "", StringComparison.Ordinal), CompileTarget.Msl).Find("CSMain", CompileTarget.Msl)!.AsText()), StringComparison.Ordinal);
    }

    [Fact]
    public void PreciseFunctionsAndNonPreciseNeighboursAreSeparate()
    {
        const string src = """
            RWStructuredBuffer<float> O : register(u0);
            float Plain(float a, float b, float c) { return a * b + c; }
            precise float Exact(float a, float b, float c) { return a * b + c; }
            [shader("compute")][numthreads(1,1,1)]
            void CSMain(uint3 id : SV_DispatchThreadID)
            {
                O[0] = Plain(O[1], O[2], O[3]);
                O[4] = Exact(O[5], O[6], O[7]);
            }
            """;
        var r = ShaderCompiler.Compile(src, "p.hlsl", new CompileOptions { Targets = [CompileTarget.Msl], Optimize = false }, TestContext.Current.CancellationToken);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        string text = r.Find("CSMain", CompileTarget.Msl)!.AsText();
        int exact = text.IndexOf(" Exact(", StringComparison.Ordinal);
        int plain = text.IndexOf(" Plain(", StringComparison.Ordinal);
        int pragma = text.IndexOf("#pragma clang fp contract(off)", StringComparison.Ordinal);
        Assert.True(exact > 0 && plain > 0 && pragma > exact, "the pragma belongs to Exact only:\n" + text);
        Assert.Equal(1, text.Split("#pragma clang fp contract(off)").Length - 1);
        if (MslCorpus.DumpDirectory is { } dir)
        {
            dir = Path.Combine(dir, "precise");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "precise_functions.metal"), text);
            File.WriteAllText(Path.Combine(dir, "precise_probe.metal"), Compile(Probe, CompileTarget.Msl).Find("CSMain", CompileTarget.Msl)!.AsText());
        }
    }

    [Fact]
    public void WgslCompilesPreciseWithAWarningOnce()
    {
        var r = Compile(Probe, CompileTarget.Wgsl);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var warnings = r.Diagnostics.Where(d => d.Code == "OSH5205").ToList();
        Assert.Single(warnings);
        Assert.Equal(OneShader.Diagnostics.DiagnosticSeverity.Warning, warnings[0].Severity);
        Assert.Contains("WGSL cannot forbid", warnings[0].Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Compile(Probe.Replace("precise ", "", StringComparison.Ordinal), CompileTarget.Wgsl).Diagnostics, d => d.Code == "OSH5205");
    }
}
