using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using OneShader.Types;

namespace OneShader.IR;

/// <summary>
/// Produces the deterministic text dump of the IR used by tests and debugging. Values are numbered
/// in print order per function (<c>%0</c>, <c>%1</c>, ...), blocks by position (<c>bb0</c>, ...), so
/// two structurally equal modules print identically regardless of object identity or names.
/// </summary>
public static class IrPrinter
{
    /// <summary>Prints a module.</summary>
    public static string Print(IrModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        var sb = new StringBuilder();
        sb.Append("module ").Append(module.Name).Append('\n');
        foreach (var g in module.Globals)
        {
            PrintGlobal(sb, g);
        }

        foreach (var f in module.Functions)
        {
            sb.Append('\n');
            PrintFunction(sb, f);
        }

        foreach (var e in module.EntryPoints)
        {
            sb.Append("\nentry ").Append(e.Stage.ToString().ToLowerInvariant()).Append(" @").Append(e.Function.Name);
            if (e.Stage == ShaderStage.Compute)
            {
                sb.Append(" workgroup(").Append(e.WorkgroupSize.X.ToString(CultureInfo.InvariantCulture)).Append(", ")
                  .Append(e.WorkgroupSize.Y.ToString(CultureInfo.InvariantCulture)).Append(", ")
                  .Append(e.WorkgroupSize.Z.ToString(CultureInfo.InvariantCulture)).Append(')');
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Prints one function.</summary>
    public static string Print(IrFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        var sb = new StringBuilder();
        PrintFunction(sb, function);
        return sb.ToString();
    }

    private static void PrintGlobal(StringBuilder sb, GlobalVariable g)
    {
        sb.Append('@').Append(g.Name).Append(" : ").Append(g.Type).Append(' ').Append(g.GlobalKind.ToString().ToLowerInvariant());
        if (g.Set is { } set)
        {
            sb.Append(" set=").Append(set.ToString(CultureInfo.InvariantCulture));
        }

        if (g.Binding is { } binding)
        {
            sb.Append(" binding=").Append(binding.ToString(CultureInfo.InvariantCulture));
        }

        if (g.Location is { } loc)
        {
            sb.Append(" location=").Append(loc.ToString(CultureInfo.InvariantCulture));
        }

        if (g.BuiltIn != Reflection.BuiltIn.None)
        {
            sb.Append(" builtin=").Append(g.BuiltIn);
        }

        if (g.Initializer is not null)
        {
            sb.Append(" = ").Append(Const(g.Initializer));
        }

        sb.Append('\n');
    }

    private static void PrintFunction(StringBuilder sb, IrFunction f)
    {
        var names = new Dictionary<IrValue, string>(ReferenceEqualityComparer.Instance);
        var blocks = new Dictionary<BasicBlock, string>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < f.Blocks.Count; i++)
        {
            blocks[f.Blocks[i]] = "bb" + i.ToString(CultureInfo.InvariantCulture);
        }

        foreach (var p in f.Parameters)
        {
            names[p] = "%arg" + p.Index.ToString(CultureInfo.InvariantCulture);
        }

        int next = 0;
        foreach (var b in f.Blocks)
        {
            foreach (var ins in b.Instructions)
            {
                if (!ins.IsVoid)
                {
                    names[ins] = "%" + (next++).ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        sb.Append("func @").Append(f.Name).Append('(');
        for (int i = 0; i < f.Parameters.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append(names[f.Parameters[i]]).Append(": ").Append(f.Parameters[i].Type);
        }

        sb.Append(") -> ").Append(f.ReturnType);
        if (f.Inline != InlineHint.Auto)
        {
            sb.Append(" inline=").Append(f.Inline.ToString().ToLowerInvariant());
        }

        sb.Append(" {\n");
        foreach (var b in f.Blocks)
        {
            sb.Append(blocks[b]).Append(':');
            if (b.Structure == StructureKind.Selection)
            {
                sb.Append(" ; selection merge=").Append(Block(blocks, b.MergeBlock));
            }
            else if (b.Structure == StructureKind.Loop)
            {
                sb.Append(" ; loop merge=").Append(Block(blocks, b.MergeBlock)).Append(" continue=").Append(Block(blocks, b.ContinueBlock));
            }

            if (b.Hint != ControlHint.None)
            {
                sb.Append(" hint=").Append(b.Hint.ToString().ToLowerInvariant());
            }

            if (b.Name is not null)
            {
                sb.Append(b.Structure == StructureKind.None ? " ; " : " ").Append('(').Append(b.Name).Append(')');
            }

            sb.Append('\n');
            foreach (var ins in b.Instructions)
            {
                PrintInstruction(sb, ins, names, blocks);
            }
        }

        sb.Append("}\n");
    }

    private static string Block(Dictionary<BasicBlock, string> blocks, BasicBlock? b) =>
        b is not null && blocks.TryGetValue(b, out var n) ? n : "<none>";

    private static void PrintInstruction(StringBuilder sb, Instruction ins, Dictionary<IrValue, string> names, Dictionary<BasicBlock, string> blocks)
    {
        sb.Append("  ");
        if (!ins.IsVoid)
        {
            sb.Append(names[ins]).Append(" = ");
        }

        if (ins.Op == Opcode.Phi)
        {
            sb.Append("phi");
            for (int i = 0; i < ins.Operands.Count; i++)
            {
                sb.Append(i == 0 ? " " : ", ").Append('[').Append(Operand(ins.Operands[i], names)).Append(", ").Append(Block(blocks, ins.Targets[i])).Append(']');
            }

            sb.Append(" : ").Append(ins.Type).Append('\n');
            return;
        }

        sb.Append(OpName(ins.Op));
        if (ins.Op == Opcode.CallIntrinsic)
        {
            sb.Append(' ').Append(ins.Intrinsic.ToString().ToLowerInvariant());
        }
        else if (ins.Op == Opcode.Call)
        {
            sb.Append(" @").Append(ins.Callee?.Name ?? "<null>");
        }

        if (ins.Precise)
        {
            sb.Append(" precise");
        }

        for (int i = 0; i < ins.Operands.Count; i++)
        {
            sb.Append(i == 0 ? " " : ", ").Append(Operand(ins.Operands[i], names));
        }

        if (ins.Op == Opcode.Switch)
        {
            sb.Append(", default ").Append(Block(blocks, ins.Targets[0]));
            for (int i = 0; i < ins.Immediates.Count; i++)
            {
                sb.Append(", ").Append(ins.Immediates[i].ToString(CultureInfo.InvariantCulture)).Append(": ").Append(Block(blocks, ins.Targets[i + 1]));
            }
        }
        else
        {
            if (ins.Immediates.Count > 0)
            {
                sb.Append(" [");
                for (int i = 0; i < ins.Immediates.Count; i++)
                {
                    sb.Append(i == 0 ? "" : ", ").Append(ins.Immediates[i].ToString(CultureInfo.InvariantCulture));
                }

                sb.Append(']');
            }

            for (int i = 0; i < ins.Targets.Count; i++)
            {
                sb.Append(ins.Operands.Count == 0 && i == 0 ? " " : ", ").Append(Block(blocks, ins.Targets[i]));
            }
        }

        if (!ins.IsVoid)
        {
            sb.Append(" : ").Append(ins.Type);
        }

        if (ins.Name is not null)
        {
            sb.Append(" ; ").Append(ins.Name);
        }

        sb.Append('\n');
    }

    private static string Operand(IrValue v, Dictionary<IrValue, string> names) => v switch
    {
        Constant c => Const(c),
        GlobalVariable g => "@" + g.Name,
        _ => names.TryGetValue(v, out var n) ? n : "%<unnamed>",
    };

    /// <summary>Prints a constant as <c>value:type</c>; floats use the shortest round-trip form.</summary>
    private static string Const(Constant c)
    {
        switch (c.ConstantKind)
        {
            case ConstantKind.Undef:
                return "undef:" + c.Type;
            case ConstantKind.Composite:
            {
                var sb = new StringBuilder("{");
                for (int i = 0; i < c.Elements.Count; i++)
                {
                    sb.Append(i == 0 ? "" : ", ").Append(Const(c.Elements[i]));
                }

                return sb.Append("}:").Append(c.Type).ToString();
            }

            default:
            {
                var s = (ScalarType)c.Type;
                string text = s.ScalarKind switch
                {
                    ScalarKind.Bool => c.Bits != 0 ? "true" : "false",
                    ScalarKind.Float or ScalarKind.LiteralFloat => ((float)c.AsDouble()).ToString("R", CultureInfo.InvariantCulture),
                    ScalarKind.Double or ScalarKind.Half or ScalarKind.Min16Float or ScalarKind.Min10Float => c.AsDouble().ToString("R", CultureInfo.InvariantCulture),
                    _ => s.IsSignedInteger ? ((long)c.AsDouble()).ToString(CultureInfo.InvariantCulture) : c.Bits.ToString(CultureInfo.InvariantCulture),
                };
                return text + ":" + s;
            }
        }
    }

    private static string OpName(Opcode op) => op switch
    {
        Opcode.AccessChain => "access",
        Opcode.ArrayLength => "arraylen",
        Opcode.AtomicRmw => "atomic",
        Opcode.AtomicCompareExchange => "cmpxchg",
        Opcode.CompositeConstruct => "construct",
        Opcode.CompositeExtract => "extract",
        Opcode.CompositeInsert => "insert",
        Opcode.VectorShuffle => "shuffle",
        Opcode.VectorExtractDynamic => "extractdyn",
        Opcode.VectorInsertDynamic => "insertdyn",
        Opcode.CallIntrinsic => "intrinsic",
        Opcode.CondBranch => "condbr",
        Opcode.Branch => "br",
        Opcode.Return => "ret",
        _ => op.ToString().ToLowerInvariant(),
    };
}
