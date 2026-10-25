using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using OneShader.IR;
using OneShader.Optimization;
using OneShader.Types;
using Xunit;

namespace OneShader.Tests;

/// <summary>Turns on per-pass IR verification for every compile in the test run.</summary>
internal static class OptimizerTestSetup
{
    [ModuleInitializer]
    internal static void Init() => Optimizer.VerifyEachPass = true;
}

/// <summary>A tiny interpreter for int-only functions, so tests can check that a pass kept the meaning.</summary>
internal static class MiniInterpreter
{
    public static int Run(IrFunction f, params int[] args)
    {
        var values = new Dictionary<IrValue, int>(ReferenceEqualityComparer.Instance);
        var cells = new Dictionary<Instruction, int>(ReferenceEqualityComparer.Instance);
        for (int k = 0; k < args.Length; k++)
        {
            values[f.Parameters[k]] = args[k];
        }

        int Get(IrValue v) => v is Constant c ? (int)c.Bits : values[v];
        var block = f.Blocks[0];
        BasicBlock? previous = null;
        for (int steps = 0; steps < 100_000; steps++)
        {
            var phiValues = new List<(Instruction, int)>();
            foreach (var i in block.Instructions)
            {
                if (i.Op == Opcode.Phi)
                {
                    int at = i.Targets.FindIndex(t => ReferenceEquals(t, previous));
                    phiValues.Add((i, Get(i.Operands[at])));
                }
            }

            foreach (var (p, v) in phiValues)
            {
                values[p] = v;
            }

            BasicBlock? next = null;
            foreach (var i in block.Instructions)
            {
                switch (i.Op)
                {
                    case Opcode.Phi:
                        break;
                    case Opcode.Alloca:
                        cells[i] = 0;
                        break;
                    case Opcode.Load:
                        values[i] = cells[(Instruction)i.Operands[0]];
                        break;
                    case Opcode.Store:
                        cells[(Instruction)i.Operands[0]] = Get(i.Operands[1]);
                        break;
                    case Opcode.Add:
                        values[i] = Get(i.Operands[0]) + Get(i.Operands[1]);
                        break;
                    case Opcode.Sub:
                        values[i] = Get(i.Operands[0]) - Get(i.Operands[1]);
                        break;
                    case Opcode.Mul:
                        values[i] = Get(i.Operands[0]) * Get(i.Operands[1]);
                        break;
                    case Opcode.BitAnd:
                        values[i] = Get(i.Operands[0]) & Get(i.Operands[1]);
                        break;
                    case Opcode.Lt:
                        values[i] = Get(i.Operands[0]) < Get(i.Operands[1]) ? 1 : 0;
                        break;
                    case Opcode.Ne:
                        values[i] = Get(i.Operands[0]) != Get(i.Operands[1]) ? 1 : 0;
                        break;
                    case Opcode.Eq:
                        values[i] = Get(i.Operands[0]) == Get(i.Operands[1]) ? 1 : 0;
                        break;
                    case Opcode.Select:
                        values[i] = Get(i.Operands[0]) != 0 ? Get(i.Operands[1]) : Get(i.Operands[2]);
                        break;
                    case Opcode.Branch:
                        next = i.Targets[0];
                        break;
                    case Opcode.CondBranch:
                        next = Get(i.Operands[0]) != 0 ? i.Targets[0] : i.Targets[1];
                        break;
                    case Opcode.Return:
                        return Get(i.Operands[0]);
                    default:
                        throw new InvalidOperationException("interpreter does not know " + i.Op);
                }
            }

            previous = block;
            block = next ?? throw new InvalidOperationException("block fell through");
        }

        throw new InvalidOperationException("did not terminate");
    }
}

public class OptimizerTests
{
    private static IrValue Fold(HlslType type, Func<IrBuilder, IrValue> make, Action<IrFunction>? setup = null)
    {
        var f = new IrFunction("t", type);
        setup?.Invoke(f);
        var b = new IrBuilder(f) { Block = f.AddBlock() };
        b.Return(make(b));
        InstCombine.Run(f);
        Assert.Empty(IrVerifier.Verify(f));
        return f.Blocks[0].Terminator!.Operands[0];
    }

