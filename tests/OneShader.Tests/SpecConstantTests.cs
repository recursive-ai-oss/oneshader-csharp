using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using OneShader.Diagnostics;
using OneShader.Layouts;
using OneShader.Reflection;
using OneShader.Types;
using Xunit;

namespace OneShader.Tests;

/// <summary>Specialization constants (<c>[[vk::constant_id]]</c>) on every target: declarations, diagnostics, reflection, layout helpers, baking.</summary>
public sealed partial class SpecConstantTests
{
    private const uint OpSpecConstantTrue = 48, OpSpecConstantFalse = 49, OpSpecConstant = 50, OpBranchConditional = 250, OpSelect = 169;

    private static readonly CompileTarget[] All = [CompileTarget.SpirV, CompileTarget.Msl, CompileTarget.Wgsl];

    private const string Tail = "RWStructuredBuffer<float> o;\n[shader(\"compute\")] [numthreads(8,1,1)] void main(uint3 id : SV_DispatchThreadID) { o[id.x] = (float)N; }";

    private static CompileResult Compile(string source, string name = "a.hlsl", CompileOptions? options = null) =>
        ShaderCompiler.Compile(source, name, options ?? new CompileOptions { Targets = All }, TestContext.Current.CancellationToken);

    private static CompileResult CompileCorpus(string rel, CompileOptions? options = null) =>
        Compile(File.ReadAllText(Path.Combine(CorpusFiles.Root(), rel)), Path.GetFileName(rel), options);

    private static string Describe(CompileResult r) => string.Join("\n", r.Diagnostics.Select(d => d.ToString()));

    private static Diagnostic OnlyError(CompileResult r)
    {
        Assert.False(r.Success);
        return Assert.Single(r.Diagnostics, x => x.Severity == DiagnosticSeverity.Error);
    }

    /// <summary>Opcodes of a SPIR-V module in order.</summary>
    private static List<uint> Opcodes(byte[] spirv)
    {
        var ops = new List<uint>();
        for (int pos = 20; pos + 4 <= spirv.Length;)
        {
            uint head = BitConverter.ToUInt32(spirv, pos);
            ops.Add(head & 0xFFFF);
            pos += (int)(head >> 16) * 4;
        }

        return ops;
    }

    [GeneratedRegex(@"OpDecorate %(\w+) SpecId (\d+)")]
    private static partial Regex SpecIdDecoration();

    // ---- declarations and diagnostics ----

