using System.Collections.Generic;
using OneShader.IR;
using OneShader.Types;

namespace OneShader.Optimization;

/// <summary>
/// Promotes function-local scalars, vectors and matrices from memory to SSA values with phis
/// (dominance-frontier placement, pruned by liveness; renaming walks the dominator tree with an
/// explicit stack). Variables whose address escapes, or that are indexed dynamically, stay in memory.
/// </summary>
internal static class Mem2Reg
{
    private sealed class Var
    {
        public Instruction Alloca = null!;
        public HlslType Type = null!;
        public int Index;
        public readonly List<Instruction> Phis = [];
    }

    /// <summary>How an instruction touches a promoted variable: which variable and, for partial access, the constant element path.</summary>
    private readonly record struct Access(Var Variable, int[] Path);

    public static bool Run(IrFunction f)
    {
        if (f.Entry is not { } entry)
        {
            return false;
        }

        var users = IrUtil.BuildUsers(f);
        var vars = new List<Var>();
        var accessOf = new Dictionary<Instruction, Access>(ReferenceEqualityComparer.Instance);
        foreach (var inst in entry.Instructions)
        {
            if (inst.Op != Opcode.Alloca || inst.Type is not PointerType { Pointee: var pointee } || pointee is not (ScalarType or VectorType or MatrixType))
            {
                continue;
            }

            var v = new Var { Alloca = inst, Type = pointee, Index = vars.Count };
            var found = new List<(Instruction, int[])>();
            if (Collect(inst, [], v, users, found, 0))
            {
                vars.Add(v);
                accessOf[inst] = new Access(v, []);
                foreach (var (node, path) in found)
                {
                    accessOf[node] = new Access(v, path);
                }
            }
        }

        if (vars.Count == 0)
        {
            return false;
        }

        var cfg = new Cfg(f);
        PlacePhis(cfg, vars, accessOf);
        Rename(f, cfg, vars, accessOf);
        return true;
    }

    // Walks the users of a pointer. Returns false when something other than a load, a store to it, or a
    // constant-index access chain uses it.
    private static bool Collect(Instruction ptr, int[] path, Var v, Dictionary<IrValue, List<Instruction>> users, List<(Instruction, int[])> found, int depth)
    {
        if (depth > 8)
        {
            return false;
        }

        if (!users.TryGetValue(ptr, out var list))
        {
            return true;
        }

        foreach (var u in list)
        {
            switch (u.Op)
            {
                case Opcode.Load:
                    break;
                case Opcode.Store:
                    if (!ReferenceEquals(u.Operands[0], ptr) || ReferenceEquals(u.Operands[1], ptr))
                    {
                        return false;
                    }

                    break;
                case Opcode.AccessChain:
                {
                    // Only vector components are addressed partially; matrices stay whole.
                    if (!ReferenceEquals(u.Operands[0], ptr) || ptr.Type is not PointerType { Pointee: VectorType } || u.Operands.Count != 2
                        || u.Operands[1] is not Constant { ConstantKind: ConstantKind.Scalar, Type: ScalarType { ScalarKind: ScalarKind.Int or ScalarKind.UInt } } idx
                        || (int)idx.Bits < 0 || (int)idx.Bits >= ((VectorType)((PointerType)ptr.Type).Pointee).Count)
                    {
                        return false;
                    }

                    var sub = new int[path.Length + 1];
                    path.CopyTo(sub, 0);
                    sub[^1] = (int)idx.Bits;
                    found.Add((u, sub));
                    if (!Collect(u, sub, v, users, found, depth + 1))
                    {
                        return false;
                    }

                    break;
                }

                default:
                    return false;
            }
        }

        return true;
    }

    private sealed class BlockUse
    {
        public bool Killed;
        public bool Exposed;
        public bool HasDef;
    }

