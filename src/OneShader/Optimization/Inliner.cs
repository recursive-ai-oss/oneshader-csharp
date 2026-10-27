using System.Collections.Generic;
using OneShader.IR;
using OneShader.Types;

namespace OneShader.Optimization;

/// <summary>
/// Inlines every call (GPU code wants flat functions, and the backends need resource pointers to be
/// visible at the point of use). Callees are copied bottom-up so each body is already call-free.
/// A callee whose only return sits at the top level of its structure is spliced in directly. Any
/// other callee is wrapped in a loop that runs once: a return becomes a store of the result plus a break,
/// and a return from inside an inner loop or switch sets a flag that the construct's merge block checks,
/// so every branch stays a legal SPIR-V break.
/// </summary>
internal static class Inliner
{
    // Untrusted input can nest calls so that full inlining is exponential; stop growing a function (or the module) past these sizes.
    private const int MaxFunctionSize = 100_000;
    private const int MaxTotalGrowth = 400_000;

    /// <summary>Inlines all calls reachable from the entry points. Returns true when anything changed.</summary>
    public static bool Run(IrModule m)
    {
        var order = new List<IrFunction>();
        var recursive = new HashSet<IrFunction>(ReferenceEqualityComparer.Instance);
        var state = new Dictionary<IrFunction, int>(ReferenceEqualityComparer.Instance);
        foreach (var ep in m.EntryPoints)
        {
            Visit(ep.Function, order, state, recursive);
        }

        bool any = false;
        int budget = MaxTotalGrowth;
        var sizes = new Dictionary<IrFunction, int>(ReferenceEqualityComparer.Instance);
        foreach (var f in order)
        {
            // Callees were already flattened, so the calls present now are the only ones; the clones add none.
            var calls = new List<Instruction>();
            foreach (var b in f.Blocks)
            {
                foreach (var i in b.Instructions)
                {
                    if (i.Op == Opcode.Call && i.Callee is { } c && !recursive.Contains(c) && !ReferenceEquals(c, f) && c.Blocks.Count > 0)
                    {
                        calls.Add(i);
                    }
                }
            }

            if (calls.Count == 0)
            {
                continue;
            }

            int size = IrUtil.Count(f);
            var map = IrUtil.NewMap();
            foreach (var call in calls)
            {
                var callee = call.Callee!;
                if (!sizes.TryGetValue(callee, out int calleeSize))
                {
                    calleeSize = IrUtil.Count(callee);
                    sizes[callee] = calleeSize;
                }

                if (budget <= 0 || size + calleeSize > MaxFunctionSize)
                {
                    break;
                }

                var block = call.Parent!;
                if (InlineCall(f, block, block.Instructions.IndexOf(call), map))
                {
                    any = true;
                    size += calleeSize;
                    budget -= calleeSize;
                }
            }

            IrUtil.ApplyReplacements(f, map);
            sizes.Remove(f);
        }

        return any;
    }

    /// <summary>Removes functions no entry point reaches.</summary>
    public static bool RemoveUnreachable(IrModule m)
    {
        var live = new HashSet<IrFunction>(ReferenceEqualityComparer.Instance);
        var work = new Stack<IrFunction>();
        foreach (var ep in m.EntryPoints)
        {
            if (live.Add(ep.Function))
            {
                work.Push(ep.Function);
            }
        }

        while (work.Count > 0)
        {
            var f = work.Pop();
            foreach (var b in f.Blocks)
            {
                foreach (var i in b.Instructions)
                {
                    if (i.Callee is { } c && live.Add(c))
                    {
                        work.Push(c);
                    }
                }
            }
        }

        return m.Functions.RemoveAll(f => !live.Contains(f)) > 0;
    }

