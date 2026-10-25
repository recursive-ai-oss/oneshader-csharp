using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OneShader.Diagnostics;
using Xunit;

namespace OneShader.Tests;

/// <summary>Entry points named after words a target reserves: the emitted name, the public helpers and the output's validity agree.</summary>
public class ReservedEntryNameTests
{
    // Legal HLSL identifiers that WGSL keywords/predeclared names, or C++/Metal names, make illegal in the output.
    private static readonly string[] Names =
    [
        "main", "loop", "fragment", "kernel", "select", "array", "let", "var", "fn", "target", "filter", "mod", "sample", "f32", "vec4",
        "device", "constant", "thread", "metal", "fast", "clamp", "min", "mix", "atomic_add", "texture2d_ms", "simd_sum", "float3x3", "uint2",
        "namespace_", "alias", "enum", "ptr", "workgroup", "uniform", "storage", "read", "write", "compute", "vertex", "final",
    ];

    private static string Source(string stage, string name) => stage switch
    {
        "compute" => "RWStructuredBuffer<uint> B : register(u0);\n[shader(\"compute\")][numthreads(4,1,1)] void " + name + "(uint3 id : SV_DispatchThreadID) { B[id.x] = id.x + 1; }\n",
        "vertex" => "[shader(\"vertex\")] float4 " + name + "(float2 p : POSITION) : SV_Position { return float4(p, 0, 1); }\n",
        _ => "[shader(\"pixel\")] float4 " + name + "(float4 p : SV_Position) : SV_Target { return p * 0.5; }\n",
    };

    public static TheoryData<string, string> Cases()
    {
        var d = new TheoryData<string, string>();
        foreach (string n in Names)
        {
            d.Add("compute", n);
        }

        foreach (string n in new[] { "main", "fragment", "loop", "vertex", "select", "kernel" })
        {
            d.Add("vertex", n);
            d.Add("pixel", n);
        }

        return d;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EmittedNamesEqualTheHelpers(string stage, string name)
    {
        var r = ShaderCompiler.Compile(Source(stage, name), "r.hlsl", new CompileOptions { Targets = [CompileTarget.Msl, CompileTarget.Wgsl] }, TestContext.Current.CancellationToken);
        if (r.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error && !d.Code.StartsWith("OSH5", StringComparison.Ordinal)))
        {
            Assert.Skip("not a legal HLSL name: " + r.Diagnostics[0]);
        }

        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        string msl = r.Find(name, CompileTarget.Msl)!.AsText();
        string wgsl = r.Find(name, CompileTarget.Wgsl)!.AsText();
        string mslKw = stage == "compute" ? "kernel void " : stage == "vertex" ? "vertex " : "fragment ";
        Assert.Contains(" " + Msl.MslBackend.EntryFunctionName(name) + "(", msl, StringComparison.Ordinal);
        Assert.Contains(mslKw, msl, StringComparison.Ordinal);
        Assert.Contains("fn " + Wgsl.WgslBackend.EntryFunctionName(name) + "(", wgsl, StringComparison.Ordinal);

        // A function the emitter calls the entry name cannot also exist elsewhere in the file.
        Assert.Equal(1, Count(msl, " " + Msl.MslBackend.EntryFunctionName(name) + "("));
        Assert.Equal(1, Count(wgsl, "fn " + Wgsl.WgslBackend.EntryFunctionName(name) + "("));

        string? naga = Oracles.Find("naga");
        if (naga is not null)
        {
            string work = Path.Combine(Path.GetTempPath(), "osh-reserved-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                string file = Path.Combine(work, "entry.wgsl");
                File.WriteAllText(file, wgsl);
                var (code, output) = Oracles.Run(naga, [file], work);
                Assert.True(code == 0, output + "\n" + wgsl);
            }
            finally
            {
                Directory.Delete(work, true);
            }
        }

        if (MslCorpus.DumpDirectory is { } dir)
        {
            dir = Path.Combine(dir, "reserved");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, stage + "_" + name + ".metal"), msl);
        }
    }

    [Fact]
    public void HelpersRenameOnlyWhatTheTargetReserves()
    {
        Assert.Equal("main0", Msl.MslBackend.EntryFunctionName("main"));
        Assert.Equal("fragment0", Msl.MslBackend.EntryFunctionName("fragment"));
        Assert.Equal("VSMain", Msl.MslBackend.EntryFunctionName("VSMain"));
        Assert.Equal("main", Wgsl.WgslBackend.EntryFunctionName("main"));
        Assert.Equal("loop_", Wgsl.WgslBackend.EntryFunctionName("loop"));
        Assert.Equal("fragment_", Wgsl.WgslBackend.EntryFunctionName("fragment"));
        Assert.Equal("VSMain", Wgsl.WgslBackend.EntryFunctionName("VSMain"));
    }

    [Fact]
    public void AHelperCalledLikeTheRenamedEntryDoesNotClash()
    {
        // 'main' is emitted as main0 in MSL; a helper the user named main0 must get another name, in both targets.
        const string src = "RWStructuredBuffer<uint> B : register(u0);\nuint main0(uint x) { return x * 3; }\n" +
            "[shader(\"compute\")][numthreads(1,1,1)] void main(uint3 id : SV_DispatchThreadID) { B[0] = main0(id.x); }\n" +
            "[shader(\"compute\")][numthreads(1,1,1)] void loop_(uint3 id : SV_DispatchThreadID) { B[0] = main0(id.x + 1); }\n";
        var r = ShaderCompiler.Compile(src, "c.hlsl", new CompileOptions { Targets = [CompileTarget.Msl, CompileTarget.Wgsl], Optimize = false }, TestContext.Current.CancellationToken);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        foreach (var b in r.Blobs)
        {
            string text = b.AsText();
            string entry = b.Target == CompileTarget.Msl ? Msl.MslBackend.EntryFunctionName(b.EntryPoint) : Wgsl.WgslBackend.EntryFunctionName(b.EntryPoint);
            string decl = b.Target == CompileTarget.Msl ? " " + entry + "(" : "fn " + entry + "(";
            Assert.Equal(1, Count(text, decl));
        }
    }

    private static int Count(string text, string needle)
    {
        int n = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            n++;
        }

        return n;
    }
}
