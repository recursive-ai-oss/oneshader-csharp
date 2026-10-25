using System.Collections.Generic;
using OneShader.IR;

namespace OneShader.Optimization;

/// <summary>
/// Simplifies the block graph without breaking structured control flow: conditional branches on a
/// constant become plain branches, unreachable blocks go (merge and continue blocks that a live header
/// still names are kept as stubs), and a block that is its predecessor's only successor is folded into it.
/// Merge blocks, continue blocks and headers are never merged away.
/// </summary>
internal static class SimplifyCfg
{
    public static bool Run(IrFunction f)
    {
        bool any = false;
        for (int round = 0; round < 64; round++)
        {
            bool changed = FoldBranches(f);
            changed |= RemoveUnreachable(f);
            changed |= MergeBlocks(f);
            if (!changed)
            {
                break;
            }

            any = true;
        }

        return any;
    }

    private static bool FoldBranches(IrFunction f)
    {
        bool changed = false;
        foreach (var b in f.Blocks)
        {
            var t = b.Terminator;
            if (t is null || t.Op != Opcode.CondBranch)
            {
                continue;
            }

            BasicBlock taken;
            BasicBlock? dropped = null;
            if (ReferenceEquals(t.Targets[0], t.Targets[1]))
            {
                taken = t.Targets[0];
            }
            else if (t.Operands[0] is Constant { ConstantKind: ConstantKind.Scalar } c)
            {
                taken = c.Bits != 0 ? t.Targets[0] : t.Targets[1];
                dropped = c.Bits != 0 ? t.Targets[1] : t.Targets[0];
            }
            else
            {
                continue;
            }

            if (dropped is not null)
            {
                IrUtil.RemovePhiPred(dropped, b);
            }

            t.Op = Opcode.Branch;
            t.Operands.Clear();
            t.Targets.Clear();
            t.Targets.Add(taken);
            if (b.Structure == StructureKind.Selection)
            {
                b.Structure = StructureKind.None;
                b.MergeBlock = null;
                b.Hint = ControlHint.None;
            }

            changed = true;
        }

        return changed;
    }

