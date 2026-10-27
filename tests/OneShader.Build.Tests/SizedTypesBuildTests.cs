using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using OneShader.Build.Tasks;
using OneShader.Reflection;
using Xunit;

namespace OneShader.Build.Tests;

/// <summary>16-bit and 64-bit scalars through the build package: the property reaches the compiler, and the generated struct has DXC's byte layout.</summary>
public sealed class SizedTypesBuildTests : IDisposable
{
    private const string Source = """
        cbuffer Params
        {
            half a;
            half3 b;
            float c;
            double d;
            int64_t e;
            uint16_t2 f;
            uint16_t g;
        };

        RWStructuredBuffer<float> Output;

        [shader("compute")]
        [numthreads(1, 1, 1)]
        void CSMain(uint3 id : SV_DispatchThreadID)
        {
            Output[0] = (float)a + (float)b.x + c + (float)d + (float)e + (float)f.x + (float)g;
        }
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "osh-sized-" + Guid.NewGuid().ToString("N"));

    public SizedTypesBuildTests() => Directory.CreateDirectory(Path.Combine(_dir, "Shaders"));

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

    private (bool Ok, Engine Engine) Run(string path, string defaultSwitch, string? itemSwitch = null)
    {
        var engine = new Engine();
        var item = new TaskItem(path);
        if (itemSwitch is not null)
        {
            item.SetMetadata("Enable16BitTypes", itemSwitch);
        }

        var task = new CompileShaders
        {
            BuildEngine = engine,
            Shaders = [item],
            OutputDirectory = "obj/oneshader",
            ProjectDirectory = _dir,
            DefaultNamespace = "T.Shaders",
            DefaultTargets = "SpirV",
            DefaultEnable16BitTypes = defaultSwitch,
        };
        return (task.Execute(), engine);
    }

    [Fact]
    public void GeneratedStructHasTheOffsetsAndSizeDxcGives()
    {
        string path = Path.Combine(_dir, "Shaders", "Sized.hlsl");
        File.WriteAllText(path, Source);
        var (ok, engine) = Run(path, "true");
        Assert.True(ok, string.Join("; ", engine.Errors.Select(e => e.Message)));
        string file = Path.Combine(_dir, "obj/oneshader", "Shaders", "Sized.hlsl.oneshader");
        var (sources, diagnostics) = Harness.Generate((file, File.ReadAllText(file)));
        Assert.Empty(diagnostics);
        string generated = Assert.Single(sources).Text;
        var asm = Harness.Compile([generated], out _);
        var type = asm.GetType("T.Shaders.Sized")!.GetNestedType("Params")!;

        // The first component of each member, in declaration order.
        string[] names = ["a", "b_X", "c", "d", "e", "f_X", "g"];
        var offsets = names.Select(n => OffsetOf(type, n)).ToArray();
        Assert.Equal(typeof(Half), type.GetField("a")!.FieldType);
        Assert.Equal(typeof(Half), type.GetField("b_Z")!.FieldType);
        Assert.Equal(typeof(double), type.GetField("d")!.FieldType);
        Assert.Equal(typeof(long), type.GetField("e")!.FieldType);
        Assert.Equal(typeof(ushort), type.GetField("f_Y")!.FieldType);
        int size = (int)(uint)type.GetField("Size")!.GetRawConstantValue()!;
        Assert.True(size >= offsets[^1] + 2 && size % 8 == 0, "Size " + size);

        // Our SPIR-V and DXC's carry the same Offset decorations.
        string? dxc = Oracles.Find("dxc");
        string? dis = Oracles.Find("spirv-dis");
        if (dxc is null || dis is null)
        {
            return;
        }

        string hlsl = Path.Combine(_dir, "dxc.hlsl");
        File.WriteAllText(hlsl, Source);
        string spv = Path.Combine(_dir, "dxc.spv");
        var (code, text) = Oracles.Run(dxc, ["-spirv", "-fspv-target-env=vulkan1.2", "-HV", "2021", "-enable-16bit-types", "-T", "cs_6_2", "-E", "CSMain", hlsl, "-Fo", spv], _dir);
        Assert.True(code == 0, text);
        var (dcode, assembly) = Oracles.Run(dis, [spv], _dir);
        Assert.Equal(0, dcode);
        var dxcOffsets = MemberOffsets(assembly, "type_Params");
        Assert.Equal(dxcOffsets, offsets);
    }

    private static int[] MemberOffsets(string disassembly, string structName)
    {
        var found = new SortedDictionary<int, int>();
        foreach (Match m in Regex.Matches(disassembly, @"OpMemberDecorate %" + structName + @" (\d+) Offset (\d+)"))
        {
            found[int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)] = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        return found.Values.ToArray();
    }

    private static int OffsetOf(Type type, string field)
    {
        var f = type.GetField(field) ?? throw new InvalidOperationException("no field " + field);
        return ((FieldOffsetAttribute)f.GetCustomAttributes(typeof(FieldOffsetAttribute), false).Single()).Value;
    }

    [Fact]
    public void ItemMetadataOverridesTheProjectProperty()
    {
        string path = Path.Combine(_dir, "Shaders", "Sized.hlsl");
        File.WriteAllText(path, "RWStructuredBuffer<float> O; [shader(\"compute\")] [numthreads(1,1,1)] void CSMain() { int16_t x = 3; O[0] = (float)x; }\n");

        // Off by default: int16_t is an error that names the property.
        var (offOk, offEngine) = Run(path, "");
        Assert.False(offOk);
        Assert.Contains(offEngine.Errors, e => e.Code == "OSH0360" && e.Message!.Contains("OneShaderEnable16BitTypes", StringComparison.Ordinal));

        // The item turns it on, or off again over a project-wide true.
        Assert.True(Run(path, "", "true").Ok);
        Assert.False(Run(path, "true", "false").Ok);
        var (badOk, badEngine) = Run(path, "maybe");
        Assert.False(badOk);
        Assert.Contains(badEngine.Errors, e => e.Code == "OSH0901");
    }

    [Fact]
    public void GeneratedEntryClassCarriesTheRequiredFeatures()
    {
        string path = Path.Combine(_dir, "Shaders", "Sized.hlsl");
        File.WriteAllText(path, Source);
        var (ok, engine) = Run(path, "true");
        Assert.True(ok, string.Join("; ", engine.Errors.Select(e => e.Message)));
        string file = Path.Combine(_dir, "obj/oneshader", "Shaders", "Sized.hlsl.oneshader");
        var (sources, _) = Harness.Generate((file, File.ReadAllText(file)));
        var asm = Harness.Compile([Assert.Single(sources).Text], out _);
        var entry = asm.GetType("T.Shaders.Sized")!.GetNestedType("CSMain")!;
        var constant = (DeviceFeatures)entry.GetField("RequiredFeatures")!.GetRawConstantValue()!;
        Assert.Equal(DeviceFeatures.Float16 | DeviceFeatures.Int16 | DeviceFeatures.Float64 | DeviceFeatures.Int64 | DeviceFeatures.UniformBuffer16, constant);
        var layout = (OneShader.Layouts.EntryLayout)entry.GetProperty("Layout")!.GetValue(null)!;
        Assert.Equal(constant, layout.RequiredFeatures);
    }
}
