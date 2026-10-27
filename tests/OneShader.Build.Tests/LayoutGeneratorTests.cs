using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using OneShader.Build.Tasks;
using OneShader.Layouts;
using OneShader.Reflection;
using Xunit;

namespace OneShader.Build.Tests;

/// <summary>
/// The generated <c>Layout</c> and <c>Pipeline</c> members describe the same pipeline as the layout helpers do when they read the
/// compiler's reflection at run time.
/// </summary>
public class LayoutGeneratorTests
{
    private static readonly CompileTarget[] All = [CompileTarget.SpirV, CompileTarget.Msl, CompileTarget.Wgsl];
    private static readonly CompileTarget[] NoWgsl = [CompileTarget.SpirV, CompileTarget.Msl];

    public static TheoryData<string, string, CompileTargetSet> Shaders() => new()
    {
        { "2d/sprite.hlsl", "Sprite", CompileTargetSet.All },
        { "2d/ui_rect.hlsl", "UiRect", CompileTargetSet.All },
        { "3d/push_lit.hlsl", "PushLit", CompileTargetSet.All },
        { "3d/pbr_forward.hlsl", "PbrForward", CompileTargetSet.All },
        { "lang/resource_arrays.hlsl", "ResourceArrays", CompileTargetSet.NoWgsl },
        { "compute/histogram.hlsl", "Histogram", CompileTargetSet.All },
        { "lang/bindless_textures.hlsl", "BindlessTextures", CompileTargetSet.NoWgsl },
        { "lang/bindless_buffers.hlsl", "BindlessBuffers", CompileTargetSet.NoWgsl },
        { "lang/bindless_heap.hlsl", "BindlessHeap", CompileTargetSet.NoWgsl },
        { "compute/append_consume.hlsl", "AppendConsume", CompileTargetSet.SpirVOnly },
    };

    /// <summary>xunit cannot serialize arrays of enums in theory data; this names the three target sets used above.</summary>
    public enum CompileTargetSet
    {
        All,
        NoWgsl,
        SpirVOnly,
    }

    private static string Describe(EntryLayout e)
    {
        var parts = new List<string> { e.Name + " " + e.Stage + " " + e.WorkgroupSize };
        // The heap kinds are a list on one side and an array on the other: compare their contents, not their type names.
        parts.AddRange(e.Resources.Select(r => (r with { HeapKinds = [] }).ToString() + " kinds=" + string.Join(",", r.HeapKinds)));
        parts.Add(e.PushConstant?.ToString() ?? "no push");
        parts.Add(e.Vertex is { } v ? "vertex " + v.Stride + " " + string.Join(";", v.Attributes.ToArray().Select(a => a.ToString())) : "no vertex");
        return string.Join("\n", parts);
    }

    private static string Describe(PipelineLayout p)
    {
        var parts = new List<string>();
        foreach (var g in p.Groups)
        {
            foreach (var b in g.Entries)
            {
                parts.Add(g.Set + ":" + b.Resource + " metal=" + string.Join(",", Enum.GetValues<ShaderStage>().Select(s => b.MetalIndexFor(s))));
            }
        }

        foreach (var b in p.PushConstants)
        {
            parts.Add("push " + b.Name + " " + b.Size + " " + b.Stages + " " + b.WgslGroup + "/" + b.WgslBinding + " metal=" + string.Join(",", Enum.GetValues<ShaderStage>().Select(s => b.MetalIndexFor(s))));
        }

        return string.Join("\n", parts);
    }

