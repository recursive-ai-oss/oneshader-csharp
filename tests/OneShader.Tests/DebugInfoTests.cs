using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OneShader.Types;
using Xunit;

namespace OneShader.Tests;

/// <summary>A small SPIR-V reader for the debug records: just enough to check what a debugger would see.</summary>
internal sealed class SpvDebugReader
{
    public const uint OpSource = 3, OpSourceContinued = 2, OpString = 7, OpLine = 8, OpNoLine = 317, OpExtInstImport = 11, OpExtInst = 12,
        OpExtension = 10, OpConstant = 43, OpTypeInt = 21, OpFunction = 54, OpFunctionEnd = 56, OpLabel = 248;

    public sealed record Record(uint Id, uint Inst, uint[] Operands, int Index);

    public sealed record LineRecord(string File, int Line, int ColumnStart, int ColumnEnd, int Index);

    public Dictionary<uint, string> Strings { get; } = [];

    public Dictionary<uint, uint> Constants { get; } = [];

    public Dictionary<uint, string> Imports { get; } = [];

    public List<string> Extensions { get; } = [];

    /// <summary>OpSource with a file: file string → text (OpSourceContinued appended).</summary>
    public Dictionary<string, string> OpSources { get; } = [];

    public List<Record> Records { get; } = [];

    public List<LineRecord> OpLines { get; } = [];

    public int OpNoLines { get; private set; }

    public static SpvDebugReader Parse(byte[] blob)
    {
        var r = new SpvDebugReader();
        var words = new uint[blob.Length / 4];
        Buffer.BlockCopy(blob, 0, words, 0, blob.Length);
        var intWidths = new Dictionary<uint, uint>();
        string? lastSource = null;
        int index = 0;
        for (int at = 5; at < words.Length;)
        {
            uint w = words[at];
            int count = (int)(w >> 16);
            uint op = w & 0xFFFF;
            var ins = words.AsSpan(at, count);
            switch (op)
            {
                case OpString:
                    r.Strings[ins[1]] = Literal(ins[2..]);
                    break;
                case OpExtension:
                    r.Extensions.Add(Literal(ins[1..]));
                    break;
                case OpExtInstImport:
                    r.Imports[ins[1]] = Literal(ins[2..]);
                    break;
                case OpTypeInt:
                    intWidths[ins[1]] = ins[2];
                    break;
                case OpConstant:
                    if (intWidths.TryGetValue(ins[1], out uint width) && width == 32)
                    {
                        r.Constants[ins[2]] = ins[3];
                    }

                    break;
                case OpSource when count >= 5:
                    lastSource = r.Strings[ins[3]];
                    r.OpSources[lastSource] = Literal(ins[4..]);
                    break;
                case OpSourceContinued when lastSource is not null:
                    r.OpSources[lastSource] += Literal(ins[1..]);
                    break;
                case OpLine:
                    r.OpLines.Add(new LineRecord(r.Strings[ins[1]], (int)ins[2], (int)ins[3], (int)ins[3], index));
                    break;
                case OpNoLine:
                    r.OpNoLines++;
                    break;
                case OpExtInst:
                    r.Records.Add(new Record(ins[2], ins[4], ins[5..].ToArray(), index));
                    break;
                default:
                    break;
            }

            index++;
            at += count;
        }

        return r;
    }

