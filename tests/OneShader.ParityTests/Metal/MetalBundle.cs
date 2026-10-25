using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace OneShader.ParityTests;

/// <summary>
/// Writes everything the Metal runner (<c>tools/metal-runner</c>) needs to execute a case, plus the reference outputs
/// from the DXC build running on Vulkan, into <c>BUNDLE/CASE/</c>:
/// <c>case.txt</c> (the manifest), one <c>.metal</c> file per entry point, <c>in_*.bin</c> inputs, <c>ref/*.bin</c> reference outputs and
/// <c>status.txt</c>. The runner adds <c>metal/</c> and <c>run.log</c>; <see cref="MetalCompare"/> reads them back.
/// </summary>
public static class MetalBundle
{
    /// <summary>Case vertex buffer binding N is Metal buffer index 30 - N: 30 is where the MSL backend reads vertex attributes.</summary>
    public static int VertexBufferIndex(int binding) => 30 - binding;

    /// <summary>Names a readback file from its output key (<c>buffer:0.2</c> becomes <c>buffer_0.2.bin</c>).</summary>
    public static string FileFor(string key) => key.Replace(':', '_').Replace('[', '_').Replace("]", string.Empty, StringComparison.Ordinal) + ".bin";

    /// <summary>Exports <paramref name="model"/>; returns "ok" or a one-line reason when nothing could be exported.</summary>
    public static string Export(string bundleDir, CaseModel model, VkContext ctx)
    {
        string dir = Path.Combine(bundleDir, model.Name);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }
        Directory.CreateDirectory(Path.Combine(dir, "ref"));
        var data = CaseData.Build(model);

        // MSL for every entry point. A refusal by the backend is a result, not an error: the case is reported as not compiled.
        string source = Path.Combine(Tools.CorpusDir, model.Shader);
        var files = new Dictionary<string, string>();
        var groups = new Dictionary<string, (uint X, uint Y, uint Z)>();
        OneShader.Reflection.ShaderReflection? reflection = null;
        foreach (var (entry, stage) in ParityCompilers.Entries(model))
        {
            var options = new CompileOptions
            {
                Targets = [CompileTarget.SpirV, CompileTarget.Msl],
                Optimize = Environment.GetEnvironmentVariable("ONESHADER_OPTIMIZE") != "0",
                Defines = model.Defines.Select(d => new KeyValuePair<string, string?>(d.Key, d.Value)).ToList(),
                IncludeDirectories = [Path.GetDirectoryName(source)!],
                EntryPoints = [new EntryPointRequest(entry, stage switch
                {
                    ShaderStage.Vertex => OneShader.ShaderStage.Vertex,
                    ShaderStage.Pixel => OneShader.ShaderStage.Pixel,
                    _ => OneShader.ShaderStage.Compute,
                })],
            };
            var result = ShaderCompiler.Compile(File.ReadAllText(source), source, options);
            var blob = result.Find(entry, CompileTarget.Msl);
            if (!result.Success || blob is null || result.Reflection is null)
            {
                return "not-compiled: " + string.Join(" | ", result.Diagnostics.Select(d => d.ToString()));
            }
            File.WriteAllText(Path.Combine(dir, entry + ".metal"), blob.AsText());
            // Kept for diagnosing a mismatch: the SPIR-V of the same compile.
            if (result.Find(entry, CompileTarget.SpirV) is { } spirv)
            {
                File.WriteAllBytes(Path.Combine(dir, entry + ".spv"), spirv.ToArray());
            }
            files[entry] = entry + ".metal";
            reflection = result.Reflection;
            groups[entry] = reflection.EntryPoints.First(e => e.Name == entry).WorkgroupSize;
        }

