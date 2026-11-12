using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using OneShader.Build.Tasks;
using Xunit;

namespace OneShader.Build.Tests;

/// <summary>The accessibility switch: <c>internal</c> changes only the keyword of each generated top-level class.</summary>
public sealed class AccessibilityBuildTests : IDisposable
{
    private const string Source = """
        cbuffer Params { float4 tint; };
        RWStructuredBuffer<float4> Output;

        [shader("compute")]
        [numthreads(1, 1, 1)]
        void CSMain(uint3 id : SV_DispatchThreadID)
        {
        #ifdef BOOST
            Output[0] = tint * 2;
        #else
            Output[0] = tint;
        #endif
        }
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "osh-access-" + Guid.NewGuid().ToString("N"));

    public AccessibilityBuildTests() => Directory.CreateDirectory(Path.Combine(_dir, "Shaders"));

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

    /// <summary>Compiles <c>Shaders/{name}.hlsl</c> and returns the generated C# (empty when the build failed).</summary>
    private int _builds;

    private (string Text, Engine Engine) Build(string name, string defaultAccess, string? itemAccess, string? axes = null)
    {
        // Each build gets its own project folder, so the same file name gives the same text apart from the switch.
        string root = Path.Combine(_dir, (++_builds).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(Path.Combine(root, "Shaders"));
        string path = Path.Combine(root, "Shaders", name + ".hlsl");
        File.WriteAllText(path, Source);
        var engine = new Engine();
        var item = new TaskItem(path);
        if (itemAccess is not null)
        {
            item.SetMetadata("Accessibility", itemAccess);
        }

        if (axes is not null)
        {
            item.SetMetadata("Axes", axes);
        }

        var task = new CompileShaders
        {
            BuildEngine = engine,
            Shaders = [item],
            OutputDirectory = "obj/oneshader",
            ProjectDirectory = root,
            DefaultNamespace = "T.Shaders",
            DefaultTargets = "SpirV",
            DefaultAccessibility = defaultAccess,
        };
        if (!task.Execute())
        {
            return ("", engine);
        }

        string outDir = Path.Combine(root, "obj/oneshader", "Shaders");
        var inputs = Directory.EnumerateFiles(outDir, name + ".hlsl.oneshader", SearchOption.TopDirectoryOnly)
            .Concat(Directory.Exists(Path.Combine(outDir, name + ".hlsl.variants")) ? Directory.EnumerateFiles(Path.Combine(outDir, name + ".hlsl.variants")) : [])
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (f, File.ReadAllText(f)))
            .ToArray();
        var (sources, diagnostics) = Harness.Generate(inputs);
        Assert.Empty(diagnostics);
        return (Assert.Single(sources).Text, engine);
    }

    [Fact]
    public void InternalChangesOnlyTheClassKeyword()
    {
        string pub = Build("A", "", null).Text;
        string explicitPub = Build("A", "public", null).Text;
        string intern = Build("A", "internal", null).Text;

        Assert.Equal(pub, explicitPub);
        Assert.Contains("\npublic static partial class A\n", pub, StringComparison.Ordinal);
        Assert.Contains("\ninternal static partial class A\n", intern, StringComparison.Ordinal);
        Assert.Equal(pub.Replace("\npublic static partial class A\n", "\ninternal static partial class A\n", StringComparison.Ordinal), intern);

        // The compiled assembly exposes nothing from it.
        var asm = Harness.Compile([Build("D", "internal", null).Text], out _);
        Assert.DoesNotContain(asm.GetExportedTypes(), t => t.Namespace == "T.Shaders");
        Assert.Contains(asm.GetTypes(), t => t.FullName == "T.Shaders.D" && !t.IsPublic);
        var pubAsm = Harness.Compile([Build("E", "", null).Text], out _);
        Assert.Contains(pubAsm.GetExportedTypes(), t => t.FullName == "T.Shaders.E");
    }

    [Fact]
    public void ItemMetadataOverridesTheProperty()
    {
        Assert.Contains("internal static partial class F", Build("F", "public", "internal").Text, StringComparison.Ordinal);
        Assert.Contains("public static partial class G", Build("G", "internal", "public").Text, StringComparison.Ordinal);
        Assert.Contains("internal static partial class H", Build("H", "", "Internal").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void VariantClassesFollowTheSwitchToo()
    {
        string pub = Build("V1", "", null, "BOOST").Text;
        string intern = Build("V1", "internal", null, "BOOST").Text;
        Assert.Contains("public static partial class V1", pub, StringComparison.Ordinal);
        Assert.Equal(pub.Replace("public static partial class V1", "internal static partial class V1", StringComparison.Ordinal), intern);
    }

    [Theory]
    [InlineData("private", null, "the OneShaderAccessibility property")]
    [InlineData("", "protected", "the Accessibility metadata")]
    public void AnyOtherValueIsAnErrorNamingWhereItWasSet(string property, string? metadata, string where)
    {
        var (text, engine) = Build("Bad", property, metadata);
        Assert.Equal("", text);
        var error = Assert.Single(engine.Errors);
        Assert.Equal("OSH0901", error.Code);
        Assert.Contains("Use public or internal", error.Message, StringComparison.Ordinal);
        Assert.Contains(where, error.Message, StringComparison.Ordinal);
    }
}
