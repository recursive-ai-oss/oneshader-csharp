using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneShader.Diagnostics;
using Xunit;

namespace OneShader.ParityTests;

/// <summary>
/// Runs the WGSL output of every corpus case in a real browser's WebGPU and compares it with DXC's SPIR-V on Vulkan.
/// The browser side is <c>tools/webgpu</c>; <c>tools/run-webgpu-parity.sh</c> sets it up and passes
/// ONESHADER_WEBGPU_DIR (working directory) and ONESHADER_WEBGPU_PYTHON (interpreter with Playwright). Without them
/// the tests skip. ONESHADER_WEBGPU_CASES (comma-separated case names) restricts the run.
/// </summary>
public class WebGpuParityTests
{
    private sealed record Outcome(RunResult? Reference, JsonNode? Result, string? Skip, string? Error);

    private static readonly Lazy<Dictionary<string, Outcome>> Outcomes = new(RunAll);

    private static string? WorkDir => Environment.GetEnvironmentVariable("ONESHADER_WEBGPU_DIR") is { Length: > 0 } d ? d : null;

    private static IEnumerable<string> SelectedCases()
    {
        string? only = Environment.GetEnvironmentVariable("ONESHADER_WEBGPU_CASES");
        return only is { Length: > 0 } ? only.Split(',', StringSplitOptions.RemoveEmptyEntries) : Corpus.CaseNames;
    }

    [Theory]
    [MemberData(nameof(Corpus.Cases), MemberType = typeof(Corpus))]
    public void WebGpuMatchesReference(string name)
    {
        Assert.SkipWhen(WorkDir is null, "set ONESHADER_WEBGPU_DIR (see tools/run-webgpu-parity.sh)");
        Assert.SkipUnless(SelectedCases().Contains(name), "not selected by ONESHADER_WEBGPU_CASES");
        var model = Corpus.Load(name);
        var outcome = Outcomes.Value[name];
        if (outcome.Skip is not null)
        {
            Row(name, "skipped", outcome.Skip);
            Assert.Skip("WGSL cannot express this case: " + outcome.Skip);
        }
        if (outcome.Error is not null)
        {
            Row(name, "error", outcome.Error);
            Assert.Fail(outcome.Error);
        }

        var result = outcome.Result!;
        var errors = result["errors"]!.AsArray().Select(e => e!.GetValue<string>()).ToList();
        if (errors.FirstOrDefault(e => e.StartsWith("no-subgroups:", StringComparison.Ordinal)) is { } noSubgroups)
        {
            Row(name, "skipped", noSubgroups);
            Assert.Skip(noSubgroups);
        }

        var web = new List<OutputBlob>();
        foreach (var r in outcome.Reference!.Outputs)
        {
            var o = result["outputs"]!.AsArray().FirstOrDefault(x => x!["key"]!.GetValue<string>() == r.Key);
            if (o is null)
            {
                continue;
            }
            byte[] bytes = Convert.FromBase64String(o["data"]!.GetValue<string>());
            var res = model.Resources.FirstOrDefault(x => x.OutputKey == r.Key);
            if (res is { Sorted: true })
            {
                bytes = VkRunner.SortWords(bytes);
            }
            web.Add(new OutputBlob(r.Key, bytes, r.Width, r.Height, r.Format, r.IsImage));
        }

        if (web.Count != outcome.Reference.Outputs.Count)
        {
            string why = errors.Count > 0 ? string.Join(" | ", errors) : "outputs missing";
            Row(name, errors.Count > 0 ? "webgpu-error" : "error", why);
            Assert.Fail($"{name}: {why}");
        }

        var report = Parity.CompareRuns(model, outcome.Reference, new RunResult(web, errors.Select(e => e).ToList()), "dxc", "webgpu", allowFloatBufferTolerance: true);
        string worst = Worst(outcome.Reference, web, model);
        string status = errors.Count > 0 ? "webgpu-error" : report.Failures.Count > 0 ? "mismatch" : "match";
        Row(name, status, worst + (report.Failures.Count > 0 ? " :: " + report.Failures[0] : "") + (errors.Count > 0 ? " :: " + errors[0] : ""));
        Assert.True(report.Failures.Count == 0, report.ToString());
    }

    // --- running everything once ----------------------------------------------------------------