    [Theory]
    [MemberData(nameof(Shaders))]
    public void GeneratedLayoutsEqualRuntimeReflection(string rel, string className, CompileTargetSet set)
    {
        string path = Path.Combine(Harness.RepoRoot, "corpus", rel);
        var targets = set switch { CompileTargetSet.All => All, CompileTargetSet.NoWgsl => NoWgsl, _ => [CompileTarget.SpirV] };
        var result = ShaderCompiler.Compile(
            File.ReadAllText(path),
            Path.GetFileName(path),
            new CompileOptions { Targets = targets, IncludeDirectories = [Path.GetDirectoryName(path)!] },
            TestContext.Current.CancellationToken);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var reflection = result.Reflection!;

        string text = CompileShaders.Render(reflection, result.Blobs, "/unused/" + className + ".hlsl", reflection.SourceName, "Probe.Shaders", className, "0", new SortedSet<string>(StringComparer.Ordinal), [], "/unused");
        var (sources, diagnostics) = Harness.Generate(("obj/oneshader/" + className + ".oneshader", text));
        Assert.Empty(diagnostics);
        var asm = Harness.Compile(sources.Select(s => s.Text), out var ctx);
        try
        {
            var shaderClass = asm.GetType("Probe.Shaders." + className)!;
            var generated = new List<EntryLayout>();
            foreach (var entry in reflection.EntryPoints)
            {
                var nested = shaderClass.GetNestedType(entry.Name)!;
                var layout = (EntryLayout)nested.GetProperty("Layout", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
                Assert.True(EntryLayout.TryCreate(reflection, entry.Name, out var fromReflection, out var error), error?.Message);
                Assert.Equal(Describe(fromReflection!), Describe(layout));
                Assert.Same(layout, nested.GetProperty("Layout", BindingFlags.Public | BindingFlags.Static)!.GetValue(null));
                generated.Add(layout);
            }

            // The file-level pipeline exists for one vertex + one pixel entry point or one compute entry point.
            var property = shaderClass.GetProperty("Pipeline", BindingFlags.Public | BindingFlags.Static);
            bool expectPipeline = reflection.EntryPoints.Count == 2 && reflection.EntryPoints.Any(e => e.Stage == ShaderStage.Vertex) && reflection.EntryPoints.Any(e => e.Stage == ShaderStage.Pixel)
                || reflection.EntryPoints.Count == 1 && reflection.EntryPoints[0].Stage == ShaderStage.Compute;
            Assert.Equal(expectPipeline, property is not null);
            if (property is not null)
            {
                var pipeline = (PipelineLayout)property.GetValue(null)!;
                Assert.True(PipelineLayout.TryCreate(generated.ToArray(), out var expected, out var error), error?.Message);
                Assert.Equal(Describe(expected!), Describe(pipeline));
                Assert.Equal(expected!.ToVulkan().SetLayouts.Count, pipeline.ToVulkan().SetLayouts.Count);
                Assert.Same(pipeline, property.GetValue(null));
            }
        }
        finally
        {
            ctx.Unload();
        }
    }

    [Fact]
    public void GeneratedBindingsMarkUnboundedArraysAndHeaps()
    {
        foreach (var (rel, className, resource, heap) in new[] { ("lang/bindless_textures.hlsl", "BindlessTextures", "Textures", ""), ("lang/bindless_heap.hlsl", "BindlessHeap", "ResourceDescriptorHeap", "Resource") })
        {
            string path = Path.Combine(Harness.RepoRoot, "corpus", rel);
            var result = ShaderCompiler.Compile(File.ReadAllText(path), Path.GetFileName(path), new CompileOptions { Targets = NoWgsl }, TestContext.Current.CancellationToken);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            string text = CompileShaders.Render(result.Reflection!, result.Blobs, "/unused/" + className + ".hlsl", result.Reflection!.SourceName, "Probe.Shaders", className, "0", new SortedSet<string>(StringComparer.Ordinal), [], "/unused");
            var (sources, diagnostics) = Harness.Generate(("obj/oneshader/" + className + ".oneshader", text));
            Assert.Empty(diagnostics);
            var asm = Harness.Compile(sources.Select(s => s.Text), out var ctx);
            try
            {
                var bindings = asm.GetType("Probe.Shaders." + className + "+Bindings+" + resource)!;
                Assert.Equal(0u, (uint)bindings.GetField("Count")!.GetValue(null)!);
                Assert.True((bool)bindings.GetField("IsUnbounded")!.GetValue(null)!);
                Assert.Equal(heap.Length > 0, bindings.GetField("Heap") is not null);
            }
            finally
            {
                ctx.Unload();
            }
        }
    }

    [Fact]
    public void ExistingGoldenFilesStillGenerateWithLayouts()
    {
        
        foreach (string name in GoldenModels.Names)
        {
            var (sources, diagnostics) = Harness.Generate(("obj/" + name + ".oneshader", GoldenModels.FileText(name)));
            Assert.Empty(diagnostics);
            Assert.Contains("Layout =>", Assert.Single(sources).Text, StringComparison.Ordinal);
        }
    }
}
