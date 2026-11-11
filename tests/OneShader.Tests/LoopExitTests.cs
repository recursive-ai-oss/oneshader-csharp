using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace OneShader.Tests;

/// <summary>
/// A <c>continue</c> or <c>break</c> guarded by a condition and followed by more loop body must survive in every target's output.
/// These are structural checks (the conditional jump is there), not output comparisons; execution is covered by the
/// lang_continue_unrolled and lang_loop_exits parity cases.
/// </summary>
public sealed class LoopExitTests
{
    private const uint OpLoopMerge = 246;
    private const uint OpBranch = 249;
    private const uint OpBranchConditional = 250;

    private static string Repro() => File.ReadAllText(Path.Combine(CorpusFiles.Root(), "lang", "continue_unrolled.hlsl"));

    private static CompileResult Compile(string source, CompileTarget target, bool optimize) =>
        ShaderCompiler.Compile(source, "loops.hlsl", new CompileOptions { Targets = [target], Optimize = optimize }, TestContext.Current.CancellationToken);

    private static string Text(string source, CompileTarget target, bool optimize)
    {
        var r = Compile(source, target, optimize);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        return r.Blobs.Single().AsText();
    }

    // An `if (cond) { ...; continue; }` block in MSL: a condition, then a block that ends in continue.
    private static readonly Regex MslGuardedContinue = new(@"if \(!?\(?\w+\)?\)\s*\{[^{}]*\bcontinue;\s*\}", RegexOptions.CultureInvariant);

    // WGSL keeps `continue` as a statement of the if; the loop's increment sits in a `continuing` block.
    private static readonly Regex WgslGuardedContinue = new(@"if \(!?\(?\w+\)?\) \{\s*continue;\s*\}", RegexOptions.CultureInvariant);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MslKeepsTheGuardedContinueInBothNestedLoops(bool optimize)
    {
        string msl = Text(Repro(), CompileTarget.Msl, optimize);
        Assert.True(MslGuardedContinue.Count(msl) >= 2, msl);
        // Both guards come before the store they protect.
        int store = msl.IndexOf("Out[", StringComparison.Ordinal);
        var matches = MslGuardedContinue.Matches(msl);
        Assert.True(matches[0].Index < store && matches[1].Index < store, msl);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WgslKeepsTheGuardedContinueInBothNestedLoops(bool optimize)
    {
        string wgsl = Text(Repro(), CompileTarget.Wgsl, optimize);
        Assert.True(WgslGuardedContinue.Count(wgsl) >= 2, wgsl);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SpirvLoopContinueTargetsHaveTheGuardedEdge(bool optimize)
    {
        var r = Compile(Repro(), CompileTarget.SpirV, optimize);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        var words = ToWords(r.Blobs.Single().Data);
        var continueTargets = new List<uint>();
        var edges = new Dictionary<uint, int>();
        for (int i = 5; i < words.Length;)
        {
            uint op = words[i] & 0xFFFF;
            int count = (int)(words[i] >> 16);
            if (count == 0)
            {
                break;
            }

            if (op == OpLoopMerge)
            {
                continueTargets.Add(words[i + 2]);
            }
            else if (op == OpBranch)
            {
                edges[words[i + 1]] = edges.GetValueOrDefault(words[i + 1]) + 1;
            }
            else if (op == OpBranchConditional)
            {
                edges[words[i + 2]] = edges.GetValueOrDefault(words[i + 2]) + 1;
                edges[words[i + 3]] = edges.GetValueOrDefault(words[i + 3]) + 1;
            }

            i += count;
        }

        // Two loops. Each continue target is reached from the end of the body and from the guarded `continue`.
        Assert.Equal(2, continueTargets.Count);
        foreach (uint target in continueTargets)
        {
            Assert.True(edges.GetValueOrDefault(target) >= 2, $"continue target %{target} has {edges.GetValueOrDefault(target)} incoming branch(es)");
        }
    }

    private static uint[] ToWords(ReadOnlySpan<byte> data)
    {
        var w = new uint[data.Length / 4];
        for (int i = 0; i < w.Length; i++)
        {
            w[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]);
        }

        return w;
    }

    // Shapes of a guarded exit that is not the last statement of the body, each in its own function so the checks stay local.
    private const string Shapes = """
        RWStructuredBuffer<uint> Out : register(u0);
        StructuredBuffer<uint> In : register(t1);

        void ContinueInWhile(uint x, uint o) { uint n = 0; while (n < 8) { n++; if (In[n] == x) { continue; } Out[o + n] = 1; } }
        void ContinueInDoWhile(uint x, uint o) { uint n = 0; do { n++; if (In[n] == x) { continue; } Out[o + n] = 2; } while (n < 8); }
        void ElseContinue(uint x, uint o) { for (uint i = 0; i < 8; i++) { if (In[i] == x) { Out[o] += 1; } else { continue; } Out[o + i] = 3; } }
        void NestedContinue(uint x, uint o) { for (uint i = 0; i < 8; i++) { if (In[i] != 0) { if (In[i + 1] == x) { continue; } Out[o + i] = 4; } } }
        void BreakGuard(uint x, uint o) { [unroll] for (uint i = 0; i < 4; i++) { if (In[i] == x) { break; } Out[o + i] = 5; } }
        uint ReturnGuard(uint x) { for (uint i = 0; i < 8; i++) { if (In[i] == x) { return i; } Out[i] = 6; } return 9; }

        [shader("compute")] [numthreads(1, 1, 1)]
        void Main(uint3 id : SV_DispatchThreadID)
        {
            ContinueInWhile(id.x, 0);
            ContinueInDoWhile(id.x, 16);
            ElseContinue(id.x, 32);
            NestedContinue(id.x, 48);
            BreakGuard(id.x, 64);
            Out[80] = ReturnGuard(id.x);
        }
        """;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MslKeepsEveryGuardedExit(bool optimize)
    {
        string msl = Text(Shapes, CompileTarget.Msl, optimize);
        // Four of the functions (or their inlined bodies) have a guarded continue, some in an else arm; break and return are covered by execution.
        Assert.True(Regex.Count(msl, @"\bcontinue;") >= 4, msl);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WgslKeepsEveryGuardedExit(bool optimize)
    {
        string wgsl = Text(Shapes, CompileTarget.Wgsl, optimize);
        Assert.True(Regex.Count(wgsl, @"\bcontinue;") >= 4, wgsl);
    }
}