    private static Dictionary<string, Outcome> RunAll()
    {
        string work = WorkDir!;
        string jobs = Path.Combine(work, "jobs");
        string results = Path.Combine(work, "results");
        foreach (string d in new[] { jobs, results })
        {
            if (Directory.Exists(d))
            {
                Directory.Delete(d, true);
            }
            Directory.CreateDirectory(d);
        }
        File.Delete(Path.Combine(work, "webgpu-table.tsv"));

        var ctx = Gpu.RequireOrSkip();
        ToolGate.RequireDxc();
        var outcomes = new Dictionary<string, Outcome>(StringComparer.Ordinal);
        var pending = new List<string>();
        foreach (string name in SelectedCases())
        {
            try
            {
                var model = Corpus.Load(name);
                var data = CaseData.Build(model);
                var modules = new List<(string Entry, ShaderStage Stage, string Wgsl)>();
                (uint Group, uint Binding)? pushSlot = null;
                string? skip = null;
                foreach (var (entry, stage) in ParityCompilers.Entries(model))
                {
                    var wgsl = CompileWgsl(model, entry, stage, out string? diag, out pushSlot);
                    if (wgsl is null)
                    {
                        skip = diag;
                        break;
                    }
                    modules.Add((entry, stage, wgsl));
                }
                if (skip is not null)
                {
                    outcomes[name] = new Outcome(null, null, skip, null);
                    continue;
                }
                var reference = Parity.Run(ctx, model, data, ParityCompilers.Reference, yUp: true);
                if (reference.ValidationMessages.Count > 0)
                {
                    outcomes[name] = new Outcome(null, null, null, "the Vulkan reference run printed validation messages: " + reference.ValidationMessages[0]);
                    continue;
                }
                File.WriteAllText(Path.Combine(jobs, name + ".json"), BuildJob(model, data, modules, pushSlot).ToJsonString());
                outcomes[name] = new Outcome(reference, null, null, null);
                pending.Add(name);
            }
            catch (Exception) when (KnownGaps.Entries.ContainsKey(name))
            {
                outcomes[name] = new Outcome(null, null, "known gap: " + KnownGaps.Entries[name], null);
            }
            catch (Exception ex)
            {
                outcomes[name] = new Outcome(null, null, null, ex.GetType().Name + ": " + ex.Message);
            }
        }

        if (pending.Count > 0)
        {
            string python = Environment.GetEnvironmentVariable("ONESHADER_WEBGPU_PYTHON") ?? "python3";
            string script = Path.Combine(Tools.CorpusDir, "..", "tools", "webgpu", "run.py");
            var (code, text) = Tools.Run(python, [script, jobs, results]);
            File.WriteAllText(Path.Combine(work, "browser.log"), text);
            Assert.True(code == 0, "the browser driver failed (exit " + code + "):\n" + text);
        }

        foreach (string name in pending)
        {
            string file = Path.Combine(results, name + ".json");
            outcomes[name] = File.Exists(file)
                ? outcomes[name] with { Result = JsonNode.Parse(File.ReadAllText(file)) }
                : outcomes[name] with { Error = "the browser produced no result" };
        }
        return outcomes;
    }

    /// <summary>The WGSL for one entry point, or null with the diagnostics when the backend reports the feature as unavailable.</summary>
    private static string? CompileWgsl(CaseModel model, string entry, ShaderStage stage, out string? diagnostics, out (uint Group, uint Binding)? pushSlot)
    {
        pushSlot = null;
        string source = Path.Combine(Tools.CorpusDir, model.Shader);
        var options = new CompileOptions
        {
            Targets = [CompileTarget.Wgsl],
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
        var blob = result.Find(entry, CompileTarget.Wgsl);
        if (result.Success && blob is not null && result.Diagnostics.Any(d => d.Code == "OSH5205"))
        {
            // 'precise' has no WGSL spelling; the compiler warns and the browser may fuse or reorder, so there is nothing to compare.
            diagnostics = string.Join("; ", result.Diagnostics.Where(d => d.Code == "OSH5205").Select(d => d.Code + ": " + d.Message));
            return null;
        }

        if (result.Success && blob is not null)
        {
            // WebGPU has no push constants: the compiler binds the block as a uniform buffer, and the browser job supplies the bytes there.
            if (result.Reflection!.PushConstants.Count > 0)
            {
                var slot = result.Reflection.PushConstants[0].Slots.Single(s => s.Target == CompileTarget.Wgsl);
                pushSlot = (slot.Set, slot.Binding);
            }

            diagnostics = null;
            return blob.AsText();
        }
        var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0 && errors.All(d => d.Code.StartsWith("OSH52", StringComparison.Ordinal)))
        {
            diagnostics = string.Join("; ", errors.Select(d => d.Code + ": " + d.Message));
            return null;
        }
        throw new InvalidOperationException("OneShader failed for " + entry + ": " + string.Join("; ", errors.Select(d => d.ToString())));
    }

