using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using OneShader.Build.Tasks;
using Xunit;

namespace OneShader.Build.Tests;

/// <summary>The task and generator with MSL and WGSL selected: blobs, binding numbers, refusals, determinism.</summary>
public sealed class TargetTests : IDisposable
{
    private const string AllTargets = "SpirV;Msl;Wgsl";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "osh-targets-" + Guid.NewGuid().ToString("N"));

    public TargetTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "Shaders"));
    }

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

    private static string SampleShader => File.ReadAllText(Path.Combine(Harness.RepoRoot, "samples", "Sprite", "Shaders", "Sprite.hlsl"));

    private (bool Ok, Engine Engine, string Output) Run(string name, string text, string targets, string itemTargets = "", string outDir = "obj/oneshader")
    {
        string path = Path.Combine(_dir, "Shaders", name + ".hlsl");
        File.WriteAllText(path, text);
        var engine = new Engine();
        var item = new TaskItem(path);
        if (itemTargets.Length > 0)
        {
            item.SetMetadata("Targets", itemTargets);
        }

        var task = new CompileShaders
        {
            BuildEngine = engine,
            Shaders = [item],
            OutputDirectory = outDir,
            ProjectDirectory = _dir,
            DefaultNamespace = "T.Shaders",
            DefaultTargets = targets,
        };
        bool ok = task.Execute();
        return (ok, engine, Path.Combine(_dir, outDir, "Shaders", name + ".hlsl.oneshader"));
    }

    private static string Generate(string oneshaderFile)
    {
        var (sources, diagnostics) = Harness.Generate((oneshaderFile, File.ReadAllText(oneshaderFile)));
        Assert.Empty(diagnostics);
        return Assert.Single(sources).Text;
    }

    private const string Probe = """
        namespace Probe;
        public static class Sprite
        {
            public static byte[] Msl(string entry) => entry == "VSMain" ? T.Shaders.Sprite.VSMain.Msl.ToArray() : T.Shaders.Sprite.PSMain.Msl.ToArray();
            public static byte[] Wgsl(string entry) => entry == "VSMain" ? T.Shaders.Sprite.VSMain.Wgsl.ToArray() : T.Shaders.Sprite.PSMain.Wgsl.ToArray();
            public static byte[] SpirV(string entry) => entry == "VSMain" ? T.Shaders.Sprite.VSMain.SpirV.ToArray() : T.Shaders.Sprite.PSMain.SpirV.ToArray();
            public static string MslName(string entry) => entry == "VSMain" ? T.Shaders.Sprite.VSMain.MslFunctionName : T.Shaders.Sprite.PSMain.MslFunctionName;
            public static string WgslName(string entry) => entry == "VSMain" ? T.Shaders.Sprite.VSMain.WgslEntryPoint : T.Shaders.Sprite.PSMain.WgslEntryPoint;
            public static uint VertexBuffer => T.Shaders.Sprite.VSMain.MslVertexBufferIndex;
            public static uint[] MslIndices => new[] { T.Shaders.Sprite.Bindings.Frame.MslIndex, T.Shaders.Sprite.Bindings.Atlas.MslIndex, T.Shaders.Sprite.Bindings.AtlasSampler.MslIndex };
            public static uint[] BindingMetal()
            {
                var all = T.Shaders.Sprite.Bindings.All;
                var r = new uint[all.Length];
                for (int i = 0; i < r.Length; i++) { r[i] = all[i].MetalIndex; }
                return r;
            }
        }
        """;

    [Fact]
    public void GeneratedMslAndWgslEqualTheCompilersOutputAndMetalIndicesEqualReflection()
    {
        var (ok, engine, output) = Run("Sprite", SampleShader, AllTargets);
        Assert.True(ok, string.Join("; ", engine.Errors.Select(e => e.Message)));
        string generated = Generate(output);

        var compiled = ShaderCompiler.Compile(
            SampleShader,
            "Shaders/Sprite.hlsl",
            new CompileOptions { Targets = [CompileTarget.SpirV, CompileTarget.Msl, CompileTarget.Wgsl] },
            TestContext.Current.CancellationToken);
        Assert.True(compiled.Success);

        var asm = Harness.Compile([generated, Probe], out var ctx);
        try
        {
            var probe = asm.GetType("Probe.Sprite")!;
            T Call<T>(string method, params object[] args) => (T)probe.GetMethod(method)!.Invoke(null, args)!;
            foreach (string entry in new[] { "VSMain", "PSMain" })
            {
                Assert.Equal(compiled.Find(entry, CompileTarget.SpirV)!.ToArray(), Call<byte[]>("SpirV", entry));
                Assert.Equal(compiled.Find(entry, CompileTarget.Msl)!.ToArray(), Call<byte[]>("Msl", entry));
                Assert.Equal(compiled.Find(entry, CompileTarget.Wgsl)!.ToArray(), Call<byte[]>("Wgsl", entry));
                Assert.Equal(Msl.MslBackend.EntryFunctionName(entry), Call<string>("MslName", entry));
                Assert.Contains("fn " + Call<string>("WgslName", entry) + "(", compiled.Find(entry, CompileTarget.Wgsl)!.AsText(), StringComparison.Ordinal);
                Assert.Contains(Call<string>("MslName", entry) + "(", compiled.Find(entry, CompileTarget.Msl)!.AsText(), StringComparison.Ordinal);
            }

            Assert.Equal((uint)Msl.MslBackend.VertexBufferIndex, (uint)probe.GetProperty("VertexBuffer")!.GetValue(null)!);

            // The generated indices are the ones in reflection, in resource order.
            var expected = compiled.Reflection!.Resources.Select(r => r.Slots.Single(s => s.Target == CompileTarget.Msl).Binding).ToArray();
            Assert.Equal(expected, (uint[])probe.GetProperty("MslIndices")!.GetValue(null)!);
            Assert.Equal(expected, Call<uint[]>("BindingMetal"));

            // The Vulkan numbers are unchanged by the extra targets.
            foreach (var r in compiled.Reflection.Resources)
            {
                var slot = r.Slots.Single(s => s.Target == CompileTarget.SpirV);
                Assert.Contains($"Set = {slot.Set}u, Binding = {slot.Binding}u", generated, StringComparison.Ordinal);
            }
        }
        finally
        {
            ctx.Unload();
        }
    }

    [Fact]
    public void DefaultTargetsGenerateSpirVOnlyAndPerItemTargetsOverride()
    {
        var (ok, _, output) = Run("Sprite", SampleShader, "");
        Assert.True(ok);
        string spirvOnly = Generate(output);
        Assert.DoesNotContain("Msl", spirvOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("Wgsl", spirvOnly, StringComparison.Ordinal);

        // The item's Targets wins over the project's.
        var (ok2, _, output2) = Run("Sprite", SampleShader, "SpirV", itemTargets: "Wgsl", outDir: "obj/two");
        Assert.True(ok2);
        string wgslOnly = Generate(output2);
        Assert.Contains("Wgsl =>", wgslOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("SpirV =>", wgslOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("Msl", wgslOnly, StringComparison.Ordinal);

        // Metal alone still lists the resources, with the Metal index standing in for the binding.
        var (ok3, _, output3) = Run("Sprite", SampleShader, "msl", outDir: "obj/three");
        Assert.True(ok3);
        string mslOnly = Generate(output3);
        Assert.Contains("MslIndex = 0u", mslOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("SpirV =>", mslOnly, StringComparison.Ordinal);
    }

    // A clip distance is valid in SPIR-V and refused by the WGSL backend (WebGPU core has no clip distances).
    private const string ClipShader = "struct VSOut { float4 Position : SV_Position; float Clip : SV_ClipDistance0; };\n"
        + "[shader(\"vertex\")]\nVSOut VSMain(float4 p : POSITION)\n{\n    VSOut o;\n    o.Position = p;\n    o.Clip = p.x;\n    return o;\n}\n";

    [Fact]
    public void ARefusalByATargetIsAnErrorOnlyWhenThatTargetIsSelected()
    {
        var (okDefault, errorsDefault, _) = Run("Clip", ClipShader, "");
        Assert.True(okDefault, string.Join("; ", errorsDefault.Errors.Select(e => e.Message)));

        var (okWgsl, engine, _) = Run("Clip", ClipShader, "SpirV;Wgsl", outDir: "obj/wgsl");
        Assert.False(okWgsl);
        var e = Assert.Single(engine.Errors);
        Assert.Equal("OSH5202", e.Code);
        Assert.Equal(Path.Combine(_dir, "Shaders", "Clip.hlsl"), e.File);
        Assert.True(e.LineNumber >= 1 && e.ColumnNumber >= 1);
        Assert.Contains("Clip", ClipShader.Split('\n')[e.LineNumber - 1], StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownTargetNamesAreReportedWithTheFix()
    {
        var (ok, engine, _) = Run("Sprite", SampleShader, "SpirV;Hlsl");
        Assert.False(ok);
        var e = Assert.Single(engine.Errors);
        Assert.Equal("OSH0903", e.Code);
        Assert.Contains("SpirV, Msl or Wgsl", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildingTwiceGivesIdenticalOutputAndChangingTargetsRecompiles()
    {
        var (ok, _, output) = Run("Sprite", SampleShader, AllTargets);
        Assert.True(ok);
        byte[] first = File.ReadAllBytes(output);
        string generated = Generate(output);

        // Same inputs in another folder: same bytes, same C#, and no machine path in either.
        var (ok2, _, output2) = Run("Sprite", SampleShader, "Wgsl;SpirV;Msl;Msl", outDir: "obj/again");
        Assert.True(ok2);
        Assert.Equal(first, File.ReadAllBytes(output2));
        Assert.Equal(generated, Generate(output2));
        Assert.DoesNotContain(_dir, System.Text.Encoding.UTF8.GetString(first), StringComparison.Ordinal);

        // Reusing the output (nothing changed) leaves the file alone; a different target list replaces it.
        var stamp = File.GetLastWriteTimeUtc(output);
        Assert.True(Run("Sprite", SampleShader, AllTargets).Ok);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(output));
        Assert.True(Run("Sprite", SampleShader, "SpirV").Ok);
        Assert.NotEqual(first, File.ReadAllBytes(output));
    }

    [Fact]
    public void WgslEntryNameIsReadFromTheDeclarationFollowingTheStageAttribute()
    {
        const string text = "struct S { a: f32 }\n\n@compute @workgroup_size(8, 8, 1)\nfn main_(@builtin(global_invocation_id) id: vec3<u32>) {\n}\n";
        Assert.Equal("main_", CompileShaders.WgslEntryName(text, "main"));
        Assert.Equal("fallback", CompileShaders.WgslEntryName("fn helper() {}", "fallback"));
    }
}