    private static bool RemoveUnreachable(IrFunction f)
    {
        if (f.Blocks.Count == 0)
        {
            return false;
        }

        var cfg = new Cfg(f);
        var kept = new HashSet<BasicBlock>(ReferenceEqualityComparer.Instance);
        var stubs = new HashSet<BasicBlock>(ReferenceEqualityComparer.Instance);
        for (int b = 0; b < f.Blocks.Count; b++)
        {
            if (cfg.IsReachable(b))
            {
                kept.Add(f.Blocks[b]);
            }
        }

        // A live header still names its merge and continue blocks; SPIR-V needs them to exist even when nothing reaches them.
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var b in f.Blocks)
            {
                if (!kept.Contains(b) || stubs.Contains(b) || b.Structure == StructureKind.None)
                {
                    continue;
                }

                foreach (var named in (BasicBlock?[])[b.MergeBlock, b.ContinueBlock])
                {
                    if (named is not null && kept.Add(named))
                    {
                        stubs.Add(named);
                        grew = true;
                    }
                }
            }
        }

        // Turn stubs into minimal blocks and cut every edge that disappears.
        bool changed = false;
        var loopHeaderOf = new Dictionary<BasicBlock, BasicBlock>(ReferenceEqualityComparer.Instance);
        foreach (var b in f.Blocks)
        {
            if (kept.Contains(b) && !stubs.Contains(b) && b.Structure == StructureKind.Loop && b.ContinueBlock is { } cb)
            {
                loopHeaderOf[cb] = b;
            }
        }

        // Continue blocks nothing reaches: their phi edges into the header must stop carrying values from dead code,
        // whether or not the block itself needs emptying.
        var deadContinues = new List<BasicBlock>();
        foreach (var b in f.Blocks)
        {
            if (stubs.Contains(b) && loopHeaderOf.ContainsKey(b))
            {
                deadContinues.Add(b);
            }
        }

        stubs.RemoveWhere(b => IsMinimalStub(b, loopHeaderOf));

        foreach (var b in f.Blocks)
        {
            if (!stubs.Contains(b))
            {
                continue;
            }

            // Only blocks that were actually unreachable get gutted. A stub that was never in the live set is
            // the only kind here, because reachable blocks are not added to the stub set.
            var old = b.Terminator;
            var oldTargets = old is null ? new List<BasicBlock>() : new List<BasicBlock>(old.Targets);
            b.Instructions.Clear();
            var term = new Instruction(loopHeaderOf.ContainsKey(b) ? Opcode.Branch : Opcode.Unreachable, OneShader.Types.HlslType.Void, new List<IrValue>()) { Parent = b };
            if (loopHeaderOf.TryGetValue(b, out var header))
            {
                term.Targets.Add(header);
            }

            b.Instructions.Add(term);
            b.Structure = StructureKind.None;
            b.MergeBlock = null;
            b.ContinueBlock = null;
            foreach (var target in oldTargets)
            {
                if (!term.Targets.Contains(target))
                {
                    IrUtil.RemovePhiPred(target, b);
                }
            }

            changed = true;
        }

        // A gutted continue block still branches to its header: keep that phi edge, with an undefined value.
        foreach (var cb in deadContinues)
        {
            var header = loopHeaderOf[cb];
            foreach (var i in header.Instructions)
            {
                if (i.Op != Opcode.Phi)
                {
                    break;
                }

                bool has = false;
                for (int k = 0; k < i.Targets.Count; k++)
                {
                    if (ReferenceEquals(i.Targets[k], cb))
                    {
                        has = true;
                        i.Operands[k] = Constant.Undef(i.Type); // the old value lived in the block we just emptied
                    }
                }

                if (!has)
                {
                    i.Targets.Add(cb);
                    i.Operands.Add(Constant.Undef(i.Type));
                }
            }
        }

        // Drop the blocks that are neither reachable nor named, and the phi entries that came from them.
        var removed = new HashSet<BasicBlock>(ReferenceEqualityComparer.Instance);
        foreach (var b in f.Blocks)
        {
            if (!kept.Contains(b))
            {
                removed.Add(b);
            }
        }

        if (removed.Count > 0)
        {
            foreach (var b in removed)
            {
                if (b.Terminator is { } t)
                {
                    foreach (var target in t.Targets)
                    {
                        if (!removed.Contains(target))
                        {
                            IrUtil.RemovePhiPred(target, b);
                        }
                    }
                }
            }

            f.Blocks.RemoveAll(removed.Contains);
            changed = true;
        }

        return changed;
    }

    private static bool IsMinimalStub(BasicBlock b, Dictionary<BasicBlock, BasicBlock> loopHeaderOf)
    {
        if (b.Instructions.Count != 1 || b.Structure != StructureKind.None)
        {
            return false;
        }

        var t = b.Instructions[0];
        return loopHeaderOf.TryGetValue(b, out var header)
            ? t.Op == Opcode.Branch && ReferenceEquals(t.Targets[0], header)
            : t.Op == Opcode.Unreachable;
    }

    private static bool MergeBlocks(IrFunction f)
    {
        var cfg = new Cfg(f);
        var named = new HashSet<BasicBlock>(ReferenceEqualityComparer.Instance);
        foreach (var b in f.Blocks)
        {
            if (b.Structure != StructureKind.None)
            {
                if (b.MergeBlock is not null)
                {
                    named.Add(b.MergeBlock);
                }

                if (b.ContinueBlock is not null)
                {
                    named.Add(b.ContinueBlock);
                }
            }
        }

        var map = IrUtil.NewMap();
        var removed = new HashSet<BasicBlock>(ReferenceEqualityComparer.Instance);
        foreach (var a in f.Blocks.ToArray())
        {
            if (removed.Contains(a) || a.Structure == StructureKind.Loop)
            {
                continue;
            }

            // A may have absorbed earlier successors; keep folding while the chain continues.
            while (a.Terminator is { Op: Opcode.Branch } br)
            {
                var bb = br.Targets[0];
                int bi = cfg.Index[bb];
                if (ReferenceEquals(a, bb) || bi == 0 || cfg.Preds[bi].Count != 1 || named.Contains(bb) || bb.Structure == StructureKind.Loop || removed.Contains(bb))
                {
                    break;
                }

                int firstNonPhi = 0;
                while (firstNonPhi < bb.Instructions.Count && bb.Instructions[firstNonPhi].Op == Opcode.Phi)
                {
                    var phi = bb.Instructions[firstNonPhi];
                    map[phi] = phi.Operands[0];
                    firstNonPhi++;
                }

                a.Instructions.RemoveAt(a.Instructions.Count - 1);
                for (int k = firstNonPhi; k < bb.Instructions.Count; k++)
                {
                    var moved = bb.Instructions[k];
                    moved.Parent = a;
                    a.Instructions.Add(moved);
                }

                if (bb.Structure != StructureKind.None)
                {
                    a.Structure = bb.Structure;
                    a.MergeBlock = bb.MergeBlock;
                    a.ContinueBlock = bb.ContinueBlock;
                    a.Hint = bb.Hint;
                }

                if (a.Terminator is { } term)
                {
                    foreach (var s in term.Targets)
                    {
                        IrUtil.RenamePhiPred(s, bb, a);
                    }
                }

                removed.Add(bb);
            }
        }

        if (removed.Count == 0)
        {
            return false;
        }

        f.Blocks.RemoveAll(removed.Contains);
        IrUtil.ApplyReplacements(f, map);
        return true;
    }
}