    // --- job description ------------------------------------------------------------------------

    private static string FormatName(PixelFormat f) => f switch
    {
        PixelFormat.R8 => "r8unorm",
        PixelFormat.Rgba8 => "rgba8unorm",
        PixelFormat.R32F => "r32float",
        PixelFormat.Rgba32F => "rgba32float",
        PixelFormat.R32UInt => "r32uint",
        PixelFormat.Rgba16F => "rgba16float",
        _ => "depth32float",
    };

    private static JsonObject BuildJob(CaseModel model, CaseData data, List<(string Entry, ShaderStage Stage, string Wgsl)> modules, (uint Group, uint Binding)? pushSlot)
    {
        var job = new JsonObject
        {
            ["name"] = model.Name,
            ["kind"] = model.IsCompute ? "compute" : "graphics",
            // WGSL with `enable subgroups;` needs a device created with that optional feature; the others keep the default device.
            ["subgroups"] = modules.Any(m => m.Wgsl.StartsWith("enable subgroups;", StringComparison.Ordinal)),
            ["modules"] = new JsonArray(modules.Select(m => (JsonNode)new JsonObject
            {
                ["entry"] = m.Entry,
                ["stage"] = m.Stage.ToString().ToLowerInvariant(),
                ["wgsl"] = m.Wgsl,
            }).ToArray()),
        };

        var resources = new JsonArray();
        for (int i = 0; i < model.Resources.Count; i++)
        {
            var r = model.Resources[i];
            var o = new JsonObject
            {
                ["set"] = r.Set,
                ["binding"] = r.Binding,
                ["kind"] = r.Kind.ToString().ToLowerInvariant(),
                ["output"] = r.Output,
                ["key"] = r.OutputKey,
            };
            switch (r.Kind)
            {
                case ResourceKind.Uniform:
                case ResourceKind.Storage:
                    o["data"] = Convert.ToBase64String(data.Buffers[i]);
                    o["length"] = data.Buffers[i].Length;
                    break;
                case ResourceKind.Texture:
                case ResourceKind.StorageImage:
                    o["data"] = Convert.ToBase64String(data.Images[i]);
                    o["texture"] = new JsonObject
                    {
                        ["width"] = r.Width,
                        ["height"] = r.Height,
                        ["layers"] = r.Layers,
                        ["format"] = FormatName(r.Format),
                        ["mips"] = r.MipLevels,
                        ["cube"] = r.Cube,
                    };
                    break;
                case ResourceKind.Sampler:
                    o["sampler"] = new JsonObject
                    {
                        ["linear"] = r.Linear,
                        ["repeat"] = r.Repeat,
                        ["mipFilter"] = r.MipFilter,
                        ["compare"] = r.Compare,
                    };
                    break;
            }
            resources.Add(o);
        }
        if (pushSlot is { } push && data.Push is { } pushBytes)
        {
            // A uniform buffer is bound as whole 16-byte rows.
            byte[] padded = new byte[(pushBytes.Length + 15) & ~15];
            pushBytes.CopyTo(padded, 0);
            resources.Add(new JsonObject
            {
                ["set"] = push.Group,
                ["binding"] = push.Binding,
                ["kind"] = "uniform",
                ["output"] = false,
                ["key"] = "push",
                ["data"] = Convert.ToBase64String(padded),
                ["length"] = padded.Length,
            });
        }

        job["resources"] = resources;

        if (model.Dispatches.Count > 0)
        {
            job["dispatches"] = new JsonArray(model.Dispatches.Select(d => (JsonNode)new JsonObject
            {
                ["entry"] = d.Entry,
                ["x"] = d.X,
                ["y"] = d.Y,
                ["z"] = d.Z,
            }).ToArray());
        }

        if (model.Graphics is { } g)
        {
            var gj = new JsonObject
            {
                ["vertexEntry"] = g.VertexEntry,
                ["pixelEntry"] = g.PixelEntry,
                ["width"] = g.Width,
                ["height"] = g.Height,
                ["targetFormat"] = FormatName(g.TargetFormat),
                ["background"] = Convert.ToBase64String(data.Background!),
                ["blend"] = g.Blend.ToString().ToLowerInvariant(),
                ["cull"] = g.Cull.ToString().ToLowerInvariant(),
                ["count"] = g.Count,
                ["instances"] = g.Instances,
                ["vertexBuffers"] = new JsonArray(g.VertexBuffers.Select(v => (JsonNode)new JsonObject
                {
                    ["binding"] = v.Binding,
                    ["stride"] = v.Stride,
                    ["perInstance"] = v.PerInstance,
                    ["data"] = Convert.ToBase64String(data.VertexBuffers[v.Binding]),
                }).ToArray()),
                ["attributes"] = new JsonArray(g.Attributes.Select(a => (JsonNode)new JsonObject
                {
                    ["location"] = a.Location,
                    ["format"] = a.Format.ToString().ToLowerInvariant(),
                    ["offset"] = a.Offset,
                    ["binding"] = a.Binding,
                }).ToArray()),
            };
            if (g.Depth is { } depth)
            {
                gj["depth"] = new JsonObject { ["clear"] = depth.Clear, ["test"] = depth.Test, ["output"] = depth.Output };
            }
            if (g.IndexData is not null)
            {
                gj["index"] = new JsonObject { ["is16"] = g.IndexIs16Bit, ["data"] = Convert.ToBase64String(data.Indices!) };
            }
            job["graphics"] = gj;
        }
        return job;
    }