    private static void AssertBits(IrValue v, ulong bits)
    {
        var c = Assert.IsType<Constant>(v);
        Assert.Equal(bits, c.Bits);
    }

    private static uint U(int v) => unchecked((uint)v);

    private static uint F(float f) => BitConverter.SingleToUInt32Bits(f);

    [Fact]
    public void IntegerOperationsWrapAsHlslDoes()
    {
        AssertBits(Fold(ScalarType.Int, b => b.Binary(Opcode.Add, Constant.Int(int.MaxValue), Constant.Int(1))), U(int.MinValue));
        AssertBits(Fold(ScalarType.Int, b => b.Binary(Opcode.Mul, Constant.Int(65536), Constant.Int(65536))), 0);
        AssertBits(Fold(ScalarType.UInt, b => b.Binary(Opcode.Sub, Constant.UInt(0), Constant.UInt(1))), 0xFFFF_FFFFu);
        AssertBits(Fold(ScalarType.Int, b => b.Unary(Opcode.Neg, Constant.Int(int.MinValue))), U(int.MinValue));
    }

    [Fact]
    public void ShiftCountsAreMaskedToTheOperandWidth()
    {
        AssertBits(Fold(ScalarType.Int, b => b.Binary(Opcode.Shl, Constant.Int(1), Constant.Int(33))), 2);
        AssertBits(Fold(ScalarType.Int, b => b.Binary(Opcode.Shr, Constant.Int(-8), Constant.Int(1))), U(-4));
        AssertBits(Fold(ScalarType.UInt, b => b.Binary(Opcode.Shr, Constant.UInt(0x8000_0000u), Constant.UInt(31))), 1);
        AssertBits(Fold(ScalarType.UInt, b => b.Binary(Opcode.Shr, Constant.UInt(0x8000_0000u), Constant.UInt(63))), 1);
    }

    [Fact]
    public void DivisionByZeroAndOverflowingDivisionAreLeftAlone()
    {
        Assert.IsType<Instruction>(Fold(ScalarType.Int, b => b.Binary(Opcode.Div, Constant.Int(1), Constant.Int(0))));
        Assert.IsType<Instruction>(Fold(ScalarType.Int, b => b.Binary(Opcode.Rem, Constant.Int(5), Constant.Int(0))));
        Assert.IsType<Instruction>(Fold(ScalarType.Int, b => b.Binary(Opcode.Div, Constant.Int(int.MinValue), Constant.Int(-1))));
        Assert.IsType<Instruction>(Fold(ScalarType.UInt, b => b.Binary(Opcode.Div, Constant.UInt(1), Constant.UInt(0))));
        Assert.IsType<Instruction>(Fold(ScalarType.Float, b => b.Binary(Opcode.Div, Constant.Float(1f), Constant.Float(0f))));
        AssertBits(Fold(ScalarType.Int, b => b.Binary(Opcode.Div, Constant.Int(-7), Constant.Int(2))), U(-3));
        AssertBits(Fold(ScalarType.Int, b => b.Binary(Opcode.Rem, Constant.Int(-7), Constant.Int(2))), U(-1));
    }

    [Fact]
    public void FloatFoldingKeepsSingleRoundingAndSignedZero()
    {
        AssertBits(Fold(ScalarType.Float, b => b.Binary(Opcode.Add, Constant.Float(0.1f), Constant.Float(0.2f))), F(0.1f + 0.2f));
        AssertBits(Fold(ScalarType.Float, b => b.Binary(Opcode.Mul, Constant.Float(0f), Constant.Float(-1f))), 0x8000_0000u);
        AssertBits(Fold(ScalarType.Float, b => b.Unary(Opcode.Neg, Constant.Float(0f))), 0x8000_0000u);
        AssertBits(Fold(ScalarType.Float, b => b.Binary(Opcode.Sub, Constant.Float(0f), Constant.Float(0f))), 0);
        AssertBits(Fold(ScalarType.Bool, b => b.Compare(Opcode.Eq, Constant.Float(0f), Constant.Float(-0f))), 1);
    }

