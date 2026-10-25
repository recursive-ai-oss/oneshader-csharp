using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using OneShader.Reflection;
using Xunit;

namespace OneShader.Tests;

/// <summary>Resource arrays, append/consume buffers and their counters, checked against what DXC binds for the same source.</summary>
public partial class ResourceArrayTests
{
    [GeneratedRegex(@"OpName %(\w+) ""([^""]+)""")]
    private static partial Regex NameLine();

    [GeneratedRegex(@"OpDecorate %(\w+) (Binding|DescriptorSet) (\d+)")]
    private static partial Regex DecorationLine();

    private static CompileResult Compile(string text)
    {
        var r = ShaderCompiler.Compile(text, "t.hlsl");
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        return r;
    }

    private static string[] Errors(string text) =>
        ShaderCompiler.Compile(text, "t.hlsl").Diagnostics.Select(d => d.Span.Line + "," + d.Span.Column + " " + d.Severity + " " + d.Code + ": " + d.Message).ToArray();

    /// <summary>Writes the blob to a temp directory, validates it for Vulkan 1.2 and returns its disassembly (null without the tools).</summary>
    private static string? ValidateAndDisassemble(byte[] spirv)
    {
        string? val = Oracles.Find("spirv-val");
        string? dis = Oracles.Find("spirv-dis");
        if (val is null || dis is null)
        {
            return null;
        }

        string dir = Path.Combine(Path.GetTempPath(), "osh-arr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "a.spv");
            File.WriteAllBytes(file, spirv);
            var (code, output) = Oracles.Run(val, ["--target-env", "vulkan1.2", file], dir);
            Assert.True(code == 0, output);
            return Oracles.Run(dis, [file], dir).Output;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>name → "set:binding" for every variable decorated in the disassembly.</summary>
    private static SortedDictionary<string, string> Bindings(string disassembly)
    {
        var names = new Dictionary<string, string>();
        foreach (Match m in NameLine().Matches(disassembly))
        {
            names[m.Groups[1].Value] = m.Groups[2].Value;
        }

        var set = new Dictionary<string, string>();
        var binding = new Dictionary<string, string>();
        foreach (Match m in DecorationLine().Matches(disassembly))
        {
            (m.Groups[2].Value == "Binding" ? binding : set)[names.GetValueOrDefault(m.Groups[1].Value, m.Groups[1].Value)] = m.Groups[3].Value;
        }

        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in binding)
        {
            result[kv.Key] = set.GetValueOrDefault(kv.Key, "?") + ":" + kv.Value;
        }

        return result;
    }

    private static SortedDictionary<string, string>? DxcBindings(string text)
    {
        string? dxc = Oracles.Find("dxc");
        string? dis = Oracles.Find("spirv-dis");
        if (dxc is null || dis is null)
        {
            return null;
        }

        string dir = Path.Combine(Path.GetTempPath(), "osh-arr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string src = Path.Combine(dir, "t.hlsl");
            File.WriteAllText(src, text);
            string spv = Path.Combine(dir, "t.spv");
            var (code, output) = Oracles.Run(dxc, ["-spirv", "-fspv-target-env=vulkan1.2", "-T", "cs_6_0", "-E", "CSMain", src, "-Fo", spv], dir);
            Assert.True(code == 0, output);
            return Bindings(Oracles.Run(dis, [spv], dir).Output);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private const string ArrayShader = @"
Texture2D<float4> Tex[4] : register(t0, space1);
SamplerState Smp[2] : register(s9, space1);
StructuredBuffer<uint> Bufs[3] : register(t0, space2);
RWStructuredBuffer<uint> Outs[2] : register(u9, space2);
RWTexture2D<float4> Imgs[2] : register(u0, space3);
struct P { float4 c; uint k; };
ConstantBuffer<P> Cbs[2] : register(b0, space4);
cbuffer G : register(b0) { uint Which; };
float4 Fetch(Texture2D<float4> t, SamplerState s, float2 uv) { return t.SampleLevel(s, uv, 0); }
[shader(""compute"")] [numthreads(4,1,1)] void CSMain(uint3 id : SV_DispatchThreadID) {
  float4 acc = 0;
  [unroll] for (int i = 0; i < 4; i++) acc += Tex[i].Load(int3(id.x, 0, 0));
  acc += Fetch(Tex[Which], Smp[Which & 1], float2(0.5, 0.5));
  Texture2D<float4> t = Tex[Which];
  acc += t.Load(int3(1,1,0));
  uint n, st; Bufs[Which].GetDimensions(n, st);
  Outs[Which & 1][id.x] = Bufs[Which][id.x] + n + Cbs[Which & 1].k;
  Imgs[Which & 1][int2(id.x, 0)] = acc + Cbs[1].c;
}";

    [Fact]
    public void ArraysOfEveryResourceKindMatchDxcBindings()
    {
        var r = Compile(ArrayShader);
        var counts = r.Reflection!.Resources.ToDictionary(x => x.Name, x => x.Count);
        Assert.Equal(new Dictionary<string, uint> { ["Tex"] = 4, ["Smp"] = 2, ["Bufs"] = 3, ["Outs"] = 2, ["Imgs"] = 2, ["Cbs"] = 2, ["G"] = 1 }, counts);
        string? dis = ValidateAndDisassemble(r.Blobs[0].ToArray());
        if (dis is null)
        {
            return;
        }

        // Each indexed capability the Vulkan features name is declared.
        foreach (string cap in new[] { "SampledImageArrayDynamicIndexing", "StorageBufferArrayDynamicIndexing", "UniformBufferArrayDynamicIndexing", "StorageImageArrayDynamicIndexing" })
        {
            Assert.Contains("OpCapability " + cap, dis, StringComparison.Ordinal);
        }

        var ours = Bindings(dis);
        var dxc = DxcBindings(ArrayShader);
        if (dxc is not null)
        {
            Assert.Equal(dxc, ours);
        }
    }

    [Fact]
    public void AnArrayTakesOneBindingNumber()
    {
        // DXC's automatic numbering gives an array one binding, not one per element.
        const string text = "Texture2D<float4> A[4];\nTexture2D<float4> B;\nSamplerState S[2];\nRWStructuredBuffer<float4> R : register(u0);\n" +
            "[shader(\"compute\")] [numthreads(1,1,1)] void CSMain() { R[0] = B.SampleLevel(S[1], float2(0,0), 0) + A[1].Load(int3(0,0,0)) + A[2].Load(int3(0,0,0)); }\n";
        var r = Compile(text);
        Assert.Equal(
            new Dictionary<string, uint> { ["A"] = 1, ["B"] = 2, ["S"] = 3, ["R"] = 0 },
            r.Reflection!.Resources.ToDictionary(x => x.Name, x => x.Slots[0].Binding));
        var dxc = DxcBindings(text);
        string? dis = ValidateAndDisassemble(r.Blobs[0].ToArray());
        if (dxc is not null && dis is not null)
        {
            Assert.Equal(dxc, Bindings(dis));
        }
    }

    [Fact]
    public void UnboundedArrayIsOneClearError()
    {
        var l = Errors("Texture2D<float4> Tex[] : register(t0);\nRWStructuredBuffer<float4> O : register(u1);\n[shader(\"compute\")] [numthreads(1,1,1)] void CSMain() { O[0] = Tex[3].Load(int3(0,0,0)); }\n");
        Assert.Equal(["1,19 Error OSH0350: 'Tex' is an unbounded resource array, which is not supported. Give it a size, for example 'Tex[16]'."], l);
    }

    [Fact]
    public void ResourceArrayElementCanBePassedWithARunTimeIndex()
    {
        const string text = "Texture2D<float4> Tex[4] : register(t1);\nSamplerState Smp : register(s5);\nRWStructuredBuffer<float4> Out : register(u7);\n" +
            "float4 Fetch(Texture2D<float4> t, float2 uv) { return t.SampleLevel(Smp, uv, 0); }\n" +
            "float4 Both(Texture2D<float4> a, Texture2D<float4> b) { return a.Load(int3(0,0,0)) + b.Load(int3(1,1,0)); }\n" +
            "cbuffer C : register(b9) { uint Which; };\n" +
            "[shader(\"compute\")] [numthreads(1,1,1)] void CSMain() {\n  Texture2D<float4> t = Tex[Which];\n" +
            "  Out[0] = Fetch(Tex[Which], 0.5) + Fetch(t, 0.25) + Both(Tex[Which], Tex[Which + 1]) + Fetch(Tex[2], 0.1);\n" +
            "  for (uint i = 0; i < 4; i++) Out[1] += Fetch(Tex[i], 0.5);\n}\n";
        foreach (bool optimize in new[] { true, false })
        {
            var r = ShaderCompiler.Compile(text, "t.hlsl", new CompileOptions { Optimize = optimize }, TestContext.Current.CancellationToken);
            Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
            ValidateAndDisassemble(r.Blobs[0].ToArray());
        }
    }

    [Fact]
    public void NonUniformIndexIsDecoratedOnTheHandles()
    {
        const string text = "Texture2D<float4> Tex[4] : register(t1);\nSamplerState Smp[2] : register(s5);\nRWStructuredBuffer<float4> Out : register(u7);\n" +
            "cbuffer C : register(b9) { uint Which; uint Samp; };\n" +
            "[shader(\"compute\")] [numthreads(1,1,1)] void CSMain() { Out[0] = Tex[NonUniformResourceIndex(Which)].SampleLevel(Smp[NonUniformResourceIndex(Samp)], float2(0.5,0.5), 0); }\n";
        var r = Compile(text);
        string? dis = ValidateAndDisassemble(r.Blobs[0].ToArray());
        if (dis is not null)
        {
            Assert.Contains("OpCapability ShaderNonUniform", dis, StringComparison.Ordinal);
            Assert.Contains("OpCapability SampledImageArrayNonUniformIndexing", dis, StringComparison.Ordinal);
            Assert.True(Regex.Count(dis, "NonUniform\\r?\\n") >= 6, "index, access chains, loads and the sampled image are decorated");
        }

        var bad = Errors("RWStructuredBuffer<uint> O;\n[shader(\"compute\")] [numthreads(1,1,1)] void CSMain() { O[0] = NonUniformResourceIndex(1.5); }\n");
        Assert.Equal(["2,88 Error OSH0302: 'NonUniformResourceIndex' takes a 32-bit integer index (int or uint), not 'float'."], bad);
    }

    // ---- append / consume

    public static IEnumerable<object[]> CounterShaders() =>
    [
        ["AppendStructuredBuffer<float4> A : register(u0);\nConsumeStructuredBuffer<float4> B : register(u1);\n[shader(\"compute\")] [numthreads(1,1,1)] void CSMain(uint3 id : SV_DispatchThreadID) {\n  float4 v = B.Consume(); uint n, s; A.GetDimensions(n, s); A.Append(v + n);\n}\n"],
        ["AppendStructuredBuffer<float4> A : register(u3);\nConsumeStructuredBuffer<float4> B : register(u1);\nRWStructuredBuffer<float4> R : register(u0);\nAppendStructuredBuffer<float4> A2 : register(u7, space1);\n[shader(\"compute\")] [numthreads(1,1,1)] void CSMain(uint3 id : SV_DispatchThreadID) {\n  float4 v = B.Consume(); A.Append(v); A2.Append(v); R[0] = v;\n}\n"],
        ["[[vk::binding(5,2)]] [[vk::counter_binding(9)]] AppendStructuredBuffer<float4> A : register(u3);\nAppendStructuredBuffer<float4> A2;\nConsumeStructuredBuffer<uint> B;\nRWStructuredBuffer<float4> R;\n[shader(\"compute\")] [numthreads(1,1,1)] void CSMain(uint3 id : SV_DispatchThreadID) {\n  A.Append(1); A2.Append(2); uint n = B.Consume(); R[0] = n;\n}\n"],
        ["RWStructuredBuffer<float4> R : register(u0);\nAppendStructuredBuffer<float4> A : register(u1);\n[shader(\"compute\")] [numthreads(1,1,1)] void CSMain(uint3 id : SV_DispatchThreadID) {\n  A.Append(1); R[0] = 2;\n}\n"],
    ];

    [Theory]
    [MemberData(nameof(CounterShaders))]
    public void CounterBindingsMatchDxc(string text)
    {
        var r = Compile(text);
        string? dis = ValidateAndDisassemble(r.Blobs[0].ToArray());
        if (dis is null)
        {
            return;
        }

        var ours = Bindings(dis);
        var dxc = DxcBindings(text);
        if (dxc is not null)
        {
            Assert.Equal(dxc, ours);
        }

        // Reflection names the same slot as the SPIR-V decoration.
        foreach (var res in r.Reflection!.Resources.Where(x => x.CounterSlots.Count > 0))
        {
            var slot = Assert.Single(res.CounterSlots);
            Assert.Equal(ours["counter.var." + res.Name], slot.Set.ToString(CultureInfo.InvariantCulture) + ":" + slot.Binding.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(res.Slots[0].Set, slot.Set);
        }
    }

    [Fact]
    public void CountersSurviveTheReflectionFileFormat()
    {
        var r = Compile("AppendStructuredBuffer<float4> A : register(u3);\n[shader(\"compute\")] [numthreads(1,1,1)] void CSMain() { A.Append(1); }\n");
        string text = ReflectionFormat.Write(r.Reflection!);
        Assert.Contains("  counter target=SpirV set=0 binding=0\n", text, StringComparison.Ordinal);
        Assert.True(ReflectionFormat.TryRead(text, out var back, out _));
        Assert.Equal(text, ReflectionFormat.Write(back!));
        Assert.Equal(r.Reflection!.Resources[0].CounterSlots, back!.Resources[0].CounterSlots);
    }

    [Fact]
    public void AppendOnAConsumeBufferSaysWhichMethodToUse()
    {
        var l = Errors("ConsumeStructuredBuffer<uint> B : register(u0);\n[shader(\"compute\")] [numthreads(1,1,1)] void CSMain() { B.Append(1); }\n");
        Assert.Equal(["2,59 Error OSH0308: 'Append' belongs to AppendStructuredBuffer, but this is a ConsumeStructuredBuffer; use Consume."], l);
    }

    [Fact]
    public void ArrayOfAppendBuffersIsRejected()
    {
        var l = Errors("AppendStructuredBuffer<uint> B[2] : register(u0);\n[shader(\"compute\")] [numthreads(1,1,1)] void CSMain() { }\n");
        Assert.Equal(["1,30 Error OSH0350: An array of append or consume buffers is not supported: each buffer needs its own counter. Declare 'B' as separate variables."], l);
    }

    // ---- language leftovers

    [Fact]
    public void ConstantBufferMemberMayShareTheBufferName()
    {
        var r = Compile("struct Light { float4 p; };\ncbuffer Lights : register(b0) { Light Lights[8]; };\nRWStructuredBuffer<float4> Out : register(u1);\n[shader(\"compute\")] [numthreads(1,1,1)] void CSMain(uint3 id : SV_DispatchThreadID) { Out[0] = Lights[id.x].p; }\n");
        Assert.Equal("Lights", r.Reflection!.Resources[0].Name);
        ValidateAndDisassemble(r.Blobs[0].ToArray());
    }

    [Fact]
    public void StaticMembersOutOfLineDefinitionsAndStructTemplateDeduction()
    {
        string source = File.ReadAllText(Path.Combine(CorpusFiles.Root(), "lang", "statics_and_literals.hlsl"));
        var r = Compile(source);
        ValidateAndDisassemble(r.Blobs[0].ToArray());
    }

    [Fact]
    public void NonConstStaticMemberIsAPerInvocationGlobal()
    {
        var r = Compile("struct Cfg { static uint Counter; static uint Next() { Counter++; return Counter; } };\nRWStructuredBuffer<uint> O : register(u0);\n[shader(\"compute\")] [numthreads(1,1,1)] void CSMain() { Cfg::Counter = 4; uint a = Cfg::Next(); O[0] = a + Cfg::Next() + Cfg::Counter; }\n");
        ValidateAndDisassemble(r.Blobs[0].ToArray());
    }

    [Fact]
    public void OutOfLineDefinitionNeedsADeclaration()
    {
        var l = Errors("struct A { uint x; };\nuint A::Get() { return x; }\n[shader(\"compute\")] [numthreads(1,1,1)] void CSMain() { }\n");
        Assert.Equal(["2,6 Error OSH0308: The struct 'A' declares no member function 'Get'. Declare it inside the struct first, then define it here."], l);
        l = Errors("struct A { uint x; uint Get(); };\nuint A::Get(uint y) { return x + y; }\n[shader(\"compute\")] [numthreads(1,1,1)] void CSMain() { }\n");
        Assert.Single(l);
        Assert.Contains("No member function of 'A' is declared as", l[0], StringComparison.Ordinal);
    }

    [Fact]
    public void LiteralIntegerArithmeticIsWide()
    {
        // DXC folds literal-only integer expressions in 64 bits and converts where the value is used.
        var r = FrontendCorpusTests.Run("RWStructuredBuffer<uint> O;\n[shader(\"compute\")] [numthreads(1,1,1)] void CSMain() { O[0] = (1 << 31) >> 3; O[1] = (3000000000 * 2) >> 1; O[2] = 0xFFFFFFFF + 1; O[3] = -1 >> 28; int s = 31; O[4] = (1 << s) >> 3; }\n", "l.hlsl");
        Assert.Empty(r.Diagnostics.Items);
        string ir = OneShader.IR.IrPrinter.Print(r.Module!);
        Assert.Contains("268435456:", ir, StringComparison.Ordinal);
        Assert.Contains("3000000000:", ir, StringComparison.Ordinal);
    }
}
