using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OneShader.Tests;
using Xunit;

namespace OneShader.Tool.Tests;

public sealed partial class ToolTests : IDisposable
{
    private static readonly string Corpus = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "..", "corpus"));
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oneshader-tool-" + Guid.NewGuid().ToString("N"));

    public ToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string ThisFile([CallerFilePath] string p = "") => p;

    private static string C(string relative) => Path.Combine(Corpus, relative);

    /// <summary>Runs the command in-process; returns exit code, stdout bytes and stderr text.</summary>
    private static (int Code, byte[] Out, string Err) InProc(params string[] args)
    {
        using var stdout = new MemoryStream();
        using var stderr = new StringWriter { NewLine = "\n" };
        int code = Cli.Run(args, stdout, stderr);
        return (code, stdout.ToArray(), stderr.ToString());
    }

    /// <summary>Runs the real tool as a separate process.</summary>
    private static (int Code, string Out, string Err) Process(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "OneShader.Tool.dll"));
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = System.Diagnostics.Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o, err.Result);
    }

    private static void SpirvVal(string file)
    {
        string? val = Oracles.Find("spirv-val");
        if (val is null)
        {
            return;
        }

        var (code, output) = Oracles.Run(val, ["--target-env", "vulkan1.2", file], Path.GetDirectoryName(file)!);
        Assert.True(code == 0, "spirv-val: " + output);
    }

    [Fact]
    public void SpirvOutputEqualsTheApiAndValidates()
    {
        string src = C("2d/sprite.hlsl");
        string o = Path.Combine(_dir, "out.spv");
        var (code, _, err) = InProc("-T", "ps_6_0", "-E", "PSMain", "-spirv", src, "-Fo", o);
        Assert.Equal(0, code);
        Assert.Equal("", err);
        var api = ShaderCompiler.CompileFile(src, new CompileOptions { EntryPoints = [new EntryPointRequest("PSMain", ShaderStage.Pixel)] }, TestContext.Current.CancellationToken);
        Assert.Equal(api.Find("PSMain", CompileTarget.SpirV)!.ToArray(), File.ReadAllBytes(o));
        SpirvVal(o);
    }

    [Theory]
    [InlineData("-Zi", DebugInfoLevel.Lines)]
    [InlineData("-fspv-debug=line", DebugInfoLevel.Lines)]
    [InlineData("-fspv-debug=vulkan-with-source", DebugInfoLevel.Full)]
    [InlineData("-fspv-debug=vulkan", DebugInfoLevel.Full)]
    public void DebugFlagsGiveTheMatchingDebugInfoAndTheOutputValidates(string flag, DebugInfoLevel level)
    {
        string src = C("2d/sprite.hlsl");
        string o = Path.Combine(_dir, "dbg.spv");
        var (code, _, err) = InProc("-T", "ps_6_0", "-E", "PSMain", flag, "-Od", src, "-Fo", o);
        Assert.Equal(0, code);
        Assert.Equal("", err);
        var api = ShaderCompiler.CompileFile(src, new CompileOptions { EntryPoints = [new EntryPointRequest("PSMain", ShaderStage.Pixel)], DebugInfo = level, Optimize = false }, TestContext.Current.CancellationToken);
        Assert.Equal(api.Find("PSMain", CompileTarget.SpirV)!.ToArray(), File.ReadAllBytes(o));
        Assert.Contains(level == DebugInfoLevel.Full ? "NonSemantic.Shader.DebugInfo.100" : "sprite.hlsl", Encoding.Latin1.GetString(File.ReadAllBytes(o)), StringComparison.Ordinal);
        SpirvVal(o);
    }

    [Fact]
    public void DebugRootMakesPathsRelativeAndMslGetsLineDirectives()
    {
        string src = C("2d/sprite.hlsl");
        var (code, bytes, err) = InProc("-T", "ps_6_0", "-E", "PSMain", "--target", "msl", "-Zi", "--debug-root", Corpus, src);
        Assert.Equal(0, code);
        Assert.Equal("", err);
        string msl = Encoding.UTF8.GetString(bytes);
        Assert.Contains("#line ", msl, StringComparison.Ordinal);
        Assert.Contains("\"2d/sprite.hlsl\"", msl, StringComparison.Ordinal);
        Assert.DoesNotContain(Corpus, msl, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownDebugKindIsAUsageError()
    {
        var (code, _, err) = InProc("-fspv-debug=everything", C("2d/sprite.hlsl"));
        Assert.Equal(2, code);
        Assert.Contains("-fspv-debug=everything", err, StringComparison.Ordinal);
    }

    [Fact]
    public void MslAndWgslEqualTheApi()
    {
        string src = C("2d/sprite.hlsl");
        string o = Path.Combine(_dir, "sp.out");
        var (code, _, err) = InProc("--target", "msl", "--target", "wgsl", src, "-Fo", o);
        Assert.True(code == 0, err);
        var api = ShaderCompiler.CompileFile(src, new CompileOptions { Targets = [CompileTarget.Msl, CompileTarget.Wgsl] }, TestContext.Current.CancellationToken);
        Assert.Equal(4, api.Blobs.Count);
        foreach (var b in api.Blobs)
        {
            string ext = b.Target == CompileTarget.Msl ? ".metal" : ".wgsl";
            Assert.Equal(b.ToArray(), File.ReadAllBytes(Path.Combine(_dir, "sp." + b.EntryPoint + ext)));
        }
    }

    [Fact]
    public void SeveralEntriesAndOneTargetKeepTheGivenExtension()
    {
        string o = Path.Combine(_dir, "x.bin");
        var (code, _, err) = InProc("--all-entries", C("2d/sprite.hlsl"), "-Fo", o);
        Assert.True(code == 0, err);
        Assert.True(File.Exists(Path.Combine(_dir, "x.VSMain.bin")));
        Assert.True(File.Exists(Path.Combine(_dir, "x.PSMain.bin")));
    }

    [Fact]
    public void SingleOutputGoesToStdoutWithoutFo()
    {
        var (code, bytes, err) = InProc("-T", "vs_6_0", "-E", "VSMain", "--target", "wgsl", C("2d/sprite.hlsl"));
        Assert.True(code == 0, err);
        var api = ShaderCompiler.CompileFile(C("2d/sprite.hlsl"), new CompileOptions { Targets = [CompileTarget.Wgsl], EntryPoints = [new EntryPointRequest("VSMain", ShaderStage.Vertex)] }, TestContext.Current.CancellationToken);
        Assert.Equal(api.Find("VSMain", CompileTarget.Wgsl)!.ToArray(), bytes);
    }

    [Fact]
    public void ReflectionJsonIsStableAndParses()
    {
        string a = Path.Combine(_dir, "a.json");
        string b = Path.Combine(_dir, "b.json");
        Assert.Equal(0, InProc("--reflect", a, C("compute/histogram.hlsl")).Code);
        Assert.Equal(0, Process("--reflect", b, C("compute/histogram.hlsl")).Code);
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
        using var doc = JsonDocument.Parse(File.ReadAllText(a));
        Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("histogram.hlsl", doc.RootElement.GetProperty("source").GetString());
        var entry = doc.RootElement.GetProperty("entryPoints")[0];
        Assert.Equal("CSMain", entry.GetProperty("name").GetString());
        Assert.Equal(256, entry.GetProperty("workgroupSize")[0].GetInt32());
        Assert.DoesNotContain("\r", File.ReadAllText(a), StringComparison.Ordinal);
        Assert.DoesNotContain(Corpus, File.ReadAllText(a), StringComparison.Ordinal);
    }

    [Fact]
    public void CompileErrorPrintsTheDiagnosticLineAndExits1()
    {
        string f = Path.Combine(_dir, "bad.hlsl");
        File.WriteAllText(f, "[shader(\"pixel\")]\nfloat4 PSMain() : SV_Target0\n{\n    return missing;\n}\n");
        var api = ShaderCompiler.CompileFile(f, null, TestContext.Current.CancellationToken);
        string expected = api.Diagnostics[0].ToString();
        Assert.Matches(@"bad\.hlsl\(4,12\): error OSH\d{4}: ", expected);

        var (code, _, err) = InProc(f);
        Assert.Equal(1, code);
        Assert.Equal(expected + "\n", err);

        var (pcode, _, perr) = Process(f);
        Assert.Equal(1, pcode);
        Assert.Equal(expected + "\n", perr.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.DoesNotContain("   at ", perr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("-fvk-b-shift")]
    [InlineData("-Fd")]
    [InlineData("-HV")]
    public void UnknownOrUnsupportedFlagIsAUsageError(string flag)
    {
        var (code, _, err) = InProc(flag, C("2d/sprite.hlsl"));
        Assert.Equal(2, code);
        Assert.StartsWith("oneshader: error: ", err, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", err, StringComparison.Ordinal);
        if (flag != "-HV")
        {
            Assert.Contains("'" + flag + "'", err, StringComparison.Ordinal);
        }

        Assert.Equal(2, Process(flag, C("2d/sprite.hlsl")).Code);
    }

    [Theory]
    [InlineData("-E", "main")]
    [InlineData("-T", "gs_6_0")]
    [InlineData("--target", "dxil")]
    [InlineData("-HV", "2016")]
    [InlineData("-fspv-target-env=vulkan1.0")]
    public void BadValuesAreUsageErrors(params string[] args)
    {
        var (code, _, err) = InProc([.. args, C("2d/sprite.hlsl")]);
        Assert.Equal(2, code);
        Assert.Contains("oneshader: error: ", err, StringComparison.Ordinal);
    }

    [Fact]
    public void HeapBindingFlagsMoveTheHeapsAndReflectionMarksThemUnbounded()
    {
        string json = Path.Combine(_dir, "heap.json");
        string spv = Path.Combine(_dir, "heap.spv");
        var (code, _, err) = InProc("-T", "cs_6_6", "-E", "CSMain", "-fvk-bind-resource-heap", "5", "3", "-fvk-bind-sampler-heap", "6", "3", "--reflect", json, "-Fo", spv, C("lang/bindless_heap.hlsl"));
        Assert.True(code == 0, err);
        SpirvVal(spv);
        using var doc = JsonDocument.Parse(File.ReadAllText(json));
        var heaps = new List<(string Heap, int Set, int Binding)>();
        foreach (var r in doc.RootElement.GetProperty("resources").EnumerateArray())
        {
            if (r.TryGetProperty("heap", out var heap))
            {
                Assert.True(r.GetProperty("unbounded").GetBoolean());
                Assert.Equal(0, r.GetProperty("count").GetInt32());
                var slot = r.GetProperty("slots")[0];
                heaps.Add((heap.GetString()!, slot.GetProperty("set").GetInt32(), slot.GetProperty("binding").GetInt32()));
            }
        }

        Assert.Equal([("Resource", 3, 5), ("Sampler", 3, 6)], heaps);
        var entry = doc.RootElement.GetProperty("entryPoints")[0];
        Assert.Equal("Resource, Sampler", entry.GetProperty("descriptorHeaps").GetString());
        Assert.Contains("MutableDescriptorType", entry.GetProperty("requiredFeatures").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-fvk-bind-resource-heap", "x", "1")]
    [InlineData("-fvk-bind-sampler-heap", "1")]
    public void HeapBindingFlagsNeedTwoNumbers(params string[] args)
    {
        var (code, _, err) = InProc([.. args, C("lang/bindless_heap.hlsl")]);
        Assert.Equal(2, code);
        Assert.Contains("oneshader: error: ", err, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingInputIsAUsageError()
    {
        Assert.Equal(2, InProc("-spirv").Code);
        Assert.Equal(1, InProc(Path.Combine(_dir, "nope.hlsl")).Code);
    }

    [Fact]
    public void DepFileListsIncludes()
    {
        File.WriteAllText(Path.Combine(_dir, "inc dir.hlsli"), "float K() { return 1.0; }\n");
        File.WriteAllText(Path.Combine(_dir, "main.hlsl"), "#include \"inc dir.hlsli\"\n[shader(\"pixel\")]\nfloat4 PSMain() : SV_Target0 { return K(); }\n");
        string dep = Path.Combine(_dir, "m.d");
        string o = Path.Combine(_dir, "m.spv");
        var (code, _, err) = InProc("--depfile", dep, "-Fo", o, Path.Combine(_dir, "main.hlsl"));
        Assert.True(code == 0, err);
        string text = File.ReadAllText(dep);
        // Dependency files use forward slashes on every platform, which Make and Ninja accept on Windows too.
        static string Make(string p) => p.Replace('\\', '/');
        Assert.StartsWith(Make(o) + ":", text, StringComparison.Ordinal);
        Assert.Contains(Make(Path.Combine(_dir, "main.hlsl")), text, StringComparison.Ordinal);
        Assert.Contains("inc\\ dir.hlsli", text, StringComparison.Ordinal); // the space is escaped for Make
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DefinesIncludesAndOptionsReachTheCompiler()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "inc"));
        File.WriteAllText(Path.Combine(_dir, "inc", "k.hlsli"), "#ifndef V\n#error V missing\n#endif\nfloat K() { return V; }\n");
        File.WriteAllText(Path.Combine(_dir, "m.hlsl"), "#include <k.hlsli>\nfloat4 main() : SV_Target0 { return K(); }\n");
        var (code, _, err) = InProc("-Tps_6_0", "-D", "V=2.0", "-I", Path.Combine(_dir, "inc"), "-O0", "-Zpr", "-HV", "2021", "-Fo" + Path.Combine(_dir, "m.spv"), Path.Combine(_dir, "m.hlsl"));
        Assert.True(code == 0, err);
        Assert.NotEqual(0, InProc("-T", "ps_6_0", "-I", Path.Combine(_dir, "inc"), Path.Combine(_dir, "m.hlsl"), "-Fo", Path.Combine(_dir, "n.spv")).Code);
    }

    [Fact]
    public void WarningsAreErrorsWithWX()
    {
        string f = Path.Combine(_dir, "w.hlsl");
        File.WriteAllText(f, "float4 main(float4 v : POSITION) : SV_Target0 { float2 a = v; return float4(a, 0, 1); }\n");
        var plain = InProc("-T", "ps_6_0", f, "-Fo", Path.Combine(_dir, "w.spv"));
        Assert.Contains("warning", plain.Err, StringComparison.Ordinal);
        Assert.Equal(0, plain.Code);
        var wx = InProc("-T", "ps_6_0", "-WX", f, "-Fo", Path.Combine(_dir, "w2.spv"));
        Assert.Equal(1, wx.Code);
        Assert.False(File.Exists(Path.Combine(_dir, "w2.spv")));
    }

    [Fact]
    public void VersionAndHelpPrint()
    {
        var v = InProc("--version");
        Assert.Equal(0, v.Code);
        Assert.StartsWith("oneshader ", Encoding.UTF8.GetString(v.Out), StringComparison.Ordinal);
        var h = InProc("--help");
        Assert.Equal(0, h.Code);
        Assert.Contains("usage: oneshader", Encoding.UTF8.GetString(h.Out), StringComparison.Ordinal);
    }

    public static IEnumerable<TheoryDataRow<string>> DxcLines() =>
    [
        new("-spirv -T vs_6_0 -E VSMain -fspv-target-env=vulkan1.2 2d/sprite.hlsl"),
        new("-spirv -T ps_6_0 -E PSMain 2d/sprite.hlsl"),
        new("-spirv -Tps_6_0 -EPSMain 2d/sprite.hlsl"),
        new("-spirv -T ps_6_0 -E PSMain -D UI_AA_WIDTH=2.0 -O3 -HV 2021 2d/ui_rect.hlsl"),
        new("-spirv -T cs_6_0 -E CSMain -O0 compute/histogram.hlsl"),
        new("-spirv -T cs_6_2 -E CSMain -Zpr compute/histogram.hlsl"),
        new("-spirv -T vs_6_0 -E VSMain -I 3d 3d/ibl.hlsl"),
        new("-spirv -T ps_6_6 -E PSMain -Zpc -Od -I 3d 3d/ibl.hlsl"),
    ];

    [Theory]
    [MemberData(nameof(DxcLines))]
    public void DxcCommandLineWorksWithBothCompilers(string line)
    {
        string? dxc = Oracles.Find("dxc");
        Assert.SkipUnless(dxc is not null, "dxc is not installed");

        var tokens = new List<string>(line.Split(' '));
        string file = tokens[^1];
        tokens.RemoveAt(tokens.Count - 1);
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] == "-I" && i + 1 < tokens.Count)
            {
                tokens[i + 1] = C(tokens[i + 1]);
            }
        }

        string theirs = Path.Combine(_dir, "dxc.spv");
        string ours = Path.Combine(_dir, "ours.spv");
        var (dcode, dout) = Oracles.Run(dxc!, [.. tokens, C(file), "-Fo", theirs], _dir);
        Assert.True(dcode == 0, "dxc: " + dout);
        var (code, _, err) = InProc([.. tokens, C(file), "-Fo", ours]);
        Assert.True(code == 0, err);
        SpirvVal(theirs);
        SpirvVal(ours);
    }
}