    // --- reporting ------------------------------------------------------------------------------

    /// <summary>Largest difference per output: differing bytes for buffers, largest 8-bit step or relative float error for images.</summary>
    private static string Worst(RunResult reference, List<OutputBlob> web, CaseModel model)
    {
        var parts = new List<string>();
        foreach (var a in reference.Outputs)
        {
            var b = web.FirstOrDefault(x => x.Key == a.Key);
            if (b is null || b.Bytes.Length != a.Bytes.Length)
            {
                parts.Add($"{a.Key} size differs");
                continue;
            }
            if (a.IsImage && Parity.IsFloatImage(a.Format))
            {
                float[] fa = Parity.DecodeFloats(a.Format, a.Bytes);
                float[] fb = Parity.DecodeFloats(a.Format, b.Bytes);
                double worst = 0;
                for (int i = 0; i < fa.Length; i++)
                {
                    if (!(float.IsNaN(fa[i]) && float.IsNaN(fb[i])))
                    {
                        double err = Math.Abs((double)fa[i] - fb[i]) / (1.0 + Math.Abs(fa[i]));
                        worst = double.IsNaN(err) ? double.PositiveInfinity : Math.Max(worst, err);
                    }
                }
                parts.Add($"{a.Key} max rel err {worst:G3}");
            }
            else if (a.IsImage)
            {
                int worst = 0;
                for (int i = 0; i < a.Bytes.Length; i++)
                {
                    worst = Math.Max(worst, Math.Abs(a.Bytes[i] - b.Bytes[i]));
                }
                parts.Add($"{a.Key} max step {worst}/255");
            }
            else if (model.Resources.FirstOrDefault(r => r.OutputKey == a.Key) is { FloatTolerance: > 0 })
            {
                double worst = 0;
                for (int i = 0; i + 4 <= a.Bytes.Length; i += 4)
                {
                    float fa = BitConverter.ToSingle(a.Bytes, i);
                    float fb = BitConverter.ToSingle(b.Bytes, i);
                    if (!(float.IsNaN(fa) && float.IsNaN(fb)))
                    {
                        double err = Math.Abs((double)fa - fb) / (1.0 + Math.Abs(fa));
                        worst = double.IsNaN(err) ? double.PositiveInfinity : Math.Max(worst, err);
                    }
                }
                parts.Add($"{a.Key} max rel err {worst:G3}");
            }
            else
            {
                int differing = 0;
                for (int i = 0; i < a.Bytes.Length; i++)
                {
                    differing += a.Bytes[i] != b.Bytes[i] ? 1 : 0;
                }
                parts.Add($"{a.Key} {differing} bytes differ");
            }
        }
        return string.Join(", ", parts);
    }

    private static readonly object Gate = new();

    private static void Row(string name, string status, string detail)
    {
        string line = string.Join('\t', name, status, detail.Replace('\t', ' ').Replace('\n', ' '));
        lock (Gate)
        {
            File.AppendAllText(Path.Combine(WorkDir!, "webgpu-table.tsv"), line + "\n");
        }
    }
}
