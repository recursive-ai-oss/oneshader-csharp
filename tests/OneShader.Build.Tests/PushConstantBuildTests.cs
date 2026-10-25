using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using OneShader.Build.Tasks;
using Xunit;

namespace OneShader.Build.Tests;

/// <summary>Push constants through the task and generator: the generated struct, its offsets, and the per-target delivery constants.</summary>
public sealed class PushConstantBuildTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "osh-push-" + Guid.NewGuid().ToString("N"));

    public PushConstantBuildTests() => Directory.CreateDirectory(Path.Combine(_dir, "Shaders"));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Engine : IBuildEngine
    {
        public List<BuildErrorEventArgs> Errors { get; } = [];

        public List<BuildWarningEventArgs> Warnings { get; } = [];

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

        public void LogWarningEvent(BuildWarningEventArgs e) => Warnings.Add(e);
    }

    private (string Output, Engine Engine, bool Ok) Build(string name, string text, string outDir = "obj/oneshader")
    {
        string path = Path.Combine(_dir, "Shaders", name + ".hlsl");
        File.WriteAllText(path, text);
        var engine = new Engine();
        var task = new CompileShaders
        {
            BuildEngine = engine,
            Shaders = [new TaskItem(path)],
            OutputDirectory = outDir,
            ProjectDirectory = _dir,
            DefaultNamespace = "T.Shaders",
            DefaultTargets = "SpirV;Msl;Wgsl",
        };
        bool ok = task.Execute();
        return (Path.Combine(_dir, outDir, "Shaders", name + ".hlsl.oneshader"), engine, ok);
    }

    private static string Generate(string oneshaderFile)
    {
        var (sources, diagnostics) = Harness.Generate((oneshaderFile, File.ReadAllText(oneshaderFile)));
        Assert.Empty(diagnostics);
        return Assert.Single(sources).Text;
    }

    private static string PushLit => File.ReadAllText(Path.Combine(Harness.RepoRoot, "corpus", "3d", "push_lit.hlsl"));

    [Fact]
    public void GeneratedStructOffsetsEqualReflectionAndSpirvAndTheDeliveryConstantsAreListed()
    {
        var (output, engine, ok) = Build("PushLit", PushLit);
        Assert.True(ok, string.Join("; ", engine.Errors.Select(e => e.Message)));
        Assert.Empty(engine.Warnings);
        string generated = Generate(output);

        var compiled = ShaderCompiler.Compile(PushLit, "Shaders/PushLit.hlsl", new CompileOptions { Targets = [CompileTarget.SpirV, CompileTarget.Msl, CompileTarget.Wgsl] }, TestContext.Current.CancellationToken);
        Assert.True(compiled.Success);
        var push = Assert.Single(compiled.Reflection!.PushConstants);

        var asm = Harness.Compile([generated], out var ctx);
        try
        {
            var shaders = asm.GetType("T.Shaders.PushLit")!;
            var type = shaders.GetNestedType("PushConstants")!;
            Assert.NotNull(type);
            var layout = type.StructLayoutAttribute!;
            Assert.Equal(LayoutKind.Explicit, layout.Value);
            Assert.Equal((int)push.Layout.Size, layout.Size);
            Assert.Equal(push.Layout.Size, (uint)type.GetField("Size")!.GetRawConstantValue()!);
            Assert.Equal(push.Layout.Size, (uint)Marshal.SizeOf(Activator.CreateInstance(type)!));

            var offsets = new Dictionary<string, int>();
            var allOffsets = new HashSet<int>();
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                int value = f.GetCustomAttribute<FieldOffsetAttribute>()!.Value;
                allOffsets.Add(value);
                if (f.IsPublic)
                {
                    offsets[f.Name] = value;
                }
            }

            foreach (var m in push.Layout.Members)
            {
                if (type.GetProperty(m.Name) is not null)
                {
                    // A matrix is a property over private row fields; its first row sits at the member's offset.
                    Assert.Contains((int)m.Offset, allOffsets);
                    continue;
                }

                Assert.True(offsets.TryGetValue(m.Name, out int off) || offsets.TryGetValue(m.Name + "_0", out off), "no generated field for " + m.Name + "; fields: " + string.Join(",", offsets.Keys));
                Assert.Equal((int)m.Offset, off);
            }

            // The same offsets are in the SPIR-V the build embedded.
            string? dis = Tool("spirv-dis");
            if (dis is not null)
            {
                string spv = Path.Combine(_dir, "vs.spv");
                File.WriteAllBytes(spv, compiled.Find("VSMain", CompileTarget.SpirV)!.ToArray());
                var psi = new ProcessStartInfo(dis, spv) { RedirectStandardOutput = true };
                using var p = Process.Start(psi)!;
                string text = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                string block = Regex.Match(text, @"OpVariable %_ptr_PushConstant_(\w+) PushConstant").Groups[1].Value;
                var spirv = Regex.Matches(text, @"OpMemberDecorate %" + Regex.Escape(block) + @" (\d+) Offset (\d+)")
                    .ToDictionary(x => int.Parse(x.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), x => uint.Parse(x.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
                Assert.Equal(push.Layout.Members.Count, spirv.Count);
                for (int i = 0; i < push.Layout.Members.Count; i++)
                {
                    Assert.Equal(push.Layout.Members[i].Offset, spirv[i]);
                }
            }

            // Delivery constants: the range's stages and size, the Metal index and the WebGPU group and binding, all from reflection.
            var bindings = shaders.GetNestedType("Bindings")!.GetNestedType("PushConstants")!;
            Assert.Equal(push.Layout.Size, (uint)bindings.GetField("Size")!.GetRawConstantValue()!);
            Assert.Equal((int)(ShaderStages.Vertex | ShaderStages.Pixel), (int)bindings.GetField("Stages")!.GetRawConstantValue()!);
            Assert.Equal(push.Slots.Single(s => s.Target == CompileTarget.Msl).Binding, (uint)bindings.GetField("MslIndex")!.GetRawConstantValue()!);
            var wgsl = push.Slots.Single(s => s.Target == CompileTarget.Wgsl);
            Assert.Equal(wgsl.Set, (uint)bindings.GetField("WgslGroup")!.GetRawConstantValue()!);
            Assert.Equal(wgsl.Binding, (uint)bindings.GetField("WgslBinding")!.GetRawConstantValue()!);
        }
        finally
        {
            ctx.Unload();
        }
    }

    [Fact]
    public void BuildingTwiceGivesIdenticalFilesAndCSharp()
    {
        var (output, _, ok) = Build("PushLit", PushLit);
        Assert.True(ok);
        byte[] first = File.ReadAllBytes(output);
        string generated = Generate(output);
        var (output2, _, ok2) = Build("PushLit", PushLit, "obj/again");
        Assert.True(ok2);
        Assert.Equal(first, File.ReadAllBytes(output2));
        Assert.Equal(generated, Generate(output2));
    }

    [Fact]
    public void ABlockOver128BytesIsABuildWarningOnTheHlslLine()
    {
        const string source = "struct Big { float4 v[9]; };\n[[vk::push_constant]] Big Pc;\n[shader(\"vertex\")] float4 VSMain(float4 p : POSITION) : SV_Position { return p + Pc.v[8]; }\n";
        var (_, engine, ok) = Build("Big", source);
        Assert.True(ok, string.Join("; ", engine.Errors.Select(e => e.Message)));
        var w = Assert.Single(engine.Warnings);
        Assert.Equal("OSH0334", w.Code);
        Assert.Equal(Path.Combine(_dir, "Shaders", "Big.hlsl"), w.File);
        Assert.Equal((2, 27), (w.LineNumber, w.ColumnNumber));
        Assert.Contains("128", w.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoBlocksInOneEntryAreABuildErrorOnTheSecondBlock()
    {
        const string source = "struct A { float4 a; };\nstruct B { float4 b; };\n[[vk::push_constant]] A First;\n[[vk::push_constant]] B Second;\n[shader(\"vertex\")] float4 VSMain(float4 p : POSITION) : SV_Position { return p + First.a + Second.b; }\n";
        var (_, engine, ok) = Build("Two", source);
        Assert.False(ok);
        var e = Assert.Single(engine.Errors);
        Assert.Equal("OSH0333", e.Code);
        Assert.Equal(Path.Combine(_dir, "Shaders", "Two.hlsl"), e.File);
        Assert.Equal((4, 25), (e.LineNumber, e.ColumnNumber));
    }

    private static string? Tool(string name)
    {
        // Tool binaries carry the .exe suffix on Windows.
        name += (OperatingSystem.IsWindows() ? ".exe" : "");
        string? tools = Environment.GetEnvironmentVariable("ONESHADER_TOOLS");
        if (!string.IsNullOrEmpty(tools) && File.Exists(Path.Combine(tools, "bin", name)))
        {
            return Path.Combine(tools, "bin", name);
        }

        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(dir, name)))
            {
                return Path.Combine(dir, name);
            }
        }

        return null;
    }
}
