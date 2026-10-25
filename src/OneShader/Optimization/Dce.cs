using System.Collections.Generic;
using OneShader.IR;

namespace OneShader.Optimization;

/// <summary>
/// Removes instructions nobody needs. First, local variables that are written but never read lose their
/// stores; then a mark phase keeps everything reachable from side effects and terminators and drops the
/// rest (which also removes dead phi cycles).
/// </summary>
internal static class Dce
{
    public static bool Run(IrFunction f)
    {
        bool changed = RemoveWriteOnlyVariables(f);
        var live = new HashSet<Instruction>(ReferenceEqualityComparer.Instance);
        var work = new Stack<Instruction>();
        foreach (var b in f.Blocks)
        {
            foreach (var i in b.Instructions)
            {
                if (!IrUtil.IsRemovable(i) && live.Add(i))
                {
                    work.Push(i);
                }
            }
        }

        while (work.Count > 0)
        {
            var i = work.Pop();
            foreach (var o in i.Operands)
            {
                if (o is Instruction oi && live.Add(oi))
                {
                    work.Push(oi);
                }
            }
        }

        foreach (var b in f.Blocks)
        {
            int removed = b.Instructions.RemoveAll(i => !live.Contains(i));
            changed |= removed > 0;
        }

        return changed;
    }

    private static bool RemoveWriteOnlyVariables(IrFunction f)
    {
        if (f.Entry is not { } entry)
        {
            return false;
        }

        var users = IrUtil.BuildUsers(f);
        var dead = new HashSet<Instruction>(ReferenceEqualityComparer.Instance);
        foreach (var inst in entry.Instructions)
        {
            if (inst.Op != Opcode.Alloca)
            {
                continue;
            }

            var derived = new List<Instruction>();
            if (OnlyWritten(inst, users, derived, 0))
            {
                foreach (var d in derived)
                {
                    dead.Add(d);
                }
            }
        }

        if (dead.Count == 0)
        {
            return false;
        }

        IrUtil.RemoveInstructions(f, dead);
        return true;
    }

    // True when the pointer is only ever stored through (directly or via access chains); collects those stores and chains.
    private static bool OnlyWritten(Instruction ptr, Dictionary<IrValue, List<Instruction>> users, List<Instruction> derived, int depth)
    {
        if (depth > 16)
        {
            return false;
        }

        if (!users.TryGetValue(ptr, out var list))
        {
            return true;
        }

        var mark = derived.Count;
        foreach (var u in list)
        {
            if (u.Op == Opcode.Store && ReferenceEquals(u.Operands[0], ptr) && !ReferenceEquals(u.Operands[1], ptr))
            {
                derived.Add(u);
            }
            else if (u.Op == Opcode.AccessChain && ReferenceEquals(u.Operands[0], ptr))
            {
                derived.Add(u);
                if (!OnlyWritten(u, users, derived, depth + 1))
                {
                    derived.RemoveRange(mark, derived.Count - mark);
                    return false;
                }
            }
            else
            {
                derived.RemoveRange(mark, derived.Count - mark);
                return false;
            }
        }

        return true;
    }
}
