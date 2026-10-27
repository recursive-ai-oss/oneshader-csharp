using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using OneShader.Build.Tasks;
using OneShader.Layouts;
using OneShader.Reflection;
using Xunit;

namespace OneShader.Build.Tests;

/// <summary>Specialization constants through the build task and generator: ids and defaults equal reflection, and the data blob for overrides is what the layout helpers build.</summary>
public sealed class SpecConstantBuildTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "osh-spec-" + Guid.NewGuid().ToString("N"));

    public SpecConstantBuildTests() => Directory.CreateDirectory(Path.Combine(_dir, "Shaders"));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Engine : IBuildEngine
    {
        public List<BuildErrorEventArgs> Errors { get; } = [];

        public bool ContinueOnError => false;

        public int LineNumberOfTaskNode => 0;

        public int ColumnNumberOfTaskNode => 0;

        public string ProjectFileOfTaskNode => "";

        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs) => true;

        public void LogCustomEvent(CustomBuildEventArgs e)
        {
        }

        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e);

        public void LogMessageEvent(BuildMessageEventArgs e)
        {
        }

        public void LogWarningEvent(BuildWarningEventArgs e)
        {
        }
    }

    private string Generate(string corpusFile, string className)
    {
        string path = Path.Combine(_dir, "Shaders", className + ".hlsl");
        File.WriteAllText(path, File.ReadAllText(Path.Combine(Harness.RepoRoot, "corpus", corpusFile)));
        var engine = new Engine();
        var task = new CompileShaders
        {
            BuildEngine = engine,
            Shaders = [new TaskItem(path)],
            OutputDirectory = "obj/oneshader",
            ProjectDirectory = _dir,
            DefaultNamespace = "T.Shaders",
            DefaultTargets = "SpirV;Msl;Wgsl",
        };
        Assert.True(task.Execute(), string.Join("; ", engine.Errors.Select(e => e.Message)));
        string file = Path.Combine(_dir, "obj/oneshader", "Shaders", className + ".hlsl.oneshader");
        var (sources, diagnostics) = Harness.Generate((file, File.ReadAllText(file)));
        Assert.Empty(diagnostics);
        return Assert.Single(sources).Text;
    }

    // Built next to the generated code, as a consumer's code would be, and run here.
    private const string Probe = """
        namespace T.Check;

        public static class Probe
        {
            public static byte[] OverriddenBlob()
            {
                var s = global::T.Shaders.Kernel.Specialization.Defaults;
                s.UseOffset = 1u;
                s.Rounds = 7;
                s.Mask = 63u;
                s.Scale = 2.25f;
                return s.ToVulkan(global::T.Shaders.Kernel.CSMain.SpecConstantIds).Data;
            }

            public static string[] OverriddenWebGpu()
            {
                var s = global::T.Shaders.Kernel.Specialization.Defaults;
                s.UseOffset = 1u;
                s.Scale = 2.25f;
                var map = s.ToWebGpu(global::T.Shaders.Kernel.CSMain.SpecConstantIds);
                return map.Select(kv => kv.Key + "=" + kv.Value.ToString("R", global::System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            }

            public static uint[] MetalBits()
            {
                var s = global::T.Shaders.Kernel.Specialization.Defaults;
                s.Rounds = -1;
                return s.ToMetal().Select(v => v.Bits).ToArray();
            }

            public static uint DefaultsSize => global::T.Shaders.Kernel.Specialization.Size;

            public static uint RoundsId => global::T.Shaders.Kernel.SpecializationConstants.Rounds.Id;

            public static int RoundsDefault => global::T.Shaders.Kernel.SpecializationConstants.Rounds.Default;

            public static float ScaleDefault => global::T.Shaders.Kernel.SpecializationConstants.Scale.Default;

            public static bool UseOffsetDefault => global::T.Shaders.Kernel.SpecializationConstants.UseOffset.Default;

            public static uint MaskDefault => global::T.Shaders.Kernel.SpecializationConstants.Mask.Default;
        }
        """;

    [Fact]
    public void IdsAndDefaultsEqualReflectionAndTheBlobEqualsTheLayoutHelpers()
    {
        string generated = Generate("compute/spec_const_compute.hlsl", "Kernel");
        Assert.Contains("public static class SpecializationConstants", generated, StringComparison.Ordinal);
        Assert.Contains("public static global::System.ReadOnlySpan<uint> SpecConstantIds", generated, StringComparison.Ordinal);

        var asm = Harness.Compile([generated, "using System.Linq;\n" + Probe], out var context);
        try
        {
            var shader = asm.GetType("T.Shaders.Kernel")!;
            var reflection = (ShaderReflection)shader.GetProperty("Reflection")!.GetValue(null)!;
            var probe = asm.GetType("T.Check.Probe")!;
            object? Get(string member) => probe.GetProperty(member)!.GetValue(null);

            // Constants: the generated ids and defaults are the reflection's.
            var byName = reflection.SpecConstants.ToDictionary(s => s.Name);
            Assert.Equal(byName["Rounds"].Id, (uint)Get("RoundsId")!);
            Assert.Equal(byName["Rounds"].DefaultValue, (int)Get("RoundsDefault")!);
            Assert.Equal(byName["Scale"].DefaultValue, (float)Get("ScaleDefault")!);
            Assert.Equal(byName["Mask"].DefaultValue, (uint)Get("MaskDefault")!);
            Assert.Equal(byName["UseOffset"].DefaultValue == 1, (bool)Get("UseOffsetDefault")!);
            Assert.Equal((uint)reflection.SpecConstants.Count * 4, (uint)Get("DefaultsSize")!);

            // The entry class lists the ids it reads, and its layout carries the constants.
            var layout = (EntryLayout)shader.GetNestedType("CSMain")!.GetProperty("Layout")!.GetValue(null)!;
            Assert.Equal(reflection.SpecConstants.Select(s => s.Id), layout.SpecConstants.Select(c => c.Id));
            Assert.Equal(reflection.SpecConstants.Select(s => s.DefaultBits), layout.SpecConstants.Select(c => c.DefaultBits));

            // The blob for overrides: the generated struct and the reflection-driven helper give the same bytes, which the parity harness
            // also builds (it uses SpecializationValues from the case's spec lines).
            var expected = SpecializationValues.For(layout).Set("UseOffset", true).Set("Rounds", 7).Set("Mask", 63u).Set("Scale", 2.25f).ToVulkan();
            byte[] blob = (byte[])probe.GetMethod("OverriddenBlob")!.Invoke(null, null)!;
            Assert.Equal(expected.Data, blob);
            Assert.Equal(
                [1u, 7u, 63u, BitConverter.SingleToUInt32Bits(2.25f)],
                Enumerable.Range(0, 4).Select(i => BitConverter.ToUInt32(blob, i * 4)).ToArray());

            var web = (string[])probe.GetMethod("OverriddenWebGpu")!.Invoke(null, null)!;
            Assert.Equal(["0=1", "1=4", "2=255", "3=2.25"], web);
            var bits = (uint[])probe.GetMethod("MetalBits")!.Invoke(null, null)!;
            Assert.Equal(unchecked((uint)-1), bits[1]);
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void OnlyTheEntryPointsThatReadConstantsListThem()
    {
        string generated = Generate("2d/spec_const_pixel.hlsl", "Tint");
        var asm = Harness.Compile([generated], out var context);
        try
        {
            var shader = asm.GetType("T.Shaders.Tint")!;
            Assert.Null(shader.GetNestedType("VSMain")!.GetProperty("SpecConstantIds"));
            Assert.NotNull(shader.GetNestedType("PSMain")!.GetProperty("SpecConstantIds"));
            Assert.Equal(["Swap", "Strength", "Bands"], shader.GetNestedType("SpecializationConstants")!.GetNestedTypes().Select(t => t.Name).ToArray());
            var vs = (EntryLayout)shader.GetNestedType("VSMain")!.GetProperty("Layout")!.GetValue(null)!;
            Assert.Empty(vs.SpecConstants);
            var ps = (EntryLayout)shader.GetNestedType("PSMain")!.GetProperty("Layout")!.GetValue(null)!;
            Assert.Equal(3, ps.SpecConstants.Count);
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void AShaderWithoutConstantsGeneratesNoSpecializationTypes()
    {
        string generated = Generate("2d/blit.hlsl", "Blit");
        Assert.DoesNotContain("Specialization", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("SpecConstantIds", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void AConstantNamedLikeAGeneratedMemberIsReportedAsAClash()
    {
        string path = Path.Combine(_dir, "Shaders", "Clash.hlsl");
        File.WriteAllText(path, "[[vk::constant_id(0)]] const int Size = 1;\nRWStructuredBuffer<int> o;\n[shader(\"compute\")] [numthreads(1,1,1)] void main() { o[0] = Size; }\n");
        var engine = new Engine();
        var task = new CompileShaders
        {
            BuildEngine = engine,
            Shaders = [new TaskItem(path)],
            OutputDirectory = "obj/oneshader",
            ProjectDirectory = _dir,
            DefaultNamespace = "T.Shaders",
        };
        Assert.True(task.Execute(), string.Join("; ", engine.Errors.Select(e => e.Message)));
        string file = Path.Combine(_dir, "obj/oneshader", "Shaders", "Clash.hlsl.oneshader");
        var (sources, diagnostics) = Harness.Generate((file, File.ReadAllText(file)));
        Assert.Empty(sources);
        Assert.Contains("Size", Assert.Single(diagnostics).GetMessage(null), StringComparison.Ordinal);
    }
}