    private static void PlacePhis(Cfg cfg, List<Var> vars, Dictionary<Instruction, Access> accessOf)
    {
        var frontier = cfg.Frontier();

        // One sweep over the code: for each variable, what each block it touches does to it.
        // Per variable work stays proportional to the blocks it is live in, not to the function.
        var uses = new Dictionary<int, BlockUse>[vars.Count];
        for (int k = 0; k < uses.Length; k++)
        {
            uses[k] = [];
        }

        for (int b = 0; b < cfg.Blocks.Count; b++)
        {
            if (!cfg.IsReachable(b))
            {
                continue;
            }

            foreach (var i in cfg.Blocks[b].Instructions)
            {
                if (i.Op == Opcode.Load && i.Operands[0] is Instruction lp && accessOf.TryGetValue(lp, out var la))
                {
                    var u = Touch(uses[la.Variable.Index], b);
                    if (!u.Killed)
                    {
                        u.Exposed = true;
                    }
                }
                else if (i.Op == Opcode.Store && i.Operands[0] is Instruction sp && accessOf.TryGetValue(sp, out var sa))
                {
                    var u = Touch(uses[sa.Variable.Index], b);
                    u.HasDef = true;
                    if (sa.Path.Length == 0)
                    {
                        u.Killed = true;
                    }
                    else if (!u.Killed)
                    {
                        u.Exposed = true; // a partial write needs the previous value
                    }
                }
            }
        }

        foreach (var v in vars)
        {
            var info = uses[v.Index];
            var defBlocks = new List<int>();
            var work = new Stack<int>();
            var liveIn = new HashSet<int>();
            foreach (var (b, u) in info)
            {
                if (u.HasDef)
                {
                    defBlocks.Add(b);
                }

                if (u.Exposed && liveIn.Add(b))
                {
                    work.Push(b);
                }
            }

            defBlocks.Sort();

            // Live-in blocks: exposed reads, and blocks that do not kill and lead to a live-in block.
            while (work.Count > 0)
            {
                int b = work.Pop();
                foreach (int p in cfg.Preds[b])
                {
                    if (cfg.IsReachable(p) && !(info.TryGetValue(p, out var pu) && pu.Killed) && liveIn.Add(p))
                    {
                        work.Push(p);
                    }
                }
            }

            var hasPhi = new HashSet<int>();
            var queue = new Stack<int>(defBlocks);
            var queued = new HashSet<int>(defBlocks);
            while (queue.Count > 0)
            {
                int b = queue.Pop();
                foreach (int y in frontier[b])
                {
                    if (!hasPhi.Add(y))
                    {
                        continue;
                    }

                    if (liveIn.Contains(y))
                    {
                        var phi = IrUtil.Make(cfg.Blocks[y], Opcode.Phi, v.Type);
                        phi.Name = v.Alloca.Name;
                        v.Phis.Add(phi);
                        // Insert after existing phis; variables go in alloca order, so the result is deterministic.
                        var insts = cfg.Blocks[y].Instructions;
                        int at = 0;
                        while (at < insts.Count && insts[at].Op == Opcode.Phi)
                        {
                            at++;
                        }

                        insts.Insert(at, phi);
                    }

                    if (queued.Add(y))
                    {
                        queue.Push(y);
                    }
                }
            }
        }
    }

    private static BlockUse Touch(Dictionary<int, BlockUse> map, int block)
    {
        if (!map.TryGetValue(block, out var u))
        {
            u = new BlockUse();
            map[block] = u;
        }

        return u;
    }