    [Theory]
    [InlineData("[[vk::constant_id(1)]] const int N = 3;\n[[vk::constant_id(1)]] const int M = 4;\n", "constant_id 1 is already used by 'N'", 2, 1)]
    [InlineData("[[vk::constant_id(1)]] const float2 N = 3;\n", "scalar bool, int, uint or float", 1, 1)]
    [InlineData("struct S { int a; };\n[[vk::constant_id(1)]] const S N = (S)0;\n", "scalar bool, int, uint or float", 2, 1)]
    [InlineData("[[vk::constant_id(1)]] const int N;\n", "needs a default value", 1, 34)]
    [InlineData("[[vk::constant_id(1)]] static const int N = 3;\n", "remove 'static'", 1, 1)]
    [InlineData("[[vk::constant_id(-1)]] const int N = 3;\n", "must not be negative", 1, 19)]
    [InlineData("[[vk::constant_id(1)]] const int M = 1;\n[[vk::constant_id(2)]] const int N = M + 1;\n", "cannot depend on another specialization constant", 2, 38)]
    [InlineData("int Runtime;\n[[vk::constant_id(1)]] const int N = Runtime;\n", "cannot depend on another specialization constant", 2, 38)]
    public void BadDeclarationsAreRefusedAtTheirLine(string head, string message, int line, int column)
    {
        var r = Compile(head + Tail);
        var d = OnlyError(r);
        Assert.Equal("OSH0335", d.Code);
        Assert.Equal(("a.hlsl", line, column), (d.Span.Path, d.Span.Line, d.Span.Column));
        Assert.Contains(message, d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AssigningToASpecializationConstantIsAnError()
    {
        var r = Compile("[[vk::constant_id(1)]] const int N = 3;\n[shader(\"compute\")] [numthreads(1,1,1)] void main() { N = 2; }");
        var d = OnlyError(r);
        Assert.Equal("OSH0307", d.Code);
        Assert.Equal(2, d.Span.Line);
    }

    [Theory]
    [InlineData("groupshared float g[N];\n[shader(\"compute\")] [numthreads(1,1,1)] void main() { g[0] = 1; }", 2)]
    [InlineData("[shader(\"compute\")] [numthreads(N,1,1)] void main() { }", 2)]
    [InlineData("[shader(\"compute\")] [numthreads(1,1,1)] void main(uint3 id : SV_DispatchThreadID) { switch (id.x) { case N: break; } }", 2)]
    public void WhereAConstantIsRequiredASpecializationConstantSaysWhyItIsRefused(string rest, int line)
    {
        // DXC refuses the same uses; array sizes, thread-group sizes and case labels have to be known when the shader is compiled.
        var r = Compile("[[vk::constant_id(1)]] const int N = 3;\n" + rest);
        var d = Assert.Single(r.Diagnostics, x => x.Severity == DiagnosticSeverity.Error);
        Assert.Equal("OSH0310", d.Code);
        Assert.Equal(line, d.Span.Line);
        Assert.Contains("specialization constant", d.Message, StringComparison.Ordinal);
        Assert.Contains("static const", d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstantDefaultsMayUseStaticConstAndConvert()
    {
        var r = Compile("static const int K = 2;\n[[vk::constant_id(1)]] const int N = K * 3;\n[[vk::constant_id(2)]] const uint U = 7;\n[[vk::constant_id(3)]] const float F = 2;\n[[vk::constant_id(4)]] const bool B = 1;\n" + Tail);
        Assert.True(r.Success, Describe(r));
        var specs = r.Reflection!.SpecConstants;
        Assert.Equal(["N", "U", "F", "B"], specs.Select(s => s.Name).ToArray());
        Assert.Equal([6.0, 7.0, 2.0, 1.0], specs.Select(s => s.DefaultValue).ToArray());
        Assert.Equal([ScalarKind.Int, ScalarKind.UInt, ScalarKind.Float, ScalarKind.Bool], specs.Select(s => s.Scalar).ToArray());
    }

    // ---- emission ----

    [Fact]
    public void CorpusKernelCompilesOnEveryTarget()
    {
        var r = CompileCorpus("compute/spec_const_compute.hlsl");
        Assert.True(r.Success, Describe(r));
        Assert.Empty(r.Diagnostics);
        Assert.Equal(3, r.Blobs.Count);
    }

    [Fact]
    public void SpirvDeclaresSpecConstantsWithTheirIdsAndDefaults()
    {
        var r = CompileCorpus("compute/spec_const_compute.hlsl", new CompileOptions());
        byte[] spirv = r.Find("CSMain", CompileTarget.SpirV)!.ToArray();
        var ops = Opcodes(spirv);
        Assert.Equal(1, ops.Count(o => o == OpSpecConstantFalse));
        Assert.Equal(3, ops.Count(o => o == OpSpecConstant));
    }

    [Fact]
    public void TheOptimizerKeepsDecisionsOnSpecializationConstantsInTheCode()
    {
        // With the default folded in, the branch would disappear and the pipeline could no longer choose.
        const string source = "[[vk::constant_id(0)]] const bool Fast = true;\n[[vk::constant_id(1)]] const int N = 4;\nRWStructuredBuffer<int> o;\n"
            + "[shader(\"compute\")] [numthreads(1,1,1)] void main() { int v = 5; if (Fast) { v = N * 2; } o[0] = v + N; }";
        var r = Compile(source, "fold.hlsl", new CompileOptions { Optimize = true });
        Assert.True(r.Success, Describe(r));
        var ops = Opcodes(r.Find("main", CompileTarget.SpirV)!.ToArray());
        Assert.Contains(OpSpecConstantTrue, ops);
        Assert.True(ops.Contains(OpBranchConditional) || ops.Contains(OpSelect), "the decision on Fast was folded away");
        Assert.Equal(1, ops.Count(o => o == OpSpecConstant));
        string wgsl = r.Find("main", CompileTarget.Wgsl) is { } w ? w.AsText() : Compile(source, "fold.hlsl", new CompileOptions { Targets = [CompileTarget.Wgsl] }).Find("main", CompileTarget.Wgsl)!.AsText();
        Assert.Contains("@id(0) override Fast: bool = true;", wgsl, StringComparison.Ordinal);
        Assert.Contains("@id(1) override N: i32 = 4i;", wgsl, StringComparison.Ordinal);
    }

    [Fact]
    public void MslUsesFunctionConstantsWithTheDefaultAppliedWhenUnset()
    {
        string msl = CompileCorpus("compute/spec_const_compute.hlsl").Find("CSMain", CompileTarget.Msl)!.AsText();
        Assert.Contains("constant bool UseOffset [[function_constant(0)]];", msl, StringComparison.Ordinal);
        Assert.Contains("constant bool UseOffset_value = is_function_constant_defined(UseOffset) ? UseOffset : false;", msl, StringComparison.Ordinal);
        Assert.Contains("constant float Scale_value = is_function_constant_defined(Scale) ? Scale : 0.5f;", msl, StringComparison.Ordinal);
        Assert.Contains("constant uint Mask_value = is_function_constant_defined(Mask) ? Mask : 255u;", msl, StringComparison.Ordinal);
    }

    [Fact]
    public void WgslUsesOverridesKeyedById()
    {
        string wgsl = CompileCorpus("compute/spec_const_compute.hlsl").Find("CSMain", CompileTarget.Wgsl)!.AsText();
        Assert.Contains("@id(0) override UseOffset: bool = false;", wgsl, StringComparison.Ordinal);
        Assert.Contains("@id(1) override Rounds: i32 = 4i;", wgsl, StringComparison.Ordinal);
        Assert.Contains("@id(2) override Mask: u32 = 255u;", wgsl, StringComparison.Ordinal);
        Assert.Contains("@id(3) override Scale: f32 = 0.5f;", wgsl, StringComparison.Ordinal);
    }

    [Fact]
    public void WgslRefusesIdsAboveTheOverrideRange()
    {
        var r = Compile("[[vk::constant_id(70000)]] const int N = 3;\n" + Tail, "big.hlsl", new CompileOptions { Targets = [CompileTarget.Wgsl] });
        var d = OnlyError(r);
        Assert.StartsWith("OSH52", d.Code, StringComparison.Ordinal);
        Assert.Contains("65535", d.Message, StringComparison.Ordinal);
        Assert.True(Compile("[[vk::constant_id(70000)]] const int N = 3;\n" + Tail, "big.hlsl", new CompileOptions { Targets = [CompileTarget.SpirV] }).Success);
    }

    [Fact]
    public void NamesAreDeterministic()
    {
        var a = CompileCorpus("compute/spec_const_compute.hlsl");
        var b = CompileCorpus("compute/spec_const_compute.hlsl");
        Assert.Equal(ReflectionFormat.Write(a.Reflection!), ReflectionFormat.Write(b.Reflection!));
        for (int i = 0; i < a.Blobs.Count; i++)
        {
            Assert.True(a.Blobs[i].Data.SequenceEqual(b.Blobs[i].Data));
        }
    }

    // ---- DXC ----

    [Theory]
    [InlineData("compute/spec_const_compute.hlsl", "CSMain", "cs_6_0")]
    [InlineData("2d/spec_const_pixel.hlsl", "PSMain", "ps_6_0")]
    public void SpecIdsEqualDxc(string rel, string entry, string profile)
    {
        string? dxc = Oracles.Find("dxc");
        string? dis = Oracles.Find("spirv-dis");
        Assert.SkipUnless(dxc is not null && dis is not null, "dxc or spirv-dis is not installed");
        string path = Path.Combine(CorpusFiles.Root(), rel);
        string dir = Path.Combine(Path.GetTempPath(), "osh-spec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string spv = Path.Combine(dir, "ref.spv");
            var (code, output) = Oracles.Run(dxc!, ["-spirv", "-fspv-target-env=vulkan1.2", "-T", profile, "-E", entry, path, "-Fo", spv], dir);
            Assert.True(code == 0, output);
            var (_, text) = Oracles.Run(dis!, [spv], dir);
            var theirs = SpecIdDecoration().Matches(text).Select(m => (m.Groups[1].Value, uint.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))).OrderBy(x => x.Item1, StringComparer.Ordinal).ToArray();
            Assert.NotEmpty(theirs);

            var r = CompileCorpus(rel, new CompileOptions());
            string ours = Path.Combine(dir, "ours.spv");
            File.WriteAllBytes(ours, r.Find(entry, CompileTarget.SpirV)!.ToArray());
            var (_, ourText) = Oracles.Run(dis!, [ours], dir);
            var mine = SpecIdDecoration().Matches(ourText).Select(m => (m.Groups[1].Value, uint.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))).OrderBy(x => x.Item1, StringComparer.Ordinal).ToArray();
            Assert.Equal(theirs, mine);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("compute/spec_const_compute.hlsl")]
    [InlineData("2d/spec_const_pixel.hlsl")]
    public void SpirvPassesTheValidator(string rel)
    {
        string? val = Oracles.Find("spirv-val");
        Assert.SkipUnless(val is not null, "spirv-val is not installed");
        var r = CompileCorpus(rel, new CompileOptions { Optimize = true });
        string dir = Path.Combine(Path.GetTempPath(), "osh-specval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var b in r.Blobs)
            {
                string f = Path.Combine(dir, b.EntryPoint + ".spv");
                File.WriteAllBytes(f, b.ToArray());
                var (code, output) = Oracles.Run(val!, ["--target-env", "vulkan1.2", f], dir);
                Assert.True(code == 0, output);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---- reflection ----

    [Fact]
    public void ReflectionListsConstantsSlotsAndUsePerEntryPoint()
    {
        var r = CompileCorpus("2d/spec_const_pixel.hlsl");
        var model = r.Reflection!;
        Assert.Equal(["Swap", "Strength", "Bands"], model.SpecConstants.Select(s => s.Name).ToArray());
        var strength = model.SpecConstants[1];
        Assert.Equal(1u, strength.Id);
        Assert.Equal(ScalarKind.Float, strength.Scalar);
        Assert.Equal(0.25, strength.DefaultValue);
        Assert.Equal(BitConverter.SingleToUInt32Bits(0.25f), strength.DefaultBits);
        Assert.Equal(
            [new SpecConstantSlot(CompileTarget.SpirV, 1, "Strength"), new SpecConstantSlot(CompileTarget.Msl, 1, "Strength"), new SpecConstantSlot(CompileTarget.Wgsl, 1, "Strength")],
            strength.Slots.ToArray());
        // Only the pixel entry point reads them.
        Assert.Empty(model.EntryPoints.Single(e => e.Name == "VSMain").UsedSpecConstants);
        Assert.Equal(["Swap", "Strength", "Bands"], model.EntryPoints.Single(e => e.Name == "PSMain").UsedSpecConstants.ToArray());
    }

    [Fact]
    public void ReflectionRoundTripsThroughTheTextFormat()
    {
        var model = CompileCorpus("compute/spec_const_compute.hlsl").Reflection!;
        string text = ReflectionFormat.Write(model);
        Assert.Contains("spec name=\"Rounds\" id=1 scalar=Int default=4", text, StringComparison.Ordinal);
        Assert.Contains("  specuse name=\"Rounds\"", text, StringComparison.Ordinal);
        Assert.True(ReflectionFormat.TryRead(text, out var back, out string? error), error);
        Assert.Equal(text, ReflectionFormat.Write(back!));
        Assert.Equal(4, back!.SpecConstants.Count);
    }

    [Fact]
    public void ReflectionOfAShaderWithoutConstantsIsUnchanged()
    {
        var model = CompileCorpus("2d/blit.hlsl").Reflection!;
        string text = ReflectionFormat.Write(model);
        Assert.DoesNotContain("spec", text.Replace("specular", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void AConstantUsedOnlyByCodeTheOptimizerRemovedIsNotAnInput()
    {
        const string source = "[[vk::constant_id(0)]] const int Dead = 1;\n[[vk::constant_id(1)]] const int Live = 2;\nRWStructuredBuffer<int> o;\n"
            + "[shader(\"compute\")] [numthreads(1,1,1)] void main() { int unused = Dead * 5; o[0] = Live; }";
        var r = Compile(source, "dead.hlsl");
        Assert.True(r.Success, Describe(r));
        Assert.Equal(["Live"], r.Reflection!.EntryPoints[0].UsedSpecConstants.ToArray());
        Assert.Equal(2, r.Reflection.SpecConstants.Count);
        Assert.DoesNotContain("Dead", r.Find("main", CompileTarget.Wgsl)!.AsText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Dead", r.Find("main", CompileTarget.Msl)!.AsText(), StringComparison.Ordinal);
    }

    // ---- layout helpers ----

    private static EntryLayout KernelLayout()
    {
        var model = CompileCorpus("compute/spec_const_compute.hlsl").Reflection!;
        Assert.True(EntryLayout.TryCreate(model, "CSMain", out var layout, out var error), error?.Message);
        return layout!;
    }

    [Fact]
    public void ValuesStartAtDefaultsAndConvertForVulkan()
    {
        var entry = KernelLayout();
        Assert.Equal(["UseOffset", "Rounds", "Mask", "Scale"], entry.SpecConstants.Select(c => c.Name).ToArray());
        var values = SpecializationValues.For(entry);
        var vk = values.ToVulkan();
        Assert.Equal([new Layouts.Vulkan.SpecializationMapEntry(0, 0, 4), new(1, 4, 4), new(2, 8, 4), new(3, 12, 4)], vk.MapEntries.ToArray());
        Assert.Equal(
            [0u, 4u, 255u, BitConverter.SingleToUInt32Bits(0.5f)],
            Enumerable.Range(0, 4).Select(i => BitConverter.ToUInt32(vk.Data, i * 4)).ToArray());

        values.Set("UseOffset", true).Set("Rounds", 7).Set("Mask", 63u).Set("Scale", 2.25f);
        vk = values.ToVulkan();
        Assert.Equal(
            [1u, 7u, 63u, BitConverter.SingleToUInt32Bits(2.25f)],
            Enumerable.Range(0, 4).Select(i => BitConverter.ToUInt32(vk.Data, i * 4)).ToArray());
    }

    [Fact]
    public void ValuesConvertForMetalAndWebGpu()
    {
        var values = SpecializationValues.For(KernelLayout()).Set("UseOffset", true).Set("Rounds", -3).Set("Scale", 1.5f);
        var metal = values.ToMetal();
        Assert.Equal([0u, 1u, 2u, 3u], metal.Select(m => m.Index).ToArray());
        Assert.Equal(
            [Layouts.Metal.FunctionConstantType.Bool, Layouts.Metal.FunctionConstantType.Int, Layouts.Metal.FunctionConstantType.UInt, Layouts.Metal.FunctionConstantType.Float],
            metal.Select(m => m.Type).ToArray());
        Assert.True(metal[0].AsBool);
        Assert.Equal(-3, metal[1].AsInt);
        Assert.Equal(1.5f, metal[3].AsFloat);

        var web = values.ToWebGpu();
        Assert.Equal(["0", "1", "2", "3"], web.Keys.ToArray());
        Assert.Equal([1.0, -3.0, 255.0, 1.5], web.Values.ToArray());
    }

    [Fact]
    public void MetalDataTypeNumbersAreThoseOfMtlDataType()
    {
        // MTLDataType: float = 3, int = 29, uint = 33, bool = 53 (Metal headers).
        Assert.Equal(3u, Layouts.Metal.FunctionConstantType.Float.DataType);
        Assert.Equal(29u, Layouts.Metal.FunctionConstantType.Int.DataType);
        Assert.Equal(33u, Layouts.Metal.FunctionConstantType.UInt.DataType);
        Assert.Equal(53u, Layouts.Metal.FunctionConstantType.Bool.DataType);
    }

    [Fact]
    public void SettingTheWrongNameOrTypeSaysWhat()
    {
        var values = SpecializationValues.For(KernelLayout());
        var unknown = Assert.Throws<ArgumentException>(() => values.Set("Nope", 1));
        Assert.Contains("Nope", unknown.Message, StringComparison.Ordinal);
        var wrong = Assert.Throws<ArgumentException>(() => values.Set("Rounds", 1.5f));
        Assert.Contains("Int", wrong.Message, StringComparison.Ordinal);
        Assert.Equal(4.0, values.ValueOf("Rounds"));
    }

    [Fact]
    public void AnEntryLayoutHasOnlyTheConstantsItReads()
    {
        var model = CompileCorpus("2d/spec_const_pixel.hlsl").Reflection!;
        Assert.True(EntryLayout.TryCreate(model, "VSMain", out var vs, out _));
        Assert.Empty(vs!.SpecConstants);
        Assert.True(EntryLayout.TryCreate(model, "PSMain", out var ps, out _));
        Assert.Equal(3, ps!.SpecConstants.Count);
        Assert.Empty(SpecializationValues.For(vs).ToVulkan().MapEntries);
    }

    [Fact]
    public void HotReloadAcceptsNewConstantsAndChangedDefaultsButNotChangedIdsOrRemovals()
    {
        const string head = "RWStructuredBuffer<int> o;\n[shader(\"compute\")] [numthreads(1,1,1)] void main() { o[0] = A + B; }";
        var built = Compile("[[vk::constant_id(0)]] const int A = 1;\n[[vk::constant_id(1)]] const int B = 2;\n" + head).Reflection!;
        var changedDefault = Compile("[[vk::constant_id(0)]] const int A = 9;\n[[vk::constant_id(1)]] const int B = 2;\n[[vk::constant_id(2)]] const int C = 2;\n" + head).Reflection!;
        Assert.True(InterfaceCompatibility.Compare(built, changedDefault).IsCompatible);
        var moved = Compile("[[vk::constant_id(0)]] const int A = 1;\n[[vk::constant_id(5)]] const int B = 2;\n" + head).Reflection!;
        var d = Assert.Single(InterfaceCompatibility.Compare(built, moved).Differences);
        Assert.Equal(InterfaceDifferenceKind.SpecConstant, d.Kind);
        Assert.Contains("B changed id from 1 to 5", d.Message, StringComparison.Ordinal);
        var retyped = Compile("[[vk::constant_id(0)]] const uint A = 1;\n[[vk::constant_id(1)]] const int B = 2;\n" + head).Reflection!;
        Assert.Contains("changed type from Int to UInt", Assert.Single(InterfaceCompatibility.Compare(built, retyped).Differences).Message, StringComparison.Ordinal);
        var removed = Compile("[[vk::constant_id(0)]] const int A = 1;\n" + head.Replace("A + B", "A", StringComparison.Ordinal)).Reflection!;
        Assert.Contains("B was removed", Assert.Single(InterfaceCompatibility.Compare(built, removed).Differences).Message, StringComparison.Ordinal);
    }

    // ---- baking ----

    [Fact]
    public void OverridingAtCompileTimeTurnsConstantsIntoPlainOnes()
    {
        var options = new CompileOptions
        {
            Targets = All,
            SpecializationOverrides = [new("UseOffset", 1), new("Rounds", 7), new("Mask", 63), new("Scale", 2.25)],
        };
        var r = CompileCorpus("compute/spec_const_compute.hlsl", options);
        Assert.True(r.Success, Describe(r));
        Assert.Empty(r.Diagnostics);
        Assert.Empty(r.Reflection!.SpecConstants);
        Assert.Empty(r.Reflection.EntryPoints[0].UsedSpecConstants);
        var ops = Opcodes(r.Find("CSMain", CompileTarget.SpirV)!.ToArray());
        Assert.DoesNotContain(OpSpecConstant, ops);
        Assert.DoesNotContain(OpSpecConstantTrue, ops);
        Assert.DoesNotContain(OpSpecConstantFalse, ops);
        Assert.DoesNotContain("override", r.Find("CSMain", CompileTarget.Wgsl)!.AsText(), StringComparison.Ordinal);
        Assert.DoesNotContain("function_constant", r.Find("CSMain", CompileTarget.Msl)!.AsText(), StringComparison.Ordinal);
    }

    [Fact]
    public void BakingOneConstantLeavesTheOthers()
    {
        var r = CompileCorpus("compute/spec_const_compute.hlsl", new CompileOptions { SpecializationOverrides = [new("Rounds", 9)] });
        Assert.True(r.Success, Describe(r));
        Assert.Equal(["UseOffset", "Mask", "Scale"], r.Reflection!.SpecConstants.Select(s => s.Name).ToArray());
    }

    [Fact]
    public void BakedAndPipelineValuesAgreeOnFoldedCode()
    {
        // The folded shader must not keep the branch on the baked bool.
        const string source = "[[vk::constant_id(0)]] const bool Fast = false;\nRWStructuredBuffer<int> o;\n"
            + "[shader(\"compute\")] [numthreads(1,1,1)] void main() { int v = 5; if (Fast) { v = 9; } o[0] = v; }";
        var open = Compile(source, "b.hlsl");
        var baked = Compile(source, "b.hlsl", new CompileOptions { SpecializationOverrides = [new("Fast", 1)] });
        Assert.True(baked.Success, Describe(baked));
        var openOps = Opcodes(open.Find("main", CompileTarget.SpirV)!.ToArray());
        var bakedOps = Opcodes(baked.Find("main", CompileTarget.SpirV)!.ToArray());
        Assert.True(openOps.Contains(OpBranchConditional) || openOps.Contains(OpSelect));
        Assert.DoesNotContain(OpBranchConditional, bakedOps);
        Assert.DoesNotContain(OpSelect, bakedOps);
    }

    [Theory]
    [InlineData("UseOffset", 2.0)]
    [InlineData("Rounds", 1.5)]
    [InlineData("Mask", -1.0)]
    [InlineData("Scale", double.MaxValue)]
    public void ABakedValueThatDoesNotFitIsAnError(string name, double value)
    {
        var r = CompileCorpus("compute/spec_const_compute.hlsl", new CompileOptions { SpecializationOverrides = [new(name, value)] });
        var d = OnlyError(r);
        Assert.Equal("OSH0335", d.Code);
        Assert.Contains(name, d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABakedNameTheShaderDoesNotDeclareIsAWarning()
    {
        var r = CompileCorpus("compute/spec_const_compute.hlsl", new CompileOptions { SpecializationOverrides = [new("Typo", 1)] });
        Assert.True(r.Success, Describe(r));
        var w = Assert.Single(r.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, w.Severity);
        Assert.Equal("OSH0335", w.Code);
        Assert.Contains("Typo", w.Message, StringComparison.Ordinal);
        Assert.Equal(4, r.Reflection!.SpecConstants.Count);
    }
}