    private static void Visit(IrFunction root, List<IrFunction> order, Dictionary<IrFunction, int> state, HashSet<IrFunction> recursive)
    {
        if (state.ContainsKey(root))
        {
            return;
        }

        var stack = new Stack<(IrFunction Function, List<IrFunction> Callees, int Next)>();
        state[root] = 1;
        stack.Push((root, Callees(root), 0));
        while (stack.Count > 0)
        {
            var (f, callees, next) = stack.Pop();
            if (next < callees.Count)
            {
                stack.Push((f, callees, next + 1));
                var c = callees[next];
                if (!state.TryGetValue(c, out int s))
                {
                    state[c] = 1;
                    stack.Push((c, Callees(c), 0));
                }
                else if (s == 1)
                {
                    recursive.Add(c); // a call cycle; HLSL forbids recursion, but input is untrusted
                }
            }
            else
            {
                state[f] = 2;
                order.Add(f);
            }
        }
    }

    private static List<IrFunction> Callees(IrFunction f)
    {
        var list = new List<IrFunction>();
        foreach (var b in f.Blocks)
        {
            foreach (var i in b.Instructions)
            {
                if (i.Callee is { } c && !list.Contains(c))
                {
                    list.Add(c);
                }
            }
        }

        return list;
    }

    // ---- structure analysis of the callee ----------------------------------------------------------

    private sealed class Regions
    {
        public readonly Dictionary<BasicBlock, HashSet<BasicBlock>> ByHeader = new(ReferenceEqualityComparer.Instance);
    }

    private static Regions ComputeRegions(IrFunction callee)
    {
        var r = new Regions();
        var markedBy = new Dictionary<BasicBlock, List<BasicBlock>>(ReferenceEqualityComparer.Instance);
        foreach (var b in callee.Blocks)
        {
            if (b.Structure == StructureKind.None)
            {
                continue;
            }

            foreach (var named in (BasicBlock?[])[b.MergeBlock, b.ContinueBlock])
            {
                if (named is null)
                {
                    continue;
                }

                if (!markedBy.TryGetValue(named, out var owners))
                {
                    owners = [];
                    markedBy[named] = owners;
                }

                owners.Add(b);
            }
        }

        foreach (var h in callee.Blocks)
        {
            if (h.Structure == StructureKind.None)
            {
                continue;
            }

            var visited = new HashSet<BasicBlock>(ReferenceEqualityComparer.Instance) { h };
            var stack = new Stack<BasicBlock>();
            stack.Push(h);
            while (stack.Count > 0)
            {
                var b = stack.Pop();
                if (b.Terminator is not { } t)
                {
                    continue;
                }

                foreach (var s in t.Targets)
                {
                    if (visited.Contains(s) || ReferenceEquals(s, h.MergeBlock))
                    {
                        continue;
                    }

                    if (markedBy.TryGetValue(s, out var owners))
                    {
                        bool ownerInside = false;
                        foreach (var o in owners)
                        {
                            ownerInside |= visited.Contains(o);
                        }

                        if (!ownerInside)
                        {
                            continue; // a break or continue of an enclosing construct
                        }
                    }

                    visited.Add(s);
                    stack.Push(s);
                }
            }

            r.ByHeader[h] = visited;
        }

        return r;
    }

    private static bool Breakable(BasicBlock h) =>
        h.Structure == StructureKind.Loop || (h.Structure == StructureKind.Selection && h.Terminator is { Op: Opcode.Switch });

    // Breakable constructs whose region holds the block, innermost first.
    private static List<BasicBlock> Chain(BasicBlock block, Regions regions, IrFunction callee)
    {
        var list = new List<BasicBlock>();
        foreach (var h in callee.Blocks)
        {
            if (Breakable(h) && regions.ByHeader.TryGetValue(h, out var set) && set.Contains(block) && !ReferenceEquals(h, block))
            {
                list.Add(h);
            }
        }

        list.Sort((a, b) => regions.ByHeader[a].Count.CompareTo(regions.ByHeader[b].Count));
        return list;
    }

    private static bool InAnyRegion(BasicBlock block, Regions regions)
    {
        foreach (var (h, set) in regions.ByHeader)
        {
            if (!ReferenceEquals(h, block) && set.Contains(block))
            {
                return true;
            }
        }

        return false;
    }