    private static string Literal(ReadOnlySpan<uint> words)
    {
        var bytes = new List<byte>();
        foreach (uint w in words)
        {
            for (int k = 0; k < 4; k++)
            {
                byte b = (byte)(w >> (8 * k));
                if (b == 0)
                {
                    return Encoding.UTF8.GetString(bytes.ToArray());
                }

                bytes.Add(b);
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    public IEnumerable<Record> Of(uint inst) => Records.Where(x => x.Inst == inst);

    public uint Const(uint id) => Constants[id];

    /// <summary>DebugSource records: file name → text.</summary>
    public Dictionary<string, string> DebugSources()
    {
        var map = new Dictionary<string, string>();
        string? last = null;
        foreach (var rec in Records)
        {
            if (rec.Inst == 35)
            {
                last = Strings[rec.Operands[0]];
                map[last] = rec.Operands.Length > 1 ? Strings[rec.Operands[1]] : "";
            }
            else if (rec.Inst == 102 && last is not null)
            {
                map[last] += Strings[rec.Operands[0]];
            }
        }

        return map;
    }

    /// <summary>DebugLine records resolved to file names and numbers.</summary>
    public List<LineRecord> DebugLines()
    {
        var files = new Dictionary<uint, string>();
        foreach (var rec in Records)
        {
            if (rec.Inst == 35)
            {
                files[rec.Id] = Strings[rec.Operands[0]];
            }
        }

        return Of(103).Select(l => new LineRecord(files[l.Operands[0]], (int)Const(l.Operands[1]), (int)Const(l.Operands[3]), (int)Const(l.Operands[4]), l.Index)).ToList();
    }

    public List<string> InstructionNames() => Records.Select(x => x.Inst.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
}

public class DebugInfoTests
{
    private const string Main = """
        #include "inc.hlsli"
        #define SCALE(x) ((x) * 2.0)
        RWStructuredBuffer<float> Out : register(u0);
        float Triple(float a)
        {
            float t = a * 3.0;
            return t;
        }
        [shader("compute")]
        [numthreads(1, 1, 1)]
        void CSMain(uint3 id : SV_DispatchThreadID)
        {
            float v = Triple(id.x);
            Out[0] = Half(v);
            Out[1] = SCALE(v);
        }
        """;

    private const string Include = """
        float Half(float x)
        {
            return x * 0.5;
        }
        """;

    private static CompileResult Build(CompileOptions options, string source = Main, string path = "main.hlsl")
    {
        var withResolver = new CompileOptions
        {
            Targets = options.Targets,
            Optimize = options.Optimize,
            DebugInfo = options.DebugInfo,
            DebugSourceRoot = options.DebugSourceRoot,
            IncludeResolver = (name, _, _) => name == "inc.hlsli" ? new ResolvedInclude("inc.hlsli", Include) : null,
        };
        var r = ShaderCompiler.Compile(source, path, withResolver, TestContext.Current.CancellationToken);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        return r;
    }

    private static byte[] Spirv(CompileResult r) => r.Blobs.Single(b => b.Target == CompileTarget.SpirV).ToArray();

    [Fact]
    public void DefaultOptionsLeaveNoDebugRecords()
    {
        var spv = SpvDebugReader.Parse(Spirv(Build(new CompileOptions())));
        Assert.Empty(spv.Records);
        Assert.Empty(spv.OpLines);
        Assert.Empty(spv.Imports);
        Assert.Empty(spv.Extensions);
        string msl = Build(new CompileOptions { Targets = [CompileTarget.Msl] }).Blobs.Single().AsText();
        Assert.DoesNotContain("#line", msl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullDebugInfoNamesTheExtendedSetAndEmbedsEverySource(bool optimize)
    {
        var spv = SpvDebugReader.Parse(Spirv(Build(new CompileOptions { DebugInfo = DebugInfoLevel.Full, Optimize = optimize })));
        Assert.Contains("NonSemantic.Shader.DebugInfo.100", spv.Imports.Values);
        Assert.Contains("SPV_KHR_non_semantic_info", spv.Extensions);
        var sources = spv.DebugSources();
        Assert.Equal(2, sources.Count);
        Assert.Equal(Main, sources["main.hlsl"]);
        Assert.Equal(Include, sources["inc.hlsli"]);
        Assert.Single(spv.Of(1)); // compilation unit
        Assert.Single(spv.Of(107)); // entry point
        Assert.NotEmpty(spv.Of(101)); // function definition
    }

    [Fact]
    public void LinesMapStatementsToTheirFilesIncludingIncludesAndMacros()
    {
        var spv = SpvDebugReader.Parse(Spirv(Build(new CompileOptions { DebugInfo = DebugInfoLevel.Full, Optimize = false })));
        var lines = spv.DebugLines();
        var main = lines.Where(l => l.File == "main.hlsl").Select(l => l.Line).ToHashSet();
        var inc = lines.Where(l => l.File == "inc.hlsli").Select(l => l.Line).ToHashSet();

        Assert.Contains(6, main); // float t = a * 3.0;
        Assert.Contains(7, main); // return t;
        Assert.Contains(13, main); // float v = Triple(id.x);
        Assert.Contains(14, main); // Out[0] = Half(v);
        Assert.Contains(3, inc); // return x * 0.5; inside the included file

        // A macro expansion is attributed to the line where the macro is used, not to the #define (line 2): its column is that of the name.
        Assert.Contains(15, main);
        Assert.DoesNotContain(2, main);
        var macro = lines.Where(l => l.File == "main.hlsl" && l.Line == 15).Select(l => l.ColumnStart).ToHashSet();
        Assert.Contains(14, macro);
        Assert.All(lines, l => Assert.True(l.ColumnEnd >= l.ColumnStart));
    }

    [Fact]
    public void LinesFormUsesOpLineAndOpSourceWithText()
    {
        var spv = SpvDebugReader.Parse(Spirv(Build(new CompileOptions { DebugInfo = DebugInfoLevel.Lines, Optimize = false })));
        Assert.Empty(spv.Records);
        Assert.Empty(spv.Imports);
        Assert.Equal(Main, spv.OpSources["main.hlsl"]);
        Assert.Equal(Include, spv.OpSources["inc.hlsli"]);
        Assert.Contains(spv.OpLines, l => l.File == "main.hlsl" && l.Line == 6);
        Assert.Contains(spv.OpLines, l => l.File == "inc.hlsli" && l.Line == 3);
        Assert.Contains(spv.OpLines, l => l.File == "main.hlsl" && l.Line == 15 && l.ColumnStart == 14);
    }

    [Fact]
    public void UnoptimizedFullInfoDeclaresLocalsAndParameters()
    {
        var spv = SpvDebugReader.Parse(Spirv(Build(new CompileOptions { DebugInfo = DebugInfoLevel.Full, Optimize = false })));
        var names = spv.Of(26).Select(v => spv.Strings[v.Operands[0]]).ToList();
        Assert.Contains("t", names);
        Assert.Contains("v", names);
        Assert.Contains("a", names);
        Assert.Contains("x", names);
        Assert.NotEmpty(spv.Of(28)); // DebugDeclare
        var argNumbers = spv.Of(26).Where(v => v.Operands.Length > 7).Select(v => spv.Const(v.Operands[7])).ToList();
        Assert.Contains(1u, argNumbers);
    }

    [Fact]
    public void OptimizedFullInfoRecordsInlinedCalls()
    {
        var spv = SpvDebugReader.Parse(Spirv(Build(new CompileOptions { DebugInfo = DebugInfoLevel.Full, Optimize = true })));
        Assert.NotEmpty(spv.Of(25)); // DebugInlinedAt
        var functions = spv.Of(20).Select(f => spv.Strings[f.Operands[0]]).ToList();
        Assert.Contains("Triple", functions);
        Assert.Contains("Half", functions);
        Assert.Contains("CSMain", functions);
        // The call to Triple on line 13 is the site the inlined statements point back to.
        Assert.Contains(spv.Of(25), s => spv.Const(s.Operands[0]) == 13);
    }

    [Fact]
    public void PathsAreRelativeToTheRootAndNeverAbsolute()
    {
        const string root = "/work/project";
        var withRoot = Build(new CompileOptions { DebugInfo = DebugInfoLevel.Full, DebugSourceRoot = root }, Main, root + "/shaders/main.hlsl");
        var spv = SpvDebugReader.Parse(Spirv(withRoot));
        Assert.Contains("shaders/main.hlsl", spv.DebugSources().Keys);
        Assert.DoesNotContain(spv.Strings.Values, s => s.Contains("/work", StringComparison.Ordinal));

        // Outside the root, or with no root: the file name alone.
        var none = SpvDebugReader.Parse(Spirv(Build(new CompileOptions { DebugInfo = DebugInfoLevel.Lines }, Main, "/other/place/main.hlsl")));
        Assert.Contains("main.hlsl", none.OpSources.Keys);
        Assert.DoesNotContain(none.Strings.Values, s => s.StartsWith('/'));
    }

    [Fact]
    public void MslGetsLineDirectivesForBothFiles()
    {
        string msl = Build(new CompileOptions { Targets = [CompileTarget.Msl], DebugInfo = DebugInfoLevel.Lines, Optimize = false }).Blobs.Single().AsText();
        Assert.Contains("#line 6 \"main.hlsl\"", msl, StringComparison.Ordinal);
        Assert.Contains("#line 3 \"inc.hlsli\"", msl, StringComparison.Ordinal);
        Assert.Contains("#line 15 \"main.hlsl\"", msl, StringComparison.Ordinal);
        foreach (string line in msl.Split('\n').Where(l => l.Contains("#line", StringComparison.Ordinal)))
        {
            Assert.StartsWith("#line ", line.TrimStart(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WgslIsUnchangedByDebugInfo()
    {
        string plain = Build(new CompileOptions { Targets = [CompileTarget.Wgsl] }).Blobs.Single().AsText();
        string debug = Build(new CompileOptions { Targets = [CompileTarget.Wgsl], DebugInfo = DebugInfoLevel.Full }).Blobs.Single().AsText();
        Assert.Equal(plain, debug);
    }

    [Fact]
    public void DebugOutputIsDeterministic()
    {
        var a = Spirv(Build(new CompileOptions { DebugInfo = DebugInfoLevel.Full }));
        var b = Spirv(Build(new CompileOptions { DebugInfo = DebugInfoLevel.Full }));
        Assert.Equal(a, b);
    }

    [Fact]
    public void DebugInfoDoesNotChangeTheCode()
    {
        // Inside function bodies the opcode stream, minus line records and extended-instruction calls, is the same with and without debug info.
        static List<uint> Body(byte[] blob)
        {
            var words = new uint[blob.Length / 4];
            Buffer.BlockCopy(blob, 0, words, 0, blob.Length);
            var ops = new List<uint>();
            bool inFunction = false;
            for (int at = 5; at < words.Length;)
            {
                int count = (int)(words[at] >> 16);
                uint op = words[at] & 0xFFFF;
                if (op == SpvDebugReader.OpFunction)
                {
                    inFunction = true;
                }

                if (inFunction && op is not (SpvDebugReader.OpExtInst or SpvDebugReader.OpLine or SpvDebugReader.OpNoLine))
                {
                    ops.Add(op);
                }

                if (op == SpvDebugReader.OpFunctionEnd)
                {
                    inFunction = false;
                }

                at += count;
            }

            return ops;
        }

        var plain = Body(Spirv(Build(new CompileOptions())));
        Assert.NotEmpty(plain);
        Assert.Equal(plain, Body(Spirv(Build(new CompileOptions { DebugInfo = DebugInfoLevel.Full }))));
        Assert.Equal(plain, Body(Spirv(Build(new CompileOptions { DebugInfo = DebugInfoLevel.Lines }))));
    }

    public static IEnumerable<object[]> CorpusShaders()
    {
        foreach (string dir in new[] { "2d", "2d/renderer", "3d", "compute", "lang", "types" })
        {
            string full = Path.Combine(CorpusFiles.Root(), dir);
            if (Directory.Exists(full))
            {
                foreach (string f in CorpusFiles.Shaders(dir))
                {
                    yield return new object[] { f };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(CorpusShaders))]
    public void CorpusDebugOutputValidatesForVulkan(string path)
    {
        string? val = Oracles.Find("spirv-val");
        Assert.SkipUnless(val is not null, "spirv-val is not installed");
        string dir = Path.Combine(Path.GetTempPath(), "osh-dbg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string text = File.ReadAllText(path);
            string shaderDir = Path.GetDirectoryName(path)!;
            bool wide = path.Contains("types", StringComparison.Ordinal);
            var baseline = ShaderCompiler.Compile(text, path, new CompileOptions { Enable16BitTypes = wide, IncludeDirectories = [shaderDir] }, TestContext.Current.CancellationToken);
            Assert.SkipUnless(baseline.Success, "the shader does not compile without debug info either");
            foreach (var level in new[] { DebugInfoLevel.Lines, DebugInfoLevel.Full })
            {
                foreach (bool optimize in new[] { true, false })
                {
                    var r = ShaderCompiler.Compile(text, path, new CompileOptions { DebugInfo = level, Optimize = optimize, DebugSourceRoot = CorpusFiles.Root(), Enable16BitTypes = wide, IncludeDirectories = [shaderDir] }, TestContext.Current.CancellationToken);
                    Assert.True(r.Success, path + ": " + string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
                    foreach (var blob in r.Blobs.Where(b => b.Target == CompileTarget.SpirV))
                    {
                        string file = Path.Combine(dir, blob.EntryPoint + "-" + level + "-" + optimize + ".spv");
                        File.WriteAllBytes(file, blob.ToArray());
                        var (code, output) = Oracles.Run(val!, ["--target-env", "vulkan1.2", file], dir);
                        Assert.True(code == 0, Path.GetFileName(path) + " " + blob.EntryPoint + " " + level + " optimize=" + optimize + ": " + output);
                    }
                }
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
