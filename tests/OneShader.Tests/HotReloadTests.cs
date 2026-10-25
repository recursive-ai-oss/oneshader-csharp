using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OneShader.Reflection;
using Xunit;

namespace OneShader.Tests;

/// <summary>The run-time pieces behind hot reload: files read, the file entry point, parallel compiles and interface comparison.</summary>
public class HotReloadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "osh-hot-" + Guid.NewGuid().ToString("N"));

    public HotReloadTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- files read ----

    [Fact]
    public void FilesListTheMainFileFirstThenIncludesInOrdinalOrder()
    {
        var files = new Dictionary<string, string>
        {
            ["z.hlsli"] = "#include \"a.hlsli\"\n",
            ["a.hlsli"] = "#pragma once\nstatic const float K = 1;\n",
            ["m.hlsli"] = "#include \"a.hlsli\"\n",
        };
        var r = ShaderCompiler.Compile(
            "#include \"z.hlsli\"\n#include \"m.hlsli\"\n[shader(\"compute\")][numthreads(1,1,1)] void main() {}\n",
            "main.hlsl",
            new CompileOptions { IncludeResolver = (name, _, _) => files.TryGetValue(name, out var t) ? new ResolvedInclude(name, t) : null },
            Ct);
        Assert.True(r.Success);
        Assert.Equal(["main.hlsl", "a.hlsli", "m.hlsli", "z.hlsli"], r.Files);
    }

    [Fact]
    public void FilesAreListedWhenTheCompileFails()
    {
        var r = ShaderCompiler.Compile(
            "#include \"inc.hlsli\"\nfloat4 f() { return missing; }\n",
            "bad.hlsl",
            new CompileOptions { IncludeResolver = (name, _, _) => name == "inc.hlsli" ? new ResolvedInclude("inc.hlsli", "// nothing\n") : null },
            Ct);
        Assert.False(r.Success);
        Assert.Equal(["bad.hlsl", "inc.hlsli"], r.Files);
    }

    [Fact]
    public void CompileFileReadsIncludesFromDiskAndReportsFullPaths()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "common"));
        File.WriteAllText(Path.Combine(_dir, "common", "k.hlsli"), "static const float K = 2;\n");
        string main = Path.Combine(_dir, "s.hlsl");
        File.WriteAllText(main, "#include \"k.hlsli\"\n[shader(\"compute\")][numthreads(4,1,1)] void CS() {}\n");
        var r = ShaderCompiler.CompileFile(main, new CompileOptions { IncludeDirectories = [Path.Combine(_dir, "common")] }, Ct);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        Assert.Equal([main, Path.Combine(_dir, "common", "k.hlsli")], r.Files);
    }

    [Fact]
    public void CompileFileOfAMissingFileIsADiagnosticNotAnException()
    {
        string path = Path.Combine(_dir, "nope.hlsl");
        var r = ShaderCompiler.CompileFile(path, null, Ct);
        Assert.False(r.Success);
        var d = Assert.Single(r.Diagnostics);
        Assert.Equal(ApiDiagnosticCodes.FileNotReadable, d.Code);
        Assert.Equal(path, d.Span.Path);
        Assert.Equal([path], r.Files);
        Assert.Null(r.Reflection);
    }

    [Fact]
    public void CompileFileOfADirectoryOrBadPathIsADiagnostic()
    {
        Assert.Equal(ApiDiagnosticCodes.FileNotReadable, Assert.Single(ShaderCompiler.CompileFile(_dir, null, Ct).Diagnostics).Code);
        Assert.Equal(ApiDiagnosticCodes.FileNotReadable, Assert.Single(ShaderCompiler.CompileFile("a\0b.hlsl", null, Ct).Diagnostics).Code);
        Assert.Throws<ArgumentNullException>(() => ShaderCompiler.CompileFile(null!, null, Ct));
    }

    // ---- parallel compiles ----

    [Fact]
    public void ParallelCompilesMatchSerialCompilesByteForByte()
    {
        var paths = new List<string>();
        foreach (string sub in new[] { "2d", "2d/renderer", "3d", "compute", "lang" })
        {
            paths.AddRange(CorpusFiles.Shaders(sub));
        }

        Assert.True(paths.Count > 40, "expected the corpus, found " + paths.Count + " files");
        CompileOptions Options(string p) => new() { IncludeDirectories = [Path.GetDirectoryName(p)!] };

        var serial = new string[paths.Count];
        for (int i = 0; i < paths.Count; i++)
        {
            serial[i] = Fingerprint(ShaderCompiler.CompileFile(paths[i], Options(paths[i]), Ct));
        }

        // Several rounds, each file compiled by many threads at once, so shared state (if any) gets a chance to show.
        for (int round = 0; round < 3; round++)
        {
            var parallel = new string[paths.Count * 4];
            Parallel.For(0, parallel.Length, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = Ct }, i =>
            {
                string p = paths[i % paths.Count];
                parallel[i] = Fingerprint(ShaderCompiler.CompileFile(p, Options(p), Ct));
            });
            for (int i = 0; i < parallel.Length; i++)
            {
                Assert.True(serial[i % paths.Count] == parallel[i], "parallel compile of " + paths[i % paths.Count] + " differs from the serial compile");
            }
        }

        Assert.Contains(serial, s => s.StartsWith("ok", StringComparison.Ordinal));
    }

    private static string Fingerprint(CompileResult r)
    {
        var sb = new System.Text.StringBuilder(r.Success ? "ok" : "failed");
        foreach (var d in r.Diagnostics)
        {
            sb.Append('\n').Append(d);
        }

        foreach (string f in r.Files)
        {
            sb.Append("\nfile ").Append(f);
        }

        foreach (var b in r.Blobs)
        {
            sb.Append("\nblob ").Append(b.EntryPoint).Append(' ').Append(b.Target).Append(' ').Append(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b.Data)));
        }

        if (r.Reflection is { } refl)
        {
            sb.Append('\n').Append(ReflectionFormat.Write(refl));
        }

        return sb.ToString();
    }

    // ---- interface compatibility ----

    private const string Graphics = """
        cbuffer Frame : register(b0)
        {
            float4x4 ViewProjection;
            float4 Tint;
            float Time;
        };
        Texture2D<float4> Atlas : register(t1);
        SamplerState Smp : register(s2);

        struct VSIn { float2 Position : POSITION; float2 Uv : TEXCOORD0; };
        struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };

        [shader("vertex")]
        VSOut VSMain(VSIn i)
        {
            VSOut o;
            o.Position = mul(ViewProjection, float4(i.Position, 0, 1));
            o.Uv = i.Uv;
            return o;
        }

        [shader("pixel")]
        float4 PSMain(VSOut i) : SV_Target { return Atlas.Sample(Smp, i.Uv) * Tint * Time; }
        """;

    private static ShaderReflection Reflect(string source)
    {
        var r = ShaderCompiler.Compile(source, "t.hlsl", null, Ct);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        return r.Reflection!;
    }

    private static string[] Diff(string built, string edited)
    {
        var report = InterfaceCompatibility.Compare(Reflect(built), Reflect(edited));
        Assert.Equal(report.Differences.Count == 0, report.IsCompatible);
        return [.. report.Differences.Select(d => d.Message)];
    }

    private static string Edit(string source, string find, string replace)
    {
        Assert.Contains(find, source, StringComparison.Ordinal);
        return source.Replace(find, replace, StringComparison.Ordinal);
    }

    [Fact]
    public void IdenticalAndBodyOnlyEditsAreCompatible()
    {
        Assert.Empty(Diff(Graphics, Graphics));
        string edited = Edit(Graphics, "* Tint * Time;", "* Tint * (Time + 1.0) * 0.5;");
        edited = Edit(edited, "o.Uv = i.Uv;", "o.Uv = i.Uv * 2.0;");
        edited += "\nfloat Unused(float x) { return x * x; }\n";
        Assert.Empty(Diff(Graphics, edited));
        var report = InterfaceCompatibility.Compare(Reflect(Graphics), Reflect(edited));
        Assert.True(report.IsCompatible);
        Assert.Equal("compatible", report.ToString());
    }

    [Fact]
    public void ReflectionRoundTripsThroughItsTextAndStaysCompatible()
    {
        // The generated code embeds the serialized form, so comparing against a parsed copy must work.
        var original = Reflect(Graphics);
        Assert.True(ReflectionFormat.TryRead(ReflectionFormat.Write(original), out var parsed, out string? error), error);
        Assert.True(InterfaceCompatibility.Compare(parsed!, original).IsCompatible);
    }

    [Fact]
    public void MovedConstantBufferMembersAreReportedWithOldAndNewOffsets()
    {
        string edited = Edit(Graphics, "float4 Tint;\n    float Time;", "float Time;\n    float4 Tint;");
        Assert.Equal(
            ["Frame.Tint moved from offset 64 to 80", "Frame.Time moved from offset 80 to 64"],
            Diff(Graphics, edited));
    }

    [Fact]
    public void ChangedMemberTypeIsReportedWithTheSizeKnockOn()
    {
        string edited = Edit(Edit(Graphics, "float4 Tint;", "float3 Tint;"), "* Tint *", "* float4(Tint, 1) *");
        Assert.Equal(
            ["Frame.Tint changed type from float4 to float3", "Frame.Time moved from offset 80 to 76"],
            Diff(Graphics, edited));
    }

    [Fact]
    public void AddedAndRemovedMembersAreReported()
    {
        Assert.Equal(["Frame.Time was removed"], Diff(Graphics, Edit(Edit(Graphics, "float Time;", ""), "* Time;", ";")));
        Assert.Equal(["Frame.Extra was added at offset 84"], Diff(Graphics, Edit(Graphics, "float Time;", "float Time;\n    float Extra;")));
    }

    [Fact]
    public void BufferSizeChangeIsReportedOnlyWhenNothingElseExplainsIt()
    {
        const string Built = "cbuffer C : register(b0) { float4 A; float B; };\n[shader(\"compute\")][numthreads(1,1,1)] void CS() { float x = A.x + B; }\n";
        string padded = Edit(Built, "float B; }", "float B; [[vk::offset(32)]] float Pad; }");
        // The new member explains the larger size; there is no separate size line.
        Assert.Equal(["C.Pad was added at offset 32"], Diff(Built, padded));

        // The same members at the same offsets in a bigger buffer: only the size says so.
        var built = Reflect(Built);
        var layout = built.Resources[0].Layout!;
        var grown = new ReflectedType { Kind = layout.Kind, Name = layout.Name, Size = layout.Size + 16, Alignment = layout.Alignment, Members = layout.Members };
        var bigger = new ShaderReflection { Resources = [new ResourceInfo { Name = "C", Kind = built.Resources[0].Kind, ResourceType = built.Resources[0].ResourceType, Stages = built.Resources[0].Stages, Slots = built.Resources[0].Slots, Layout = grown }], EntryPoints = built.EntryPoints };
        Assert.Equal(["C size changed from 32 to 48 bytes"], InterfaceCompatibility.Compare(built, bigger).Differences.Select(d => d.Message));
    }

    [Fact]
    public void ArrayAndMatrixChangesInsideBuffersAreReported()
    {
        const string Built = "cbuffer C : register(b0) { float4 Lights[4]; row_major float4x4 M; };\n[shader(\"compute\")][numthreads(1,1,1)] void CS() { float x = Lights[0].x + M[0][0]; }\n";
        Assert.Equal(["C.Lights changed array length from 4 to 8", "C.M moved from offset 64 to 128"], Diff(Built, Edit(Built, "Lights[4]", "Lights[8]")));
        string packing = Edit(Built, "row_major float4x4", "column_major float4x4");
        Assert.Equal(["C.M changed matrix packing from row_major to column_major"], Diff(Built, packing));
    }

    [Fact]
    public void NestedStructMembersAreNamedByPath()
    {
        const string Built = "struct Light { float3 Pos; float Range; };\ncbuffer C : register(b0) { Light Lights[2]; };\n[shader(\"compute\")][numthreads(1,1,1)] void CS() { float x = Lights[1].Range; }\n";
        string edited = Edit(Built, "float3 Pos; float Range;", "float4 Pos; float Range;");
        Assert.Equal(
            ["C.Lights changed array stride from 16 to 32", "C.Lights[].Pos changed type from float3 to float4", "C.Lights[].Range moved from offset 12 to 16"],
            Diff(Built, edited));
    }

    [Fact]
    public void ResourceChangesAreReported()
    {
        Assert.Equal(["Resource Atlas moved from set 0 binding 1 to set 0 binding 3 (SpirV)"], Diff(Graphics, Edit(Graphics, "register(t1)", "register(t3)")));
        Assert.Equal(["Atlas changed type from float4 to float2"], Diff(Graphics, Edit(Edit(Graphics, "Texture2D<float4>", "Texture2D<float2>"), "Atlas.Sample(Smp, i.Uv)", "float4(Atlas.Sample(Smp, i.Uv), 0, 1)")));
        Assert.Equal(["Resource Atlas was removed"], Diff(Graphics, Edit(Edit(Graphics, "Texture2D<float4> Atlas : register(t1);", ""), "Atlas.Sample(Smp, i.Uv) *", "")));
        Assert.Equal(["Resource Extra was added (Texture2D<float4>)"], Diff(Graphics, Edit(Graphics, "SamplerState Smp", "Texture2D<float4> Extra : register(t5);\nSamplerState Smp") + "\n"));
    }

    [Fact]
    public void ResourceArraySizeChangeIsReported()
    {
        const string Built = "Texture2D<float4> Tex[4] : register(t0);\nSamplerState S : register(s4);\n[shader(\"pixel\")] float4 PS() : SV_Target { return Tex[1].Sample(S, 0.5); }\n";
        Assert.Equal(["Resource Tex changed array size from 4 to 8"], Diff(Built, Edit(Built, "Tex[4]", "Tex[8]")));
    }

    [Fact]
    public void StageVisibilityIsReportedOnlyWhenItWidens()
    {
        const string PixelOnly = "cbuffer C : register(b0) { float4 K; };\n[shader(\"vertex\")] float4 VS(float4 p : POSITION) : SV_Position { return p; }\n[shader(\"pixel\")] float4 PS() : SV_Target { return K; }\n";
        string both = Edit(PixelOnly, "{ return p; }", "{ return p + K; }");
        Assert.Equal(
            ["Resource C is now also used by the Vertex stage, which the compiled-in bindings do not make it visible to (was Pixel)"],
            Diff(PixelOnly, both));
        // Dropping a use narrows visibility, which the built layout already covers.
        Assert.Empty(Diff(both, PixelOnly));
    }

    [Fact]
    public void EntryPointChangesAreReported()
    {
        Assert.Equal(["Entry point PSMain is missing"], Diff(Graphics, Edit(Graphics, "PSMain", "PSMain2")));
        // A new entry point is not a problem: the built code has no class for it.
        Assert.Empty(Diff(Graphics, Graphics + "\n[shader(\"pixel\")] float4 PSExtra(VSOut i) : SV_Target { return 1; }\n"));
    }

    [Fact]
    public void WorkgroupSizeChangeIsReported()
    {
        const string Built = "RWStructuredBuffer<float> B : register(u0);\n[shader(\"compute\")][numthreads(8, 8, 1)] void CS(uint3 id : SV_DispatchThreadID) { B[id.x] = 1; }\n";
        Assert.Equal(["CS workgroup size changed from 8x8x1 to 16x4x1"], Diff(Built, Edit(Built, "numthreads(8, 8, 1)", "numthreads(16, 4, 1)")));
    }

    [Fact]
    public void VertexInputChangesAreReported()
    {
        Assert.Equal(
            ["VSMain vertex input i.Uv changed format from Float32x2 to Float32x3", "VSMain vertex stride changed from 16 to 20"],
            Diff(Graphics, Edit(Edit(Graphics, "float2 Uv : TEXCOORD0; };\nstruct VSOut", "float3 Uv : TEXCOORD0; };\nstruct VSOut"), "o.Uv = i.Uv;", "o.Uv = i.Uv.xy;")));
        Assert.Equal(
            ["VSMain vertex input i.Position moved from location 0 to 1", "VSMain vertex input i.Position moved from offset 0 to 8", "VSMain vertex input i.Uv moved from location 1 to 0", "VSMain vertex input i.Uv moved from offset 8 to 0"],
            Diff(Graphics, Edit(Graphics, "float2 Position : POSITION; float2 Uv : TEXCOORD0;", "float2 Uv : TEXCOORD0; float2 Position : POSITION;")));
        Assert.Equal(
            ["VSMain vertex input i.Color was added at location 2", "VSMain vertex stride changed from 16 to 32"],
            Diff(Graphics, Edit(Graphics, "float2 Uv : TEXCOORD0; };\nstruct VSOut", "float2 Uv : TEXCOORD0; float4 Color : COLOR0; };\nstruct VSOut")));
        string removed = Edit(Edit(Graphics, "float2 Uv : TEXCOORD0; };\nstruct VSOut", "};\nstruct VSOut"), "o.Uv = i.Uv;", "o.Uv = 0;");
        Assert.Equal(["VSMain vertex input i.Uv was removed", "VSMain vertex stride changed from 16 to 8"], Diff(Graphics, removed));
    }

    [Fact]
    public void ReportTextListsEveryDifferenceAndSaysWhatToDo()
    {
        var report = InterfaceCompatibility.Compare(Reflect(Graphics), Reflect(Edit(Graphics, "register(t1)", "register(t3)")));
        Assert.False(report.IsCompatible);
        Assert.Equal(
            "incompatible with the compiled-in C# bindings:\n  Resource Atlas moved from set 0 binding 1 to set 0 binding 3 (SpirV)\nRebuild the project to update the generated C# bindings.",
            report.ToString());
        Assert.Equal(InterfaceDifferenceKind.Resource, report.Differences[0].Kind);
    }

    [Fact]
    public void NullArgumentsAreRejected()
    {
        var r = Reflect(Graphics);
        Assert.Throws<ArgumentNullException>(() => InterfaceCompatibility.Compare(null!, r));
        Assert.Throws<ArgumentNullException>(() => InterfaceCompatibility.Compare(r, null!));
    }
}