    [Fact]
    public void NanAndInfinityResultsAreNotFolded()
    {
        float inf = float.PositiveInfinity;
        Assert.IsType<Instruction>(Fold(ScalarType.Float, b => b.Binary(Opcode.Sub, Constant.Float(inf), Constant.Float(inf))));
        Assert.IsType<Instruction>(Fold(ScalarType.Float, b => b.Binary(Opcode.Div, Constant.Float(0f), Constant.Float(0f))));
        Assert.IsType<Instruction>(Fold(ScalarType.Float, b => b.Binary(Opcode.Mul, Constant.Float(3e38f), Constant.Float(10f))));
        Assert.IsType<Instruction>(Fold(ScalarType.Float, b => b.Binary(Opcode.Add, Constant.Float(float.NaN), Constant.Float(1f))));
    }

    [Fact]
    public void ConversionsFoldOnlyWhenTheResultIsDefined()
    {
        AssertBits(Fold(ScalarType.Int, b => b.Convert(Constant.Float(3.7f), ScalarType.Int)), 3);
        AssertBits(Fold(ScalarType.Int, b => b.Convert(Constant.Float(-3.7f), ScalarType.Int)), U(-3));
        AssertBits(Fold(ScalarType.Float, b => b.Convert(Constant.Int(-5), ScalarType.Float)), F(-5f));
        Assert.IsType<Instruction>(Fold(ScalarType.Int, b => b.Convert(Constant.Float(1e20f), ScalarType.Int)));
        Assert.IsType<Instruction>(Fold(ScalarType.Int, b => b.Convert(Constant.Float(float.NaN), ScalarType.Int)));
        AssertBits(Fold(ScalarType.UInt, b => b.Bitcast(Constant.Float(1f), ScalarType.UInt)), 0x3F80_0000u);
    }

    [Fact]
    public void VectorsFoldComponentWise()
    {
        var v4 = HlslType.Vector(ScalarType.Int, 2);
        var a = Constant.Composite(v4, [Constant.Int(1), Constant.Int(int.MaxValue)]);
        var one = Constant.Composite(v4, [Constant.Int(1), Constant.Int(1)]);
        var sum = Assert.IsType<Constant>(Fold(v4, b => b.Binary(Opcode.Add, a, one)));
        Assert.Equal(2UL, sum.Elements[0].Bits);
        Assert.Equal(U(int.MinValue), sum.Elements[1].Bits);
    }

    [Fact]
    public void AlgebraicIdentitiesHoldForEveryInputAndRespectPrecise()
    {
        IrValue Case(HlslType t, Func<IrBuilder, Argument, IrValue> make)
        {
            Argument? arg = null;
            return Fold(t, b => make(b, arg!), f => arg = f.AddParameter(t, "x"));
        }

        Assert.IsType<Argument>(Case(ScalarType.Int, (b, x) => b.Binary(Opcode.Add, x, Constant.Int(0))));
        Assert.IsType<Argument>(Case(ScalarType.Float, (b, x) => b.Binary(Opcode.Add, x, Constant.Float(-0f))));
        Assert.IsType<Instruction>(Case(ScalarType.Float, (b, x) => b.Binary(Opcode.Add, x, Constant.Float(0f)))); // -0 + +0 is +0
        Assert.IsType<Argument>(Case(ScalarType.Float, (b, x) => b.Binary(Opcode.Mul, x, Constant.Float(1f))));
        Assert.IsType<Instruction>(Case(ScalarType.Float, (b, x) => b.Binary(Opcode.Mul, x, Constant.Float(0f)))); // NaN and sign
        AssertBits(Case(ScalarType.Int, (b, x) => b.Binary(Opcode.Mul, x, Constant.Int(0))), 0);
        Assert.IsType<Instruction>(Case(ScalarType.Float, (b, x) =>
        {
            var m = b.Binary(Opcode.Mul, x, Constant.Float(1f));
            m.Precise = true;
            return m;
        }));
    }

