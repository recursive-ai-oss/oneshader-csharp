using System.Collections.Generic;
using OneShader.IR;
using OneShader.Types;
using Xunit;

namespace OneShader.Tests;

public class IrTests
{
    private static (IrModule Module, IrFunction Function) BuildLoopWithIf()
    {
        // int f(int n) { int sum = 0; for (int i = 0; i < n; i++) { if ((i & 1) != 0) sum += i; } return sum; }
        var module = new IrModule { Name = "loop.hlsl" };
        var f = new IrFunction("f", ScalarType.Int);
        module.Functions.Add(f);
        var n = f.AddParameter(ScalarType.Int, "n");
        var entry = f.AddBlock("entry");
        var header = f.AddBlock("for.header");
        var cond = f.AddBlock("for.cond");
        var body = f.AddBlock("for.body");
        var step = f.AddBlock("for.step");
        var exit = f.AddBlock("for.exit");
        var then = f.AddBlock("if.then");
        var join = f.AddBlock("if.join");

        var b = new IrBuilder(f) { Block = entry };
        var sum = b.Alloca(ScalarType.Int, "sum");
        var i = b.Alloca(ScalarType.Int, "i");
        b.Store(sum, Constant.Int(0));
        b.Store(i, Constant.Int(0));
        b.Branch(header);

        b.Block = header;
        b.Branch(cond);
        IrBuilder.MarkLoop(header, exit, step, ControlHint.DontUnroll);

        b.Block = cond;
        var iv = b.Load(i);
        var lt = b.Compare(Opcode.Lt, iv, n);
        b.CondBranch(lt, body, exit);

        b.Block = body;
        var iv2 = b.Load(i);
        var bit = b.Binary(Opcode.BitAnd, iv2, Constant.Int(1));
        var odd = b.Compare(Opcode.Ne, bit, Constant.Int(0));
        b.CondBranch(odd, then, join);
        IrBuilder.MarkSelection(body, join);

        b.Block = then;
        var s0 = b.Load(sum);
        var iv3 = b.Load(i);
        b.Store(sum, b.Binary(Opcode.Add, s0, iv3));
        b.Branch(join);

        b.Block = join;
        b.Branch(step);

        b.Block = step;
        var iv4 = b.Load(i);
        b.Store(i, b.Binary(Opcode.Add, iv4, Constant.Int(1)));
        b.Branch(header);

        b.Block = exit;
        b.Return(b.Load(sum));
        return (module, f);
    }

    [Fact]
    public void LoopWithConditionalDumpsDeterministically()
    {
        var (module, _) = BuildLoopWithIf();
        Assert.Empty(IrVerifier.Verify(module));

        string dump = IrPrinter.Print(module);
        const string expected = """
            module loop.hlsl

            func @f(%arg0: int) -> int {
            bb0: ; (entry)
              %0 = alloca : ptr<function, int> ; sum
              %1 = alloca : ptr<function, int> ; i
              store %0, 0:int
              store %1, 0:int
              br bb1
            bb1: ; loop merge=bb5 continue=bb4 hint=dontunroll (for.header)
              br bb2
            bb2: ; (for.cond)
              %2 = load %1 : int
              %3 = lt %2, %arg0 : bool
              condbr %3, bb3, bb5
            bb3: ; selection merge=bb7 (for.body)
              %4 = load %1 : int
              %5 = bitand %4, 1:int : int
              %6 = ne %5, 0:int : bool
              condbr %6, bb6, bb7
            bb4: ; (for.step)
              %7 = load %1 : int
              %8 = add %7, 1:int : int
              store %1, %8
              br bb1
            bb5: ; (for.exit)
              %9 = load %0 : int
              ret %9
            bb6: ; (if.then)
              %10 = load %0 : int
              %11 = load %1 : int
              %12 = add %10, %11 : int
              store %0, %12
              br bb7
            bb7: ; (if.join)
              br bb4
            }

            """;
        Assert.Equal(expected, dump);

        // Same construction twice gives identical text.
        Assert.Equal(dump, IrPrinter.Print(BuildLoopWithIf().Module));
    }

    [Fact]
    public void VerifierFindsBrokenBlocksAndTypes()
    {
        var f = new IrFunction("bad", HlslType.Void);
        var e = f.AddBlock();
        var b = new IrBuilder(f) { Block = e };
        var x = b.Alloca(ScalarType.Int);
        b.Emit(Opcode.Add, ScalarType.Int, Constant.Int(1), Constant.Float(1f));
        b.Store(x, Constant.Float(2f));

        var errors = IrVerifier.Verify(f);
        Assert.Contains(errors, m => m.Contains("does not end in a terminator", System.StringComparison.Ordinal));
        Assert.Contains(errors, m => m.Contains("same type", System.StringComparison.Ordinal));
        Assert.Contains(errors, m => m.Contains("store needs", System.StringComparison.Ordinal));
    }

    [Fact]
    public void BuilderRefusesToAppendAfterTerminator()
    {
        var f = new IrFunction("g", HlslType.Void);
        var b = new IrBuilder(f) { Block = f.AddBlock() };
        b.Return();
        Assert.Throws<System.InvalidOperationException>(() => b.Return());
    }

    [Fact]
    public void ConstantsKeepExactBits()
    {
        var nan = Constant.Float(float.NaN);
        Assert.True(nan.ValueEquals(Constant.Float(float.NaN)));
        Assert.False(Constant.Float(0f).ValueEquals(Constant.Float(-0f)));
        var v = Constant.Zero(HlslType.Vector(ScalarType.Float, 3));
        Assert.Equal(3, v.Elements.Count);
        Assert.Equal(1.5, Constant.Float(1.5f).AsDouble());
    }

    [Fact]
    public void PhiAndSwitchPrint()
    {
        var f = new IrFunction("p", ScalarType.Int);
        var a = f.AddBlock();
        var c1 = f.AddBlock();
        var c2 = f.AddBlock();
        var m = f.AddBlock();
        var arg = f.AddParameter(ScalarType.Int);
        var b = new IrBuilder(f) { Block = a };
        b.Switch(arg, c2, [new KeyValuePair<int, BasicBlock>(1, c1)]);
        IrBuilder.MarkSelection(a, m);
        b.Block = c1;
        b.Branch(m);
        b.Block = c2;
        b.Branch(m);
        b.Block = m;
        var phi = b.Phi(m, ScalarType.Int, [new(c1, Constant.Int(10)), new(c2, Constant.Int(20))]);
        b.Return(phi);
        Assert.Empty(IrVerifier.Verify(f));
        string dump = IrPrinter.Print(f);
        Assert.Contains("switch %arg0, default bb2, 1: bb1", dump, System.StringComparison.Ordinal);
        Assert.Contains("%0 = phi [10:int, bb1], [20:int, bb2] : int", dump, System.StringComparison.Ordinal);
    }
}