    private static void Rename(IrFunction f, Cfg cfg, List<Var> vars, Dictionary<Instruction, Access> accessOf)
    {
        int n = cfg.Blocks.Count;
        var map = IrUtil.NewMap();
        var dead = new HashSet<Instruction>(ReferenceEqualityComparer.Instance);
        var current = new IrValue[vars.Count];
        for (int k = 0; k < vars.Count; k++)
        {
            current[k] = Constant.Undef(vars[k].Type);
        }

        // Which phi (if any) belongs to which variable, by instruction.
        var phiVar = new Dictionary<Instruction, Var>(ReferenceEqualityComparer.Instance);
        foreach (var v in vars)
        {
            foreach (var p in v.Phis)
            {
                phiVar[p] = v;
            }
        }

        var incoming = new Dictionary<Instruction, Dictionary<int, IrValue>>(ReferenceEqualityComparer.Instance);
        var newInstructions = new List<Instruction>[n];

        // Explicit-stack walk of the dominator tree. Each frame remembers the variable values to restore on exit.
        var stack = new Stack<(int Block, int NextChild, List<(int Var, IrValue Old)> Undo)>();
        stack.Push((0, 0, ProcessBlock(0)));
        while (stack.Count > 0)
        {
            var (b, next, undo) = stack.Pop();
            if (next < cfg.DomChildren[b].Count)
            {
                stack.Push((b, next + 1, undo));
                int child = cfg.DomChildren[b][next];
                stack.Push((child, 0, ProcessBlock(child)));
            }
            else
            {
                for (int k = undo.Count - 1; k >= 0; k--)
                {
                    current[undo[k].Var] = undo[k].Old;
                }
            }
        }

        List<(int Var, IrValue Old)> ProcessBlock(int b)
        {
            var undo = new List<(int, IrValue)>();
            var block = cfg.Blocks[b];
            var output = new List<Instruction>(block.Instructions.Count);
            foreach (var i in block.Instructions)
            {
                if (i.Op == Opcode.Phi && phiVar.TryGetValue(i, out var pv))
                {
                    undo.Add((pv.Index, current[pv.Index]));
                    current[pv.Index] = i;
                    output.Add(i);
                    continue;
                }

                if (i.Op == Opcode.Alloca && accessOf.ContainsKey(i))
                {
                    dead.Add(i);
                    continue;
                }

                if (i.Op == Opcode.AccessChain && accessOf.ContainsKey(i))
                {
                    dead.Add(i);
                    continue;
                }

                if (i.Op == Opcode.Load && i.Operands[0] is Instruction lp && accessOf.TryGetValue(lp, out var la))
                {
                    var cur = IrUtil.Resolve(map, current[la.Variable.Index]);
                    if (la.Path.Length == 0)
                    {
                        map[i] = cur;
                    }
                    else
                    {
                        var ex = IrUtil.Make(block, Opcode.CompositeExtract, i.Type, cur);
                        ex.Immediates.AddRange(la.Path);
                        ex.Span = i.Span;
                        ex.InlinedAt = i.InlinedAt;
                        ex.Name = i.Name;
                        output.Add(ex);
                        map[i] = ex;
                    }

                    dead.Add(i);
                    continue;
                }

                if (i.Op == Opcode.Store && i.Operands[0] is Instruction sp && accessOf.TryGetValue(sp, out var sa))
                {
                    int vi = sa.Variable.Index;
                    undo.Add((vi, current[vi]));
                    var value = IrUtil.Resolve(map, i.Operands[1]);
                    if (sa.Path.Length == 0)
                    {
                        current[vi] = value;
                    }
                    else
                    {
                        var ins = IrUtil.Make(block, Opcode.CompositeInsert, sa.Variable.Type, IrUtil.Resolve(map, current[vi]), value);
                        ins.Immediates.AddRange(sa.Path);
                        ins.Span = i.Span;
                        ins.InlinedAt = i.InlinedAt;
                        output.Add(ins);
                        current[vi] = ins;
                    }

                    dead.Add(i);
                    continue;
                }

                output.Add(i);
            }

            newInstructions[b] = output;

            // Feed our successors' phis with the values live at the end of this block.
            foreach (int s in cfg.Succs[b])
            {
                foreach (var inst in cfg.Blocks[s].Instructions)
                {
                    if (inst.Op != Opcode.Phi)
                    {
                        break;
                    }

                    if (phiVar.TryGetValue(inst, out var v))
                    {
                        if (!incoming.TryGetValue(inst, out var byPred))
                        {
                            byPred = [];
                            incoming[inst] = byPred;
                        }

                        byPred[b] = current[v.Index];
                    }
                }
            }

            return undo;
        }

        // Unreachable blocks: loads of promoted variables read nothing; stores vanish.
        for (int b = 0; b < n; b++)
        {
            if (cfg.IsReachable(b))
            {
                continue;
            }

            foreach (var i in cfg.Blocks[b].Instructions)
            {
                if (i.Op == Opcode.Load && i.Operands[0] is Instruction lp && accessOf.ContainsKey(lp))
                {
                    map[i] = Constant.Undef(i.Type);
                    dead.Add(i);
                }
                else if ((i.Op == Opcode.Store && i.Operands[0] is Instruction sp && accessOf.ContainsKey(sp))
                    || (i.Op is Opcode.Alloca or Opcode.AccessChain && accessOf.ContainsKey(i)))
                {
                    dead.Add(i);
                }
            }
        }

        // Install the rewritten instruction lists, then complete the phis in predecessor order.
        for (int b = 0; b < n; b++)
        {
            if (newInstructions[b] is { } list)
            {
                cfg.Blocks[b].Instructions.Clear();
                cfg.Blocks[b].Instructions.AddRange(list);
            }
        }

        foreach (var v in vars)
        {
            foreach (var phi in v.Phis)
            {
                int blockIndex = cfg.Index[phi.Parent!];
                incoming.TryGetValue(phi, out var byPred);
                foreach (int p in cfg.Preds[blockIndex])
                {
                    phi.Targets.Add(cfg.Blocks[p]);
                    phi.Operands.Add(byPred is not null && byPred.TryGetValue(p, out var val) ? val : Constant.Undef(v.Type));
                }
            }
        }

        IrUtil.RemoveInstructions(f, dead);
        IrUtil.ApplyReplacements(f, map);
    }
}