    [Fact]
    public void ExtractFromConstructFindsTheOperand()
    {
        var v4 = HlslType.Vector(ScalarType.Float, 4);
        Argument? y = null;
        var r = Fold(ScalarType.Float, b =>
        {
            var c = b.Construct(v4, Constant.Float(1f), y!, Constant.Float(3f), Constant.Float(4f));
            return b.Extract(c, ScalarType.Float, 1);
        }, f => y = f.AddParameter(ScalarType.Float, "y"));
        Assert.Same(y, r);
    }

    private static bool HasOp(IrFunction f, Opcode op) => f.Blocks.Any(b => b.Instructions.Any(i => i.Op == op));

    [Fact]
    public void LoopVariablesBecomePhisAndKeepTheirMeaning()
    {
        var (_, f) = IrTests.BuildLoopWithIf();
        int[] before = [Run(f, 0), Run(f, 1), Run(f, 10), Run(f, 25)];
        Assert.Equal([0, 0, 25, 144], before);

        Assert.True(Mem2Reg.Run(f));
        Assert.Empty(IrVerifier.Verify(f));
        Assert.False(HasOp(f, Opcode.Alloca));
        Assert.False(HasOp(f, Opcode.Load));
        Assert.False(HasOp(f, Opcode.Store));
        var header = f.Blocks[1];
        Assert.Equal(StructureKind.Loop, header.Structure);
        Assert.Equal(2, header.Instructions.Count(i => i.Op == Opcode.Phi)); // sum and i, merged at the loop header
        Assert.Equal(before, new[] { Run(f, 0), Run(f, 1), Run(f, 10), Run(f, 25) });

        InstCombine.Run(f);
        Dce.Run(f);
        SimplifyCfg.Run(f);
        Assert.Empty(IrVerifier.Verify(f));
        Assert.Equal(before, new[] { Run(f, 0), Run(f, 1), Run(f, 10), Run(f, 25) });
    }

    private static int Run(IrFunction f, int n) => MiniInterpreter.Run(f, n);

    [Fact]
    public void ConstantConditionInsideALoopLosesItsDeadArm()
    {
        var (_, f) = IrTests.BuildLoopWithIf();
        Mem2Reg.Run(f);
        // Make the odd/even test constant: every iteration takes the "then" arm.
        var body = f.Blocks.Single(b => b.Structure == StructureKind.Selection);
        body.Terminator!.Operands[0] = Constant.Bool(true);
        int[] expected = [0, 0, 45, 300];
        Assert.Equal(expected, new[] { Run(f, 0), Run(f, 1), Run(f, 10), Run(f, 25) });

        int blocksBefore = f.Blocks.Count;
        Assert.True(SimplifyCfg.Run(f));
        Assert.Empty(IrVerifier.Verify(f));
        Assert.True(f.Blocks.Count < blocksBefore);
        Assert.DoesNotContain(f.Blocks, b => b.Structure == StructureKind.Selection);
        Assert.Equal(1, f.Blocks.Count(b => b.Structure == StructureKind.Loop));
        var loop = f.Blocks.Single(b => b.Structure == StructureKind.Loop);
        Assert.Contains(loop.MergeBlock, f.Blocks);
        Assert.Contains(loop.ContinueBlock, f.Blocks);
        Assert.Equal(expected, new[] { Run(f, 0), Run(f, 1), Run(f, 10), Run(f, 25) });
    }

