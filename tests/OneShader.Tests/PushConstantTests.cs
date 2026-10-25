using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using OneShader.Diagnostics;
using OneShader.Reflection;
using Xunit;

namespace OneShader.Tests;

/// <summary>Push constants (<c>[[vk::push_constant]]</c>) on every target: reflection, DXC layout, diagnostics, validation.</summary>
public sealed partial class PushConstantTests
{
    private static readonly CompileTarget[] All = [CompileTarget.SpirV, CompileTarget.Msl, CompileTarget.Wgsl];

    public static TheoryData<string> Shaders() => new() { "3d/push_lit.hlsl", "compute/push_kernel.hlsl" };

    private static CompileResult Compile(string source, string name = "a.hlsl", params CompileTarget[] targets) =>
        ShaderCompiler.Compile(source, name, new CompileOptions { Targets = targets.Length == 0 ? All : targets }, TestContext.Current.CancellationToken);

    private static CompileResult CompileCorpus(string rel, params CompileTarget[] targets) =>
        Compile(File.ReadAllText(Path.Combine(CorpusFiles.Root(), rel)), Path.GetFileName(rel), targets);

    private static string Describe(CompileResult r) => string.Join("\n", r.Diagnostics.Select(d => d.ToString()));

    [GeneratedRegex(@"OpMemberName %(\w+) (\d+) ""(\w+)""")]
    private static partial Regex MemberName();

    [GeneratedRegex(@"OpMemberDecorate %(\w+) (\d+) Offset (\d+)")]
    private static partial Regex MemberOffset();

    [Theory]
    [MemberData(nameof(Shaders))]
    public void CompilesOnEveryTargetWithoutDiagnostics(string rel)
    {
        var r = CompileCorpus(rel);
        Assert.True(r.Success, Describe(r));
        Assert.Empty(r.Diagnostics);
        Assert.NotNull(r.Reflection);
        Assert.Equal(3 * r.Reflection!.EntryPoints.Count, r.Blobs.Count);
    }

    [Fact]
    public void ReflectionRecordsLayoutStagesAndSlots()
    {
        var r = CompileCorpus("3d/push_lit.hlsl");
        var p = Assert.Single(r.Reflection!.PushConstants);
        Assert.Equal("Draw", p.Name);
        Assert.Equal(ShaderStages.Vertex | ShaderStages.Pixel, p.Stages);
        Assert.Equal(112u, p.Layout.Size);
        Assert.Equal(
            [("Model", 0u), ("Tint", 64u), ("PaletteIndex", 80u), ("LightDirection", 84u), ("UvScale", 96u), ("Weights", 104u)],
            p.Layout.Members.Select(m => (m.Name, m.Offset)).ToArray());
        Assert.All(r.Reflection.EntryPoints, e => Assert.Equal("Draw", e.PushConstant));
        // The push block is not a descriptor: the resource list only has the shader's own resources.
        Assert.DoesNotContain(r.Reflection.Resources, x => x.Name == "Draw");
        // The block is the first buffer in declaration order for Metal; WebGPU takes the next binding after the shader's own (0 and 1).
        Assert.Equal(new TargetSlot(CompileTarget.Msl, 0, 0), p.Slots.Single(s => s.Target == CompileTarget.Msl));
        Assert.Equal(new TargetSlot(CompileTarget.Wgsl, 0, 2), p.Slots.Single(s => s.Target == CompileTarget.Wgsl));
        Assert.DoesNotContain(p.Slots, s => s.Target == CompileTarget.SpirV);

        var k = CompileCorpus("compute/push_kernel.hlsl").Reflection!;
        Assert.Equal(ShaderStages.Compute, Assert.Single(k.PushConstants).Stages);
        Assert.Equal(64u, k.PushConstants[0].Layout.Size);
    }

    [Fact]
    public void ReflectionRoundTripsThroughTheTextFormat()
    {
        var model = CompileCorpus("3d/push_lit.hlsl").Reflection!;
        string text = ReflectionFormat.Write(model);
        Assert.True(ReflectionFormat.TryRead(text, out var back, out string? error), error);
        Assert.Equal(text, ReflectionFormat.Write(back!));
        Assert.Equal("Draw", back!.PushConstants[0].Name);
        Assert.Equal("Draw", back.EntryPoints[0].PushConstant);
    }

