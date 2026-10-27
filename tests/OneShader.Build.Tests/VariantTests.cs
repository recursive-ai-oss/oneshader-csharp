using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using OneShader.Build.Tasks;
using Xunit;

namespace OneShader.Build.Tests;

/// <summary>Shader variants through the task and the generator: keyword axes, named sets, limits, messages, incremental builds.</summary>
public sealed class VariantTests : IDisposable
{
    private const string AllTargets = "SpirV;Msl;Wgsl";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "osh-variants-" + Guid.NewGuid().ToString("N"));

    public VariantTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "Shaders"));
    }

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

    private static string MaterialShader => File.ReadAllText(Path.Combine(Harness.RepoRoot, "samples", "Variants", "Shaders", "Material.hlsl"));

    private sealed record Result(bool Ok, Engine Engine, CompileShaders Task, string Manifest);

    private Result Run(string text, string axes, string targets = "SpirV", string outDir = "obj/oneshader", string name = "Material", string extraMetadata = "", params (string Name, string Defines)[] named)
    {
        string path = Path.Combine(_dir, "Shaders", name + ".hlsl");
        File.WriteAllText(path, text);
        var engine = new Engine();
        var item = new TaskItem(path);
        if (axes.Length > 0)
        {
            item.SetMetadata("Axes", axes);
        }

        foreach (string pair in extraMetadata.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=', StringComparison.Ordinal);
            item.SetMetadata(pair[..eq], pair[(eq + 1)..]);
        }

        var variants = named.Select(n =>
        {
            var v = new TaskItem(n.Name);
            v.SetMetadata("Shader", path);
            v.SetMetadata("Defines", n.Defines);
            return (ITaskItem)v;
        }).ToArray();
        var task = new CompileShaders
        {
            BuildEngine = engine,
            Shaders = [item],
            Variants = variants,
            OutputDirectory = outDir,
            ProjectDirectory = _dir,
            DefaultNamespace = "T.Shaders",
            DefaultTargets = targets,
        };
        bool ok = task.Execute();
        return new Result(ok, engine, task, Path.Combine(_dir, outDir, "Shaders", name + ".hlsl.oneshader"));
    }

    private static string Describe(Engine engine) => string.Join("; ", engine.Errors.Select(e => e.Code + " " + e.Message));

    private static (string Path, string Text)[] Outputs(Result r) =>
        r.Task.GeneratedFiles.Select(f => (f.ItemSpec, File.ReadAllText(f.ItemSpec))).ToArray();

    private static string Generate(Result r)
    {
        var (sources, diagnostics) = Harness.Generate(Outputs(r));
        Assert.Empty(diagnostics);
        return Assert.Single(sources).Text;
    }

    private const string Probe = """
        using System.Linq;
        namespace Probe;
        public static class Material
        {
            private static T.Shaders.Material.Variant Pick(bool alphaTest, int lighting) =>
                T.Shaders.Material.Variants.Get(new T.Shaders.Material.Key(alphaTest, (T.Shaders.Material.LightingValue)lighting));
            public static byte[] SpirV(bool alphaTest, int lighting, string entry) => (entry == "VSMain" ? Pick(alphaTest, lighting).VSMain : Pick(alphaTest, lighting).PSMain).SpirV.ToArray();
            public static byte[] Msl(bool alphaTest, int lighting, string entry) => (entry == "VSMain" ? Pick(alphaTest, lighting).VSMain : Pick(alphaTest, lighting).PSMain).Msl.ToArray();
            public static byte[] Wgsl(bool alphaTest, int lighting, string entry) => (entry == "VSMain" ? Pick(alphaTest, lighting).VSMain : Pick(alphaTest, lighting).PSMain).Wgsl.ToArray();
            public static string MslName(bool alphaTest, int lighting) => Pick(alphaTest, lighting).PSMain.MslFunctionName!;
            public static string Defines(bool alphaTest, int lighting) => Pick(alphaTest, lighting).Defines;
            public static string Name(bool alphaTest, int lighting) => Pick(alphaTest, lighting).Name;
            public static string[] BindingNames(bool alphaTest, int lighting) => Pick(alphaTest, lighting).Bindings.ToArray().Select(b => b.Name).ToArray();
            public static int Count => T.Shaders.Material.Variants.Count;
            public static int IndexOf(bool alphaTest, int lighting) => new T.Shaders.Material.Key(alphaTest, (T.Shaders.Material.LightingValue)lighting).Index;
            public static bool TryBad() => T.Shaders.Material.Variants.TryGet(new T.Shaders.Material.Key(true, (T.Shaders.Material.LightingValue)7), out _);
            public static int PipelineStages(bool alphaTest, int lighting) => Pick(alphaTest, lighting).Pipeline!.ToVulkan().SetLayouts.Count;
            public static uint FrameSize => T.Shaders.Material.Frame.Size;
        }
        """;

    private static string? SpirvVal() => Oracles.Find("spirv-val");

    private static void Validate(string dir, byte[] spirv, string name)
    {
        string? val = SpirvVal();
        if (val is null)
        {
            return;
        }

        string file = Path.Combine(dir, name + ".spv");
        File.WriteAllBytes(file, spirv);
        var (code, output) = Oracles.Run(val, ["--target-env", "vulkan1.2", file], dir);
        Assert.True(code == 0, name + ": " + output);
    }

    [Fact]
    public void TwoAxesGiveSixVariantsAndTheLookupReturnsTheCompilersBytesForEveryTarget()
    {
        var r = Run(MaterialShader, "ALPHA_TEST;LIGHTING=NONE|LAMBERT|PBR", AllTargets);
        Assert.True(r.Ok, Describe(r.Engine));

        // The manifest plus one file per variant.
        Assert.Equal(7, r.Task.GeneratedFiles.Length);
        Assert.Equal(6, r.Task.CompiledCount);
        string generated = Generate(r);
        var asm = Harness.Compile([generated, Probe], out var ctx);
        try
        {
            var probe = asm.GetType("Probe.Material")!;
            T Call<T>(string method, params object[] args) => (T)probe.GetMethod(method)!.Invoke(null, args)!;
            string[] lighting = ["NONE", "LAMBERT", "PBR"];
            Assert.Equal(6, (int)probe.GetProperty("Count")!.GetValue(null)!);
            var seen = new HashSet<string>();
            int expectedIndex = 0;
            foreach (bool alpha in new[] { false, true })
            {
                for (int l = 0; l < 3; l++)
                {
                    // Exactly what a direct compile with these defines produces; first axis varies slowest.
                    var defines = new List<KeyValuePair<string, string?>>();
                    if (alpha)
                    {
                        defines.Add(new("ALPHA_TEST", "1"));
                    }

                    defines.Add(new("LIGHTING", lighting[l]));
                    defines.Add(new("LIGHTING_" + lighting[l], "1"));
                    var compiled = ShaderCompiler.Compile(MaterialShader, "Shaders/Material.hlsl", new CompileOptions { Defines = defines, Targets = [CompileTarget.SpirV, CompileTarget.Msl, CompileTarget.Wgsl] }, TestContext.Current.CancellationToken);
                    Assert.True(compiled.Success, string.Join("\n", compiled.Diagnostics));
                    foreach (string entry in new[] { "VSMain", "PSMain" })
                    {
                        byte[] spirv = Call<byte[]>("SpirV", alpha, l, entry);
                        Assert.Equal(compiled.Find(entry, CompileTarget.SpirV)!.ToArray(), spirv);
                        Assert.Equal(compiled.Find(entry, CompileTarget.Msl)!.ToArray(), Call<byte[]>("Msl", alpha, l, entry));
                        Assert.Equal(compiled.Find(entry, CompileTarget.Wgsl)!.ToArray(), Call<byte[]>("Wgsl", alpha, l, entry));
                        Validate(_dir, spirv, "v" + (alpha ? 1 : 0) + l + entry);
                    }

                    Assert.Equal(expectedIndex++, Call<int>("IndexOf", alpha, l));
                    Assert.Equal(Msl.MslBackend.EntryFunctionName("PSMain"), Call<string>("MslName", alpha, l));
                    Assert.Equal((alpha ? "ALPHA_TEST=1 " : "") + "LIGHTING=" + lighting[l], Call<string>("Defines", alpha, l));
                    Assert.True(seen.Add(Call<string>("Name", alpha, l)));

                    // Binding sets differ between variants and the data says so.
                    string[] expectedBindings = (l == 0 ? new[] { "Frame", "Atlas", "AtlasSampler" } : new[] { "Frame", "Atlas", "AtlasSampler", "NormalMap" });
                    Assert.Equal(expectedBindings.OrderBy(n => n, StringComparer.Ordinal), Call<string[]>("BindingNames", alpha, l).OrderBy(n => n, StringComparer.Ordinal));
                    Assert.Equal(1, Call<int>("PipelineStages", alpha, l));
                }
            }

            // The constant buffer agrees in every variant, so there is one struct, at file level.
            Assert.Equal(1, Regex.Count(generated, @"public struct Frame\b"));
            Assert.Equal(80u, (uint)probe.GetProperty("FrameSize")!.GetValue(null)!);
            Assert.False(Call<bool>("TryBad"));
            Assert.Equal(-1, Call<int>("IndexOf", true, 7));
        }
        finally
        {
            ctx.Unload();
        }
    }

    [Fact]
    public void UnboundedArraysAndHeapsWorkInVariants()
    {
        const string source = """
            [[vk::binding(0, 0)]] Texture2D<float4> Tex[] : register(t0);
            [[vk::binding(1, 0)]] SamplerState Smp : register(s0);
            [[vk::binding(2, 0)]] RWStructuredBuffer<float4> Out : register(u0);
            [shader("compute")] [numthreads(4,1,1)]
            void CSMain(uint3 id : SV_DispatchThreadID)
            {
            #ifdef HEAP
                Texture2D<float4> t = ResourceDescriptorHeap[NonUniformResourceIndex(id.x)];
            #else
                Texture2D<float4> t = Tex[NonUniformResourceIndex(id.x)];
            #endif
                Out[id.x] = t.SampleLevel(Smp, float2(0.5, 0.5), 0);
            }
            """;
        var r = Run(source, "HEAP", "SpirV;Msl", name: "Bindless");
        Assert.True(r.Ok, Describe(r.Engine));
        string generated = Generate(r);
        const string probeSource = """
            using System.Linq;
            namespace Probe;
            public static class Bindless
            {
                public static string Describe(bool heap)
                {
                    var v = T.Shaders.Bindless.Variants.Get(new T.Shaders.Bindless.Key(heap));
                    return string.Join(";", v.Bindings.ToArray().Select(b => b.Name + "=" + b.Count + (b.IsUnbounded ? "u" : "")));
                }
            }
            """;
        var asm = Harness.Compile([generated, probeSource], out var ctx);
        try
        {
            var probe = asm.GetType("Probe.Bindless")!;
            string Call(bool heap) => (string)probe.GetMethod("Describe")!.Invoke(null, [heap])!;
            Assert.Equal("Tex=0u;Smp=1;Out=1", Call(false));
            Assert.Equal("Tex=0u;Smp=1;Out=1;ResourceDescriptorHeap=0u", Call(true));
        }
        finally
        {
            ctx.Unload();
        }
    }

    [Fact]
    public void AnErrorInOneVariantIsReportedOnceOnItsLineNamingTheVariant()
    {
        string source = MaterialShader.Replace("    return c;", "#if ALPHA_TEST && LIGHTING_PBR\n    c = missing;\n#endif\n    return c;", StringComparison.Ordinal);
        int line = source.Split('\n').ToList().FindIndex(l => l.Contains("c = missing", StringComparison.Ordinal)) + 1;
        var r = Run(source, "ALPHA_TEST;LIGHTING=NONE|LAMBERT|PBR");
        Assert.False(r.Ok);
        var e = Assert.Single(r.Engine.Errors);
        Assert.Equal(Path.Combine(_dir, "Shaders", "Material.hlsl"), e.File);
        Assert.Equal(line, e.LineNumber);
        Assert.Equal("OSH0300", e.Code);
        Assert.EndsWith("(variant ALPHA_TEST=1, LIGHTING=PBR)", e.Message, StringComparison.Ordinal);

        // No manifest for a set with a broken member.
        Assert.False(File.Exists(r.Manifest));
    }

    [Fact]
    public void AnErrorSharedByVariantsIsReportedOnceListingThem()
    {
        string source = MaterialShader.Replace("    return c;", "#if LIGHTING_PBR\n    c = missing;\n#endif\n    return c;", StringComparison.Ordinal);
        var r = Run(source, "ALPHA_TEST;LIGHTING=NONE|LAMBERT|PBR");
        Assert.False(r.Ok);
        var e = Assert.Single(r.Engine.Errors);
        Assert.Matches(@"\(variants LIGHTING=PBR; ALPHA_TEST=1, LIGHTING=PBR\)$", e.Message);

        // The same error in every variant says so instead of listing all of them.
        var all = Run("[shader(\"vertex\")] float4 VS() : SV_Position { return missing; }\n", "A;B=1|2|3", name: "AllBroken", outDir: "obj/all");
        Assert.False(all.Ok);
        var m = Assert.Single(all.Engine.Errors);
        Assert.EndsWith("(in all 6 variants)", m.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TooManyVariantsIsADiagnosticThatNamesTheItemAndTheFix()
    {
        var r = Run(MaterialShader, "A;B;C;D;E;F;G", name: "Big");
        Assert.False(r.Ok);
        var e = Assert.Single(r.Engine.Errors);
        Assert.Equal("OSH0905", e.Code);
        Assert.Contains("'Shaders/Big.hlsl'", e.Message, StringComparison.Ordinal);
        Assert.Contains("128 variants", e.Message, StringComparison.Ordinal);
        Assert.Contains("limit of 64", e.Message, StringComparison.Ordinal);
        Assert.Contains("MaxVariants=\"128\"", e.Message, StringComparison.Ordinal);
        Assert.Contains("OneShaderMaxVariants", e.Message, StringComparison.Ordinal);

        // Raising the limit on the item lets it through.
        var raised = Run("[shader(\"compute\")][numthreads(1,1,1)] void CS() {}\n", "A;B;C;D;E;F;G", name: "Big", outDir: "obj/raised", extraMetadata: "MaxVariants=128");
        Assert.True(raised.Ok, Describe(raised.Engine));
        Assert.Equal(129, raised.Task.GeneratedFiles.Length);
    }

    [Fact]
    public void InvalidAxesAndUnknownVariantShadersAreDiagnosticsWithTheFix()
    {
        foreach (string bad in new[] { "1BAD", "A;A", "L=X||Y", "L=X|X", "L=a-b|c", "=A" })
        {
            var r = Run(MaterialShader, bad, name: "Bad", outDir: "obj/bad");
            Assert.False(r.Ok, bad);
            var e = Assert.Single(r.Engine.Errors);
            Assert.Equal("OSH0904", e.Code);
            Assert.Contains("NAME=A|B|C", e.Message, StringComparison.Ordinal);
        }

        // Axes and named variants together are ambiguous.
        var both = Run(MaterialShader, "A", name: "Both", outDir: "obj/both", named: [("One", "X=1")]);
        Assert.False(both.Ok);
        Assert.Equal("OSH0904", Assert.Single(both.Engine.Errors).Code);

        // A variant item that names no shader.
        var engine = new Engine();
        string path = Path.Combine(_dir, "Shaders", "Real.hlsl");
        File.WriteAllText(path, "[shader(\"compute\")][numthreads(1,1,1)] void CS() {}\n");
        var stray = new TaskItem("Typo");
        stray.SetMetadata("Shader", Path.Combine(_dir, "Shaders", "Reall.hlsl"));
        var task = new CompileShaders { BuildEngine = engine, Shaders = [new TaskItem(path)], Variants = [stray], OutputDirectory = "obj/stray", ProjectDirectory = _dir, DefaultNamespace = "T.Shaders" };
        Assert.False(task.Execute());
        Assert.Equal("OSH0906", Assert.Single(engine.Errors).Code);
    }

    [Fact]
    public void ANoOpRebuildCompilesNothingAndChangingAnAxisCompilesOnlyTheNewVariants()
    {
        var first = Run(MaterialShader, "ALPHA_TEST;LIGHTING=NONE|LAMBERT|PBR");
        Assert.True(first.Ok, Describe(first.Engine));
        Assert.Equal((6, 0), (first.Task.CompiledCount, first.Task.UpToDateCount));
        var stamps = first.Task.GeneratedFiles.ToDictionary(f => f.ItemSpec, f => File.GetLastWriteTimeUtc(f.ItemSpec));

        var again = Run(MaterialShader, "ALPHA_TEST;LIGHTING=NONE|LAMBERT|PBR");
        Assert.True(again.Ok);
        Assert.Equal((0, 6), (again.Task.CompiledCount, again.Task.UpToDateCount));
        foreach (var f in again.Task.GeneratedFiles)
        {
            Assert.Equal(stamps[f.ItemSpec], File.GetLastWriteTimeUtc(f.ItemSpec));
        }

        // One more value: the six define sets that already existed are reused, the two new ones compile.
        var grown = Run(MaterialShader, "ALPHA_TEST;LIGHTING=NONE|LAMBERT|PBR|TOON");
        Assert.True(grown.Ok, Describe(grown.Engine));
        Assert.Equal((2, 6), (grown.Task.CompiledCount, grown.Task.UpToDateCount));
        Assert.Equal(9, grown.Task.GeneratedFiles.Length);

        // Dropping it again removes the files of the variants that are gone.
        var shrunk = Run(MaterialShader, "ALPHA_TEST;LIGHTING=NONE|LAMBERT|PBR");
        Assert.Equal((0, 6), (shrunk.Task.CompiledCount, shrunk.Task.UpToDateCount));
        Assert.Equal(7, Directory.EnumerateFiles(Path.Combine(_dir, "obj"), "*.oneshader", SearchOption.AllDirectories).Count());

        // A changed define in the shared item list recompiles every variant.
        var edited = Run(MaterialShader, "ALPHA_TEST;LIGHTING=NONE|LAMBERT|PBR", extraMetadata: "Defines=QUALITY=2");
        Assert.Equal((6, 0), (edited.Task.CompiledCount, edited.Task.UpToDateCount));

        // A changed include recompiles too, in every variant that read it.
        File.WriteAllText(Path.Combine(_dir, "Shaders", "Common.hlsli"), "#define SCALE 1.0\n");
        string withInclude = MaterialShader.Replace("cbuffer Frame", "#include \"Common.hlsli\"\ncbuffer Frame", StringComparison.Ordinal);
        Assert.True(Run(withInclude, "ALPHA_TEST;LIGHTING=NONE|LAMBERT|PBR", outDir: "obj/inc").Ok);
        File.WriteAllText(Path.Combine(_dir, "Shaders", "Common.hlsli"), "#define SCALE 2.0\n");
        var includeChanged = Run(withInclude, "ALPHA_TEST;LIGHTING=NONE|LAMBERT|PBR", outDir: "obj/inc");
        Assert.Equal((6, 0), (includeChanged.Task.CompiledCount, includeChanged.Task.UpToDateCount));
    }

    [Fact]
    public void NamedVariantsGetAnEnumKeyAndTheirOwnDefines()
    {
        const string source = "cbuffer Frame : register(b0) { float4 Tint; };\n"
            + "[shader(\"pixel\")] float4 PSMain() : SV_Target {\n#if CUTOUT\n    clip(Tint.a - 0.5);\n#endif\n    return Tint; }\n";
        var r = Run(source, "", name: "Sky", named: [("Opaque", "QUALITY=1"), ("Cutout", "CUTOUT=1;QUALITY=2")]);
        Assert.True(r.Ok, Describe(r.Engine));
        Assert.Equal(3, r.Task.GeneratedFiles.Length);
        string generated = Generate(r);
        Assert.Contains("public enum VariantName", generated, StringComparison.Ordinal);
        const string probe = """
            namespace Probe;
            public static class Sky
            {
                public static byte[] SpirV(int which) => T.Shaders.Sky.Variants.Get(new T.Shaders.Sky.Key((T.Shaders.Sky.VariantName)which)).PSMain.SpirV.ToArray();
                public static string Defines(int which) => T.Shaders.Sky.Variants.Get(new T.Shaders.Sky.Key(T.Shaders.Sky.VariantName.Cutout)).Defines + which;
                public static string Name(int which) => T.Shaders.Sky.Variants.All[which].Name;
            }
            """;
        var asm = Harness.Compile([generated, probe], out var ctx);
        try
        {
            var type = asm.GetType("Probe.Sky")!;
            byte[] Spirv(int i) => (byte[])type.GetMethod("SpirV")!.Invoke(null, [i])!;
            foreach (var (index, defines) in new[] { (0, new[] { new KeyValuePair<string, string?>("QUALITY", "1") }), (1, new[] { new KeyValuePair<string, string?>("CUTOUT", "1"), new KeyValuePair<string, string?>("QUALITY", "2") }) })
            {
                var compiled = ShaderCompiler.Compile(source, "Shaders/Sky.hlsl", new CompileOptions { Defines = defines }, TestContext.Current.CancellationToken);
                Assert.Equal(compiled.Find("PSMain", CompileTarget.SpirV)!.ToArray(), Spirv(index));
            }

            Assert.Equal("CUTOUT=1 QUALITY=21", (string)type.GetMethod("Defines")!.Invoke(null, [1])!);
            Assert.Equal("Cutout", (string)type.GetMethod("Name")!.Invoke(null, [1])!);
        }
        finally
        {
            ctx.Unload();
        }
    }

    [Fact]
    public void SpecializationConstantsAreGeneratedPerVariantAndEqualReflection()
    {
        const string source = "[[vk::constant_id(3)]] const float Gain = 2.0;\n"
            + "#if WIDE\n[[vk::constant_id(5)]] const int Bias = 7;\n#endif\n"
            + "cbuffer P : register(b0) { float4 Tint; };\n"
            + "[shader(\"pixel\")] float4 PS() : SV_Target {\n#if WIDE\n return Tint * Gain + Bias;\n#else\n return Tint * Gain;\n#endif\n}\n";
        var r = Run(source, "WIDE", AllTargets, name: "Spec");
        Assert.True(r.Ok, Describe(r.Engine));
        string generated = Generate(r);
        const string probe = """
            namespace Probe;
            public static class Spec
            {
                public static uint[] Ids(bool wide) => T.Shaders.Spec.Variants.Get(new T.Shaders.Spec.Key(wide)).PS.SpecConstantIds.ToArray();
                public static uint GainId(bool wide) => wide ? T.Shaders.Spec.Variants.WideOn.SpecializationConstants.Gain.Id : T.Shaders.Spec.Variants.WideOff.SpecializationConstants.Gain.Id;
                public static float GainDefault(bool wide) => wide ? T.Shaders.Spec.Variants.WideOn.SpecializationConstants.Gain.Default : T.Shaders.Spec.Variants.WideOff.SpecializationConstants.Gain.Default;
                public static uint BiasId => T.Shaders.Spec.Variants.WideOn.SpecializationConstants.Bias.Id;
                public static int BiasDefault => T.Shaders.Spec.Variants.WideOn.SpecializationConstants.Bias.Default;
                public static uint SizeOn => T.Shaders.Spec.Variants.WideOn.Specialization.Size;
                public static uint SizeOff => T.Shaders.Spec.Variants.WideOff.Specialization.Size;
            }
            """;
        var asm = Harness.Compile([generated, probe], out var ctx);
        try
        {
            var type = asm.GetType("Probe.Spec")!;
            T Get<T>(string member, params object[] args) => (T)(args.Length == 0 && type.GetProperty(member) is { } p ? p.GetValue(null)! : type.GetMethod(member)!.Invoke(null, args)!);
            foreach (bool wide in new[] { false, true })
            {
                var defines = wide ? new[] { new KeyValuePair<string, string?>("WIDE", "1") } : Array.Empty<KeyValuePair<string, string?>>();
                var compiled = ShaderCompiler.Compile(source, "Shaders/Spec.hlsl", new CompileOptions { Defines = defines }, TestContext.Current.CancellationToken);
                var specs = compiled.Reflection!.SpecConstants;
                Assert.Equal(wide ? 2 : 1, specs.Count);
                Assert.Equal(specs.Select(s => s.Id).ToArray(), Get<uint[]>("Ids", wide));
                var gain = specs.Single(s => s.Name == "Gain");
                Assert.Equal(gain.Id, Get<uint>("GainId", wide));
                Assert.Equal(gain.DefaultValue, Get<float>("GainDefault", wide));
                Assert.Equal((uint)specs.Count * 4, Get<uint>(wide ? "SizeOn" : "SizeOff"));
                if (wide)
                {
                    var bias = specs.Single(s => s.Name == "Bias");
                    Assert.Equal(bias.Id, Get<uint>("BiasId"));
                    Assert.Equal(bias.DefaultValue, Get<int>("BiasDefault"));
                }
            }
        }
        finally
        {
            ctx.Unload();
        }
    }

    [Fact]
    public void ABufferThatDiffersBetweenVariantsGetsATypePerVariant()
    {
        const string source = "cbuffer Params : register(b0) {\n#if WIDE\n float4 A; float4 B;\n#else\n float4 A;\n#endif\n};\n"
            + "[shader(\"pixel\")] float4 PS() : SV_Target { return A; }\n";
        var r = Run(source, "WIDE", name: "Wide");
        Assert.True(r.Ok, Describe(r.Engine));
        string generated = Generate(r);
        Assert.Equal(2, Regex.Count(generated, @"public struct Params\b"));
        Assert.Contains("Size = 16u", generated, StringComparison.Ordinal);
        Assert.Contains("Size = 32u", generated, StringComparison.Ordinal);
        Harness.Compile([generated], out var ctx);
        ctx.Unload();
    }

    [Fact]
    public void GeneratedCodeIsTheSameInAnotherFolder()
    {
        var a = Run(MaterialShader, "ALPHA_TEST;LIGHTING=NONE|LAMBERT|PBR", AllTargets);
        var b = Run(MaterialShader, "ALPHA_TEST;LIGHTING=NONE|LAMBERT|PBR", AllTargets, outDir: "obj/again");
        Assert.True(a.Ok && b.Ok);
        Assert.Equal(Generate(a), Generate(b));
        Assert.Equal(File.ReadAllBytes(a.Manifest), File.ReadAllBytes(b.Manifest));
        Assert.DoesNotContain(_dir, File.ReadAllText(a.Manifest), StringComparison.Ordinal);
    }
}