    [Fact]
    public void UnusedTextureSampleIsRemovedButUsedAndStoredOnesStay()
    {
        const string src = """
            [[vk::binding(0, 0)]] Texture2D<float4> Tex;
            [[vk::binding(1, 0)]] SamplerState Smp;
            [[vk::binding(2, 0)]] RWTexture2D<float4> Dst;
            [shader("pixel")]
            float4 PSMain(float2 uv : TEXCOORD0) : SV_Target
            {
                float4 unused = Tex.Sample(Smp, uv * 2.0);
                float4 used = Tex.Sample(Smp, uv);
                Dst[uint2(1, 2)] = used;
                return used * 0.5;
            }
            """;
        var r = Semantics.Frontend.Run(src, "t.hlsl", new CompileOptions(), TestContext.Current.CancellationToken);
        Assert.Empty(r.Diagnostics.Items);
        Assert.Equal(2, r.Module!.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions).Count(i => i.Op == Opcode.TextureSample));
        Assert.Null(Optimizer.Run(r.Module, TestContext.Current.CancellationToken));
        var all = r.Module.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions).ToList();
        Assert.Single(all, i => i.Op == Opcode.TextureSample);
        Assert.Single(all, i => i.Op == Opcode.TextureStore);
    }

    [Fact]
    public void CommonSubexpressionsAndConstantBufferLoadsAreShared()
    {
        const string src = """
            cbuffer C : register(b0) { float4x4 M; float Scale; };
            [shader("vertex")]
            float4 VSMain(float4 p : POSITION) : SV_Position
            {
                float4 a = mul(M, p) * Scale;
                float4 b = mul(M, p) * Scale;
                return a + b;
            }
            """;
        var r = Semantics.Frontend.Run(src, "t.hlsl", new CompileOptions(), TestContext.Current.CancellationToken);
        Assert.Empty(r.Diagnostics.Items);
        Assert.Null(Optimizer.Run(r.Module!, TestContext.Current.CancellationToken));
        var all = r.Module!.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instructions).ToList();
        Assert.Single(all, i => i.Op == Opcode.MatMul);
    }

    [Fact]
    public void RecursionIsLeftAloneAndDoesNotHang()
    {
        var module = new IrModule { Name = "rec.hlsl" };
        var self = new IrFunction("loopy", ScalarType.Int);
        self.AddParameter(ScalarType.Int, "n");
        var b = new IrBuilder(self) { Block = self.AddBlock() };
        b.Return(b.Call(self, self.Parameters[0]));
        var entry = new IrFunction("main", HlslType.Void);
        var eb = new IrBuilder(entry) { Block = entry.AddBlock() };
        eb.Call(self, Constant.Int(3));
        eb.Return();
        module.Functions.Add(self);
        module.Functions.Add(entry);
        module.EntryPoints.Add(new EntryPoint(entry, ShaderStage.Compute));

        Assert.Null(Optimizer.Run(module, TestContext.Current.CancellationToken));
        Assert.Contains(module.Functions, f => f.Name == "loopy");
        Assert.Empty(IrVerifier.Verify(module));
    }

    [Fact]
    public void BrokenIrIsReportedNotThrown()
    {
        var module = new IrModule { Name = "bad.hlsl" };
        var f = new IrFunction("main", HlslType.Void);
        f.AddBlock(); // no terminator
        module.Functions.Add(f);
        module.EntryPoints.Add(new EntryPoint(f, ShaderStage.Compute));
        var message = Optimizer.Run(module, TestContext.Current.CancellationToken);
        Assert.NotNull(message);
        Assert.Contains("terminator", message, StringComparison.Ordinal);
    }

    private static void AssertOptimizesCleanly(string src)
    {
        var r = Semantics.Frontend.Run(src, "t.hlsl", new CompileOptions(), TestContext.Current.CancellationToken);
        Assert.Empty(r.Diagnostics.Items);
        Assert.Null(Optimizer.Run(r.Module!, TestContext.Current.CancellationToken));
        var result = ShaderCompiler.Compile(src, "t.hlsl", new CompileOptions(), TestContext.Current.CancellationToken);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void LoopsWhoseContinuePathIsDeadKeepAValidHeaderPhi()
    {
        // The inner loop body always returns, so its continue block is unreachable; the loop header's phis
        // must not keep values from the emptied block.
        AssertOptimizesCleanly("""
            [[vk::binding(0, 0)]] RWStructuredBuffer<uint> Output;
            uint F(uint t)
            {
                for (uint i = 0; i < 6; i++)
                {
                    for (uint j = 0; j < 6; j++)
                    {
                        return i;
                        if (i * j == t && i > 1) { return 1; }
                    }
                }
                return 1000;
            }
            [shader("compute")] [numthreads(1, 1, 1)]
            void CSMain(uint3 id : SV_DispatchThreadID) { Output[0] = F(id.x); }
            """);
    }

    [Fact]
    public void LoopThatNeverRunsLeavesNoStaleHeaderPhi()
    {
        // After folding, the body and the step (which defines the loop-carried values) are unreachable.
        AssertOptimizesCleanly("""
            [[vk::binding(0, 0)]] RWStructuredBuffer<uint> Output;
            uint F(uint a)
            {
                uint s = a;
                float f = 1.0;
                for (uint i = 0; 0 > 1; i++)
                {
                    s += i * a;
                    f *= 2.0;
                }
                return s + (uint)f;
            }
            [shader("compute")] [numthreads(1, 1, 1)]
            void CSMain(uint3 id : SV_DispatchThreadID) { Output[0] = F(id.x); }
            """);
    }

    [Fact]
    public void ExponentialCallNestingStaysBounded()
    {
        // Each function calls the previous one twice: full inlining would be 2^24 copies.
        var sb = new System.Text.StringBuilder();
        sb.Append("[[vk::binding(0, 0)]] RWStructuredBuffer<uint> Output;\nuint f0(uint x) { return x * 3 + 1; }\n");
        for (int i = 1; i <= 24; i++)
        {
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"uint f{i}(uint x) {{ return f{i - 1}(x) + f{i - 1}(x ^ {i}u); }}\n");
        }

        sb.Append("[shader(\"compute\")] [numthreads(1, 1, 1)] void CSMain(uint3 id : SV_DispatchThreadID) { Output[0] = f24(id.x); }\n");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = ShaderCompiler.Compile(sb.ToString(), "deep.hlsl", new CompileOptions(), TestContext.Current.CancellationToken);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "took " + clock.Elapsed);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
    }

    public static IEnumerable<object[]> StressFiles() =>
        CorpusFiles.Graphics().Concat(CorpusFiles.Shaders("compute"))
            .Concat(Directory.GetFiles(OptimizerCaseDir(), "*.hlsl").OrderBy(p => p, StringComparer.Ordinal))
            .Select(p => new object[] { p });

    private static string OptimizerCaseDir([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "OneShader.ParityTests", "OptimizerCases"));

    [Theory]
    [MemberData(nameof(StressFiles))]
    public void OptimizedCorpusCompilesCleanDeterministicallyAndNotLarger(string path)
    {
        string text = File.ReadAllText(path);
        var on = new CompileOptions { IncludeDirectories = [Path.GetDirectoryName(path)!] };
        var off = new CompileOptions { IncludeDirectories = [Path.GetDirectoryName(path)!], Optimize = false };
        var a = ShaderCompiler.Compile(text, path, on, TestContext.Current.CancellationToken);
        var b = ShaderCompiler.Compile(text, path, on, TestContext.Current.CancellationToken);
        var baseline = ShaderCompiler.Compile(text, path, off, TestContext.Current.CancellationToken);
        Assert.Empty(a.Diagnostics);
        Assert.True(a.Success);
        Assert.Equal(baseline.Blobs.Count, a.Blobs.Count);
        string? val = Oracles.Find("spirv-val");
        string dir = Path.Combine(Path.GetTempPath(), "osh-opt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            for (int k = 0; k < a.Blobs.Count; k++)
            {
                Assert.True(a.Blobs[k].Data.SequenceEqual(b.Blobs[k].Data), "output differs between two compiles of " + path);
                if (!path.StartsWith(OptimizerCaseDir(), StringComparison.Ordinal))
                {
                    // Inlining a function at many call sites may legitimately grow the stress shaders; the corpus must shrink.
                    Assert.True(a.Blobs[k].Data.Length <= baseline.Blobs[k].Data.Length, "optimized output is larger for " + a.Blobs[k].EntryPoint);
                }

                if (val is not null)
                {
                    string file = Path.Combine(dir, a.Blobs[k].EntryPoint + ".spv");
                    File.WriteAllBytes(file, a.Blobs[k].ToArray());
                    var (code, output) = Oracles.Run(val, ["--target-env", "vulkan1.2", file], dir);
                    Assert.True(code == 0, Path.GetFileName(path) + " " + a.Blobs[k].EntryPoint + ": " + output);
                }
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