    [Theory]
    [MemberData(nameof(Shaders))]
    public void SpirvIsValidAndOffsetsEqualDxc(string rel)
    {
        string? dxc = Oracles.Find("dxc");
        string? dis = Oracles.Find("spirv-dis");
        string? val = Oracles.Find("spirv-val");
        Assert.SkipUnless(dxc is not null && dis is not null && val is not null, "dxc, spirv-dis or spirv-val is not installed");
        string path = Path.Combine(CorpusFiles.Root(), rel);
        var r = CompileCorpus(rel, CompileTarget.SpirV);
        string dir = Path.Combine(Path.GetTempPath(), "osh-push-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var ep in r.Reflection!.EntryPoints)
            {
                string ours = Path.Combine(dir, ep.Name + ".ours.spv");
                File.WriteAllBytes(ours, r.Find(ep.Name, CompileTarget.SpirV)!.ToArray());
                var (vc, vo) = Oracles.Run(val!, ["--target-env", "vulkan1.2", ours], dir);
                Assert.True(vc == 0, vo);

                string theirs = Path.Combine(dir, ep.Name + ".dxc.spv");
                string profile = ep.Stage switch { ShaderStage.Vertex => "vs_6_0", ShaderStage.Pixel => "ps_6_0", _ => "cs_6_0" };
                var (dc, dout) = Oracles.Run(dxc!, ["-spirv", "-fspv-target-env=vulkan1.2", "-T", profile, "-E", ep.Name, path, "-Fo", theirs], dir);
                Assert.True(dc == 0, dout);

                var push = r.Reflection.PushConstants[0];
                string ourText = Oracles.Run(dis!, [ours], dir).Output;
                string dxcText = Oracles.Run(dis!, [theirs], dir).Output;
                Assert.Contains("PushConstant", ourText, StringComparison.Ordinal);
                var ourOffsets = PushBlockOffsets(ourText);
                var dxcOffsets = PushBlockOffsets(dxcText);
                Assert.Equal(dxcOffsets, ourOffsets);
                Assert.Equal(push.Layout.Members.Select(m => (m.Name, m.Offset)).ToArray(), ourOffsets);
                Assert.DoesNotContain("DescriptorSet", Regex.Match(ourText, @"OpDecorate %" + Regex.Escape(push.Name) + @" .*").Value, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>Member names and offsets of the Block struct that backs the PushConstant variable, in member order.</summary>
    private static (string, uint)[] PushBlockOffsets(string dis)
    {
        string? type = null;
        foreach (Match m in Regex.Matches(dis, @"%\w+ = OpVariable %_ptr_PushConstant_(\w+) PushConstant"))
        {
            type = m.Groups[1].Value;
        }

        Assert.NotNull(type);
        var names = new SortedDictionary<int, string>();
        foreach (Match m in MemberName().Matches(dis))
        {
            if (m.Groups[1].Value == type)
            {
                names[int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)] = m.Groups[3].Value;
            }
        }

        var list = new List<(string, uint)>();
        foreach (Match m in MemberOffset().Matches(dis))
        {
            if (m.Groups[1].Value == type)
            {
                list.Add((names[int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)], uint.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)));
            }
        }

        return [.. list];
    }

    [Fact]
    public void MslTakesTheBlockAsAConstantReference()
    {
        var r = CompileCorpus("3d/push_lit.hlsl", CompileTarget.Msl);
        Assert.True(r.Success, Describe(r));
        string vs = r.Find("VSMain", CompileTarget.Msl)!.AsText();
        string ps = r.Find("PSMain", CompileTarget.Msl)!.AsText();
        Assert.Matches(@"constant \w+& Draw \[\[buffer\(0\)\]\]", vs);
        Assert.Matches(@"constant \w+& Draw \[\[buffer\(0\)\]\]", ps);
    }

    [Fact]
    public void WgslBindsTheBlockAsAUniformAfterTheShadersOwnBindings()
    {
        var r = CompileCorpus("3d/push_lit.hlsl", CompileTarget.Wgsl);
        Assert.True(r.Success, Describe(r));
        string text = r.Find("PSMain", CompileTarget.Wgsl)!.AsText();
        Assert.Contains("@group(0) @binding(2) var<uniform> Draw: array<vec4<u32>, 7>;", text, StringComparison.Ordinal);
        string? naga = Oracles.Find("naga");
        if (naga is not null)
        {
            WgslBackendTests.ValidateWithNaga(naga, r);
        }
    }

    [Fact]
    public void WgslBindingNeverCollidesWithTheShadersOwn()
    {
        const string source = """
            struct P { float4 v; };
            [[vk::push_constant]] P Pc;
            [[vk::binding(5, 2)]] RWStructuredBuffer<float4> A;
            [[vk::binding(0, 3)]] StructuredBuffer<float4> B;
            [[vk::binding(1, 3)]] StructuredBuffer<float4> C;
            [shader("compute")][numthreads(1,1,1)] void CSMain(uint3 id : SV_DispatchThreadID) { A[0] = Pc.v + B[0] + C[0]; }
            """;
        var r = Compile(source);
        Assert.True(r.Success, Describe(r));
        var slot = r.Reflection!.PushConstants[0].Slots.Single(s => s.Target == CompileTarget.Wgsl);
        Assert.Equal(new TargetSlot(CompileTarget.Wgsl, 3, 2), slot);
        // Metal: the block is declared before no buffer, so it takes index 0 and the buffers follow.
        Assert.Equal(0u, r.Reflection.PushConstants[0].Slots.Single(s => s.Target == CompileTarget.Msl).Binding);
        Assert.Equal(1u, r.Reflection.Resources.First(x => x.Name == "A").Slots.Single(s => s.Target == CompileTarget.Msl).Binding);
    }

    [Fact]
    public void PushConstantsLeaveDescriptorBindingsUntouched()
    {
        const string source = """
            struct P { float4 v; };
            [[vk::push_constant]] P Pc;
            RWStructuredBuffer<float4> A : register(u0);
            StructuredBuffer<float4> B : register(t1);
            [shader("compute")][numthreads(1,1,1)] void CSMain(uint3 id : SV_DispatchThreadID) { A[0] = Pc.v + B[0]; }
            """;
        var r = Compile(source, "a.hlsl", CompileTarget.SpirV);
        Assert.True(r.Success, Describe(r));
        Assert.Equal(new TargetSlot(CompileTarget.SpirV, 0, 0), r.Reflection!.Resources[0].Slots[0]);
        Assert.Equal(new TargetSlot(CompileTarget.SpirV, 0, 1), r.Reflection.Resources[1].Slots[0]);
    }

    [Fact]
    public void TwoBlocksInOneEntryPointAreAnErrorAtTheSecondBlock()
    {
        const string source = """
            struct A { float4 a; };
            struct B { float4 b; };
            [[vk::push_constant]] A First;
            [[vk::push_constant]] ConstantBuffer<B> Second;
            [shader("vertex")] float4 VSMain(float4 p : POSITION) : SV_Position { return p + First.a + Second.b; }
            """;
        var r = Compile(source, "two.hlsl");
        Assert.False(r.Success);
        var d = Assert.Single(r.Diagnostics, x => x.Severity == DiagnosticSeverity.Error);
        Assert.Equal("OSH0333", d.Code);
        Assert.Equal(("two.hlsl", 4, 41), (d.Span.Path, d.Span.Line, d.Span.Column));
        Assert.Contains("'First' and 'Second'", d.Message, StringComparison.Ordinal);
        Assert.Contains("Merge them into one struct", d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoBlocksUsedByDifferentEntryPointsAreFine()
    {
        const string source = """
            struct A { float4 a; };
            struct B { float4 b; };
            [[vk::push_constant]] A First;
            [[vk::push_constant]] B Second;
            [shader("vertex")] float4 VSMain(float4 p : POSITION) : SV_Position { return p + First.a; }
            [shader("pixel")] float4 PSMain() : SV_Target { return Second.b; }
            """;
        var r = Compile(source, "two.hlsl");
        Assert.True(r.Success, Describe(r));
        Assert.Equal(2, r.Reflection!.PushConstants.Count);
        Assert.Equal("First", r.Reflection.EntryPoints[0].PushConstant);
        Assert.Equal("Second", r.Reflection.EntryPoints[1].PushConstant);
    }

    [Fact]
    public void ABlockOver128BytesWarnsAndNamesTheLimit()
    {
        const string source = """
            struct Big { float4 v[9]; };
            [[vk::push_constant]] Big Pc;
            [shader("vertex")] float4 VSMain(float4 p : POSITION) : SV_Position { return p + Pc.v[8]; }
            """;
        var r = Compile(source, "big.hlsl");
        Assert.True(r.Success, Describe(r));
        var d = Assert.Single(r.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Equal("OSH0334", d.Code);
        Assert.Equal(("big.hlsl", 2, 27), (d.Span.Path, d.Span.Line, d.Span.Column));
        Assert.Contains("144 bytes", d.Message, StringComparison.Ordinal);
        Assert.Contains("128", d.Message, StringComparison.Ordinal);
        Assert.Equal(144u, r.Reflection!.PushConstants[0].Layout.Size);
    }

    [Fact]
    public void ABlockOfExactly128BytesDoesNotWarn()
    {
        const string source = """
            struct Fits { float4 v[8]; };
            [[vk::push_constant]] Fits Pc;
            [shader("vertex")] float4 VSMain(float4 p : POSITION) : SV_Position { return p + Pc.v[7]; }
            """;
        Assert.Empty(Compile(source).Diagnostics);
    }

    [Fact]
    public void CbufferAndNonStructFormsAreRejectedWithAdvice()
    {
        var cb = Compile("[[vk::push_constant]] cbuffer C { float4 a; };\n[shader(\"vertex\")] float4 VSMain(float4 p : POSITION) : SV_Position { return p + a; }", "c.hlsl");
        var e1 = Assert.Single(cb.Diagnostics, x => x.Severity == DiagnosticSeverity.Error);
        Assert.Equal("OSH0333", e1.Code);
        Assert.Equal(("c.hlsl", 1, 1), (e1.Span.Path, e1.Span.Line, e1.Span.Column));
        Assert.Contains("ConstantBuffer<", e1.Message, StringComparison.Ordinal);

        var scalar = Compile("[[vk::push_constant]] float4 a;\n[shader(\"vertex\")] float4 VSMain(float4 p : POSITION) : SV_Position { return p; }", "s.hlsl");
        var e2 = Assert.Single(scalar.Diagnostics, x => x.Severity == DiagnosticSeverity.Error);
        Assert.Equal("OSH0333", e2.Code);
        Assert.Contains("struct", e2.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BlockSizeIsTheMembersRoundedUpToTheStructAlignment()
    {
        // DXC puts Index at 80; the block ends at 84, which the reflected size rounds up to the 16-byte struct alignment.
        const string source = """
            struct PerDraw { float4x4 Model; float4 Tint; uint Index; };
            [[vk::push_constant]] ConstantBuffer<PerDraw> Draw;
            [shader("vertex")] float4 VSMain(float4 p : POSITION) : SV_Position { return mul(Draw.Model, p) * Draw.Tint + Draw.Index; }
            """;
        var r = Compile(source);
        Assert.True(r.Success, Describe(r));
        Assert.Equal(96u, r.Reflection!.PushConstants[0].Layout.Size);
    }

    [Fact]
    public void HotReloadSeesAChangedPushBlock()
    {
        const string v1 = "struct P { float4 a; uint n; };\n[[vk::push_constant]] P Pc;\n[shader(\"vertex\")] float4 VSMain(float4 p : POSITION) : SV_Position { return p + Pc.a * Pc.n; }\n";
        string v2 = v1.Replace("float4 a; uint n;", "uint n; float4 a;", StringComparison.Ordinal);
        var built = Compile(v1).Reflection!;
        Assert.True(InterfaceCompatibility.Compare(built, Compile(v1).Reflection!).IsCompatible);
        var report = InterfaceCompatibility.Compare(built, Compile(v2).Reflection!);
        Assert.False(report.IsCompatible);
        Assert.Contains(report.Differences, d => d.Message.Contains("Pc", StringComparison.Ordinal));
    }

    [Fact]
    public void OutputIsDeterministic()
    {
        var a = CompileCorpus("3d/push_lit.hlsl");
        var b = CompileCorpus("3d/push_lit.hlsl");
        Assert.Equal(ReflectionFormat.Write(a.Reflection!), ReflectionFormat.Write(b.Reflection!));
        for (int i = 0; i < a.Blobs.Count; i++)
        {
            Assert.Equal(a.Blobs[i].ToArray(), b.Blobs[i].ToArray());
        }
    }
}