    // ---- the inlining itself ----------------------------------------------------------------------

    private static bool InlineCall(IrFunction caller, BasicBlock block, int index, Dictionary<IrValue, IrValue> map)
    {
        var call = block.Instructions[index];
        var callee = call.Callee!;
        var regions = ComputeRegions(callee);

        var returns = new List<BasicBlock>();
        foreach (var b in callee.Blocks)
        {
            if (b.Terminator is { Op: Opcode.Return })
            {
                returns.Add(b);
            }
        }

        bool simple = returns.Count == 0 || (returns.Count == 1 && !InAnyRegion(returns[0], regions));

        // For the general case work out the breaks and flag checks before touching anything.
        var chains = new Dictionary<BasicBlock, List<BasicBlock>>(ReferenceEqualityComparer.Instance);
        var needCheck = new List<BasicBlock>();
        if (!simple)
        {
            foreach (var rb in returns)
            {
                var chain = Chain(rb, regions, callee);
                chains[rb] = chain;
                foreach (var q in chain)
                {
                    if (!needCheck.Contains(q))
                    {
                        needCheck.Add(q);
                    }
                }
            }

            foreach (var q in needCheck)
            {
                // Adding break edges into a merge block that has phis would need new phi entries; give up on those.
                if (q.MergeBlock is not { } mb || (mb.Instructions.Count > 0 && mb.Instructions[0].Op == Opcode.Phi))
                {
                    return false;
                }
            }
        }

        var entry = caller.Entry!;
        var clones = new List<BasicBlock>(callee.Blocks.Count);
        var blockMap = new Dictionary<BasicBlock, BasicBlock>(ReferenceEqualityComparer.Instance);
        foreach (var b in callee.Blocks)
        {
            var nb = new BasicBlock(caller, (callee.Name + "." + b.Name).TrimEnd('.'));
            blockMap[b] = nb;
            clones.Add(nb);
        }

        var valueMap = IrUtil.NewMap();
        for (int k = 0; k < callee.Parameters.Count; k++)
        {
            valueMap[callee.Parameters[k]] = IrUtil.Resolve(map, call.Operands[k]);
        }

        var hoisted = new List<Instruction>();
        var cloneList = new List<Instruction>();
        var callSite = new InlineSite(call.Span, call.InlinedAt);
        var rebased = new Dictionary<InlineSite, InlineSite>(ReferenceEqualityComparer.Instance);
        for (int bi = 0; bi < callee.Blocks.Count; bi++)
        {
            var ob = callee.Blocks[bi];
            var nb = clones[bi];
            foreach (var i in ob.Instructions)
            {
                var c = new Instruction(i.Op, i.Type, new List<IrValue>(i.Operands))
                {
                    Callee = i.Callee,
                    Intrinsic = i.Intrinsic,
                    Precise = i.Precise,
                    Span = i.Span,
                    Name = i.Name,
                    DebugVariable = i.DebugVariable,
                    InlinedAt = Rebase(i.InlinedAt, callSite, rebased),
                };
                c.Immediates.AddRange(i.Immediates);
                foreach (var t in i.Targets)
                {
                    c.Targets.Add(blockMap[t]);
                }

                valueMap[i] = c;
                cloneList.Add(c);
                if (i.Op == Opcode.Alloca)
                {
                    c.Parent = entry;
                    hoisted.Add(c);
                }
                else
                {
                    c.Parent = nb;
                    nb.Instructions.Add(c);
                }
            }

            nb.Structure = ob.Structure;
            nb.Hint = ob.Hint;
            nb.MergeBlock = ob.MergeBlock is null ? null : blockMap[ob.MergeBlock];
            nb.ContinueBlock = ob.ContinueBlock is null ? null : blockMap[ob.ContinueBlock];
        }

        foreach (var c in cloneList)
        {
            for (int k = 0; k < c.Operands.Count; k++)
            {
                if (valueMap.TryGetValue(c.Operands[k], out var mapped))
                {
                    c.Operands[k] = mapped;
                }
            }
        }

        // Split the caller's block at the call.
        var head = block;
        var cont = new BasicBlock(caller, head.Name is null ? "inline.cont" : head.Name + ".cont");
        for (int k = index + 1; k < head.Instructions.Count; k++)
        {
            var moved = head.Instructions[k];
            moved.Parent = cont;
            cont.Instructions.Add(moved);
        }

        head.Instructions.RemoveRange(index, head.Instructions.Count - index);
        if (head.Structure == StructureKind.Selection)
        {
            cont.Structure = head.Structure;
            cont.MergeBlock = head.MergeBlock;
            cont.Hint = head.Hint;
            head.Structure = StructureKind.None;
            head.MergeBlock = null;
            head.Hint = ControlHint.None;
        }

        if (cont.Terminator is { } contTerm)
        {
            foreach (var s in contTerm.Targets)
            {
                IrUtil.RenamePhiPred(s, head, cont);
            }
        }

        var sequence = new List<BasicBlock>();
        IrValue? result = null;
        if (simple)
        {
            if (returns.Count == 1)
            {
                var rc = blockMap[returns[0]];
                var ret = rc.Instructions[^1];
                result = ret.Operands.Count > 0 ? ret.Operands[0] : null;
                ret.Op = Opcode.Branch;
                ret.Operands.Clear();
                ret.Targets.Add(cont);
            }

            head.Instructions.Add(Branch(head, clones[0]));
            sequence.AddRange(clones);
        }
        else
        {
            bool hasValue = !callee.ReturnType.IsVoid;
            Instruction? retVar = null;
            Instruction? flag = null;
            if (hasValue)
            {
                retVar = IrUtil.Make(entry, Opcode.Alloca, new PointerType(AddressSpace.Function, callee.ReturnType));
                retVar.Name = "retval";
                hoisted.Add(retVar);
            }

            if (needCheck.Count > 0)
            {
                flag = IrUtil.Make(entry, Opcode.Alloca, new PointerType(AddressSpace.Function, ScalarType.Bool));
                flag.Name = "returned";
                hoisted.Add(flag);
                head.Instructions.Add(IrUtil.Make(head, Opcode.Store, HlslType.Void, flag, Constant.Bool(false)));
            }

            var loop = new BasicBlock(caller, "inline.loop");
            var loopContinue = new BasicBlock(caller, "inline.continue");
            loop.Instructions.Add(Branch(loop, clones[0]));
            loop.Structure = StructureKind.Loop;
            loop.MergeBlock = cont;
            loop.ContinueBlock = loopContinue;
            loopContinue.Instructions.Add(Branch(loopContinue, loop));
            head.Instructions.Add(Branch(head, loop));

            foreach (var rb in returns)
            {
                var rc = blockMap[rb];
                var ret = rc.Instructions[^1];
                rc.Instructions.RemoveAt(rc.Instructions.Count - 1);
                if (retVar is not null && ret.Operands.Count > 0)
                {
                    rc.Instructions.Add(IrUtil.Make(rc, Opcode.Store, HlslType.Void, retVar, ret.Operands[0]));
                }

                var chain = chains[rb];
                if (chain.Count == 0)
                {
                    rc.Instructions.Add(Branch(rc, cont));
                }
                else
                {
                    rc.Instructions.Add(IrUtil.Make(rc, Opcode.Store, HlslType.Void, flag!, Constant.Bool(true)));
                    rc.Instructions.Add(Branch(rc, blockMap[chain[0].MergeBlock!]));
                }
            }

            sequence.Add(loop);
            var extra = new Dictionary<BasicBlock, List<BasicBlock>>(ReferenceEqualityComparer.Instance);
            foreach (var q in needCheck)
            {
                var m = blockMap[q.MergeBlock!];
                var rest = new BasicBlock(caller, "inline.rest");
                var exit = new BasicBlock(caller, "inline.exit");
                foreach (var i in m.Instructions)
                {
                    i.Parent = rest;
                    rest.Instructions.Add(i);
                }

                m.Instructions.Clear();
                rest.Structure = m.Structure;
                rest.MergeBlock = m.MergeBlock;
                rest.ContinueBlock = m.ContinueBlock;
                rest.Hint = m.Hint;
                m.Structure = StructureKind.None;
                m.MergeBlock = null;
                m.ContinueBlock = null;
                m.Hint = ControlHint.None;
                if (rest.Terminator is { } rt)
                {
                    foreach (var s in rt.Targets)
                    {
                        IrUtil.RenamePhiPred(s, m, rest);
                    }
                }

                // The next breakable construct outward: the smallest region that contains this construct's header.
                BasicBlock? parent = null;
                foreach (var h in callee.Blocks)
                {
                    if (Breakable(h) && !ReferenceEquals(h, q) && regions.ByHeader[h].Contains(q)
                        && (parent is null || regions.ByHeader[h].Count < regions.ByHeader[parent].Count))
                    {
                        parent = h;
                    }
                }

                var outward = parent is null ? cont : blockMap[parent.MergeBlock!];
                exit.Instructions.Add(Branch(exit, outward));
                var load = IrUtil.Make(m, Opcode.Load, ScalarType.Bool, flag!);
                m.Instructions.Add(load);
                var branch = IrUtil.Make(m, Opcode.CondBranch, HlslType.Void, load);
                branch.Targets.Add(exit);
                branch.Targets.Add(rest);
                m.Instructions.Add(branch);
                m.Structure = StructureKind.Selection;
                m.MergeBlock = rest;
                extra[m] = [exit, rest];
            }

            foreach (var nb in clones)
            {
                sequence.Add(nb);
                if (extra.TryGetValue(nb, out var more))
                {
                    sequence.AddRange(more);
                }
            }

            sequence.Add(loopContinue);
            if (retVar is not null)
            {
                var load = IrUtil.Make(cont, Opcode.Load, callee.ReturnType, retVar);
                cont.Instructions.Insert(0, load);
                result = load;
            }
        }

        sequence.Add(cont);
        int at = caller.Blocks.IndexOf(head) + 1;
        caller.Blocks.InsertRange(at, sequence);
        entry.Instructions.InsertRange(IrUtil.AllocaEnd(entry), hoisted);

        if (!call.Type.IsVoid)
        {
            map[call] = result ?? Constant.Undef(call.Type);
        }

        return true;
    }