        var sb = new StringBuilder();
        string Inv(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        // An array of N resources takes N consecutive Metal indices; the case names the element.
        int MslIndex(ResourceDecl r) => (int)reflection!.Resources
            .Where(x => x.Slots.Any(s => s.Target == CompileTarget.SpirV && s.Set == r.Set && s.Binding == r.Binding))
            .SelectMany(x => x.Slots)
            .First(s => s.Target == CompileTarget.Msl).Binding + r.Element;
        bool Bound(ResourceDecl r) => reflection!.Resources.Any(x => x.Slots.Any(s => s.Target == CompileTarget.SpirV && s.Set == r.Set && s.Binding == r.Binding));

        for (int i = 0; i < model.Resources.Count; i++)
        {
            var r = model.Resources[i];
            if (!Bound(r))
            {
                continue; // The file does not declare it (a case may bind more than one entry point needs).
            }
            string input = $"in_{i}.bin";
            switch (r.Kind)
            {
                case ResourceKind.Uniform:
                case ResourceKind.Storage:
                    File.WriteAllBytes(Path.Combine(dir, input), data.Buffers[i]);
                    sb.Line($"buffer {MslIndex(r)} {input} {(r.Output ? FileFor(r.OutputKey) : "-")}\n");
                    break;
                case ResourceKind.Texture:
                    {
                        File.WriteAllBytes(Path.Combine(dir, input), data.Images[i]);
                        string type = r.Cube ? "cube" : r.Layers > 0 ? "2darray" : "2d";
                        sb.Line($"texture {MslIndex(r)} {type} {r.Width} {r.Height} {r.Layers} {r.MipLevels} {FormatName(r.Format)} {input}\n");
                        break;
                    }
                case ResourceKind.StorageImage:
                    {
                        File.WriteAllBytes(Path.Combine(dir, input), data.Images[i]);
                        string type = r.Layers > 0 ? "2darray" : "2d";
                        sb.Line($"image {MslIndex(r)} {type} {r.Width} {r.Height} {r.Layers} {FormatName(r.Format)} {input} {(r.Output ? FileFor(r.OutputKey) : "-")}\n");
                        break;
                    }
                case ResourceKind.Sampler:
                    sb.Line($"sampler {MslIndex(r)} {(r.Linear ? "linear" : "nearest")} {(r.Repeat ? "repeat" : "clamp")} {(r.MipFilter ? 1 : 0)} {(r.Compare ? 1 : 0)}\n");
                    break;
            }
        }

        if (data.Push is { } push && reflection!.PushConstants.Count > 0)
        {
            // The Metal host delivers the block with setBytes at the index reflection records.
            File.WriteAllBytes(Path.Combine(dir, "push.bin"), push);
            sb.Line($"push {reflection.PushConstants[0].Slots.Single(s => s.Target == CompileTarget.Msl).Binding} push.bin\n");
        }

        foreach (var d in model.Dispatches)
        {
            var (tx, ty, tz) = groups[d.Entry];
            sb.Line($"compute {files[d.Entry]} {OneShader.Msl.MslBackend.EntryFunctionName(d.Entry)} {d.X} {d.Y} {d.Z} {tx} {ty} {tz}\n");
        }

        if (model.Graphics is { } g)
        {
            string ps = g.PixelEntry.Length > 0 ? $"{files[g.PixelEntry]} {OneShader.Msl.MslBackend.EntryFunctionName(g.PixelEntry)}" : "- -";
            string blend = g.Blend switch { BlendMode.Premultiplied => "premul", BlendMode.Alpha => "alpha", _ => "none" };
            string cull = g.Cull switch { CullMode.Back => "back", CullMode.Front => "front", _ => "none" };
            int count = g.Count;
            sb.Line($"graphics {files[g.VertexEntry]} {OneShader.Msl.MslBackend.EntryFunctionName(g.VertexEntry)} {ps} {g.Width} {g.Height} {FormatName(g.TargetFormat)} {blend} {cull} {g.Instances} {count} {(g.IndexData is not null ? 1 : 0)}\n");
            if (g.Depth is { } depth)
            {
                sb.Line($"depth {Inv(depth.Clear)} {(depth.Test ? 1 : 0)} {(depth.Output ? FileFor("depth") : "-")}\n");
            }
            foreach (var vb in g.VertexBuffers)
            {
                File.WriteAllBytes(Path.Combine(dir, $"vb{vb.Binding}.bin"), data.VertexBuffers[vb.Binding]);
                sb.Line($"vbuf {VertexBufferIndex(vb.Binding)} {vb.Stride} {(vb.PerInstance ? 1 : 0)} vb{vb.Binding}.bin\n");
            }
            foreach (var a in g.Attributes)
            {
                sb.Line($"attr {a.Location} {a.Format.ToString().ToLowerInvariant()} {a.Offset} {VertexBufferIndex(a.Binding)}\n");
            }
            if (data.Indices is not null)
            {
                File.WriteAllBytes(Path.Combine(dir, "index.bin"), data.Indices);
                sb.Line($"index {(g.IndexIs16Bit ? 16 : 32)} index.bin\n");
            }
            File.WriteAllBytes(Path.Combine(dir, "background.bin"), data.Background!);
            sb.Append("background background.bin\n");
            sb.Line($"targetout {FileFor("target")}\n");
        }
        File.WriteAllText(Path.Combine(dir, "case.txt"), sb.ToString());

        // The reference: DXC's SPIR-V on Vulkan, with the validation layer.
        var run = Parity.Run(ctx, model, data, ParityCompilers.Reference);
        var reference = new StringBuilder();
        foreach (var o in run.Outputs)
        {
            File.WriteAllBytes(Path.Combine(dir, "ref", FileFor(o.Key)), o.Bytes);
            reference.Line($"{o.Key} {FileFor(o.Key)} {o.Width} {o.Height} {FormatName(o.Format)} {(o.IsImage ? 1 : 0)}\n");
        }
        File.WriteAllText(Path.Combine(dir, "ref.txt"), reference.ToString());
        if (run.ValidationMessages.Count > 0)
        {
            return "reference-validation: " + run.ValidationMessages[0];
        }
        return "ok";
    }

    internal static string FormatName(PixelFormat f) => f switch
    {
        PixelFormat.R8 => "r8",
        PixelFormat.Rgba8 => "rgba8",
        PixelFormat.R32F => "r32f",
        PixelFormat.Rgba32F => "rgba32f",
        PixelFormat.R32UInt => "r32uint",
        PixelFormat.Rgba16F => "rgba16f",
        _ => "depth32f",
    };

    internal static PixelFormat ParseFormat(string s) => s switch
    {
        "r8" => PixelFormat.R8,
        "rgba8" => PixelFormat.Rgba8,
        "r32f" => PixelFormat.R32F,
        "rgba32f" => PixelFormat.Rgba32F,
        "r32uint" => PixelFormat.R32UInt,
        "rgba16f" => PixelFormat.Rgba16F,
        _ => PixelFormat.Depth32F,
    };
}

internal static class StringBuilderLines
{
    /// <summary>Appends an already formatted string (interpolated strings here are culture-neutral: only integers and invariant text).</summary>
    public static StringBuilder Line(this StringBuilder sb, string text) => sb.Append(text);
}