    private static Instruction Branch(BasicBlock from, BasicBlock to)
    {
        var br = IrUtil.Make(from, Opcode.Branch, HlslType.Void);
        br.Targets.Add(to);
        return br;
    }

    /// <summary>
    /// Debug info: the inline chain of a callee instruction once the callee itself is inlined at <paramref name="callSite"/>.
    /// The outermost site of the old chain continues into the new one. Walks iteratively, so a deep chain cannot overflow the stack.
    /// </summary>
    private static InlineSite Rebase(InlineSite? old, InlineSite callSite, Dictionary<InlineSite, InlineSite> made)
    {
        if (old is null)
        {
            return callSite;
        }

        var chain = new List<InlineSite>();
        for (var s = old; s is not null; s = s.Outer)
        {
            if (made.TryGetValue(s, out var done))
            {
                var result = done;
                for (int k = chain.Count - 1; k >= 0; k--)
                {
                    result = Link(chain[k], result, made);
                }

                return result;
            }

            chain.Add(s);
        }

        var tail = callSite;
        for (int k = chain.Count - 1; k >= 0; k--)
        {
            tail = Link(chain[k], tail, made);
        }

        return tail;
    }

    private static InlineSite Link(InlineSite old, InlineSite outer, Dictionary<InlineSite, InlineSite> made)
    {
        var n = new InlineSite(old.Call, outer);
        made[old] = n;
        return n;
    }
}
