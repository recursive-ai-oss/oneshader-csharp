using System.Collections.Generic;
using OneShader.IR;
using OneShader.Types;

namespace OneShader.Optimization;

/// <summary>Small helpers shared by the passes.</summary>
internal static class IrUtil
{
    public static Dictionary<IrValue, IrValue> NewMap() => new(ReferenceEqualityComparer.Instance);

    /// <summary>Follows a replacement chain to its end; bounded so a bad map cannot loop forever.</summary>
    public static IrValue Resolve(Dictionary<IrValue, IrValue> map, IrValue v)
    {
        for (int guard = 0; guard < 1_000_000 && map.TryGetValue(v, out var next) && !ReferenceEquals(next, v); guard++)
        {
            v = next;
        }

        return v;
    }

    /// <summary>Rewrites every operand of the function through <paramref name="map"/> in one linear sweep.</summary>
    public static void ApplyReplacements(IrFunction f, Dictionary<IrValue, IrValue> map)
    {
        if (map.Count == 0)
        {
            return;
        }

        foreach (var b in f.Blocks)
        {
            foreach (var i in b.Instructions)
            {
                var ops = i.Operands;
                for (int k = 0; k < ops.Count; k++)
                {
                    ops[k] = Resolve(map, ops[k]);
                }
            }
        }
    }

    /// <summary>Removes the instructions in <paramref name="dead"/> from their blocks.</summary>
    public static void RemoveInstructions(IrFunction f, HashSet<Instruction> dead)
    {
        if (dead.Count == 0)
        {
            return;
        }

        foreach (var b in f.Blocks)
        {
            b.Instructions.RemoveAll(dead.Contains);
        }
    }

    /// <summary>Instructions that can be dropped when nothing uses their result.</summary>
    public static bool IsRemovable(Instruction i) => !i.IsTerminator && !i.HasSideEffects;

    /// <summary>Creates a detached instruction owned by <paramref name="block"/>.</summary>
    public static Instruction Make(BasicBlock block, Opcode op, HlslType type, params IrValue[] operands)
    {
        var i = new Instruction(op, type, new List<IrValue>(operands)) { Parent = block };
        return i;
    }

    /// <summary>Index of the first instruction in the entry block that is not an alloca.</summary>
    public static int AllocaEnd(BasicBlock entry)
    {
        int at = 0;
        while (at < entry.Instructions.Count && entry.Instructions[at].Op == Opcode.Alloca)
        {
            at++;
        }

        return at;
    }

    /// <summary>Replaces <paramref name="oldPred"/> by <paramref name="newPred"/> in the phis of <paramref name="block"/>.</summary>
    public static void RenamePhiPred(BasicBlock block, BasicBlock oldPred, BasicBlock newPred)
    {
        foreach (var i in block.Instructions)
        {
            if (i.Op != Opcode.Phi)
            {
                break;
            }

            for (int k = 0; k < i.Targets.Count; k++)
            {
                if (ReferenceEquals(i.Targets[k], oldPred))
                {
                    i.Targets[k] = newPred;
                }
            }
        }
    }

    /// <summary>Drops the entries for <paramref name="pred"/> from the phis of <paramref name="block"/>.</summary>
    public static void RemovePhiPred(BasicBlock block, BasicBlock pred)
    {
        foreach (var i in block.Instructions)
        {
            if (i.Op != Opcode.Phi)
            {
                break;
            }

            for (int k = i.Targets.Count - 1; k >= 0; k--)
            {
                if (ReferenceEquals(i.Targets[k], pred))
                {
                    i.Targets.RemoveAt(k);
                    i.Operands.RemoveAt(k);
                }
            }
        }
    }

    /// <summary>Counts the instructions of a function.</summary>
    public static int Count(IrFunction f)
    {
        int n = 0;
        foreach (var b in f.Blocks)
        {
            n += b.Instructions.Count;
        }

        return n;
    }

    /// <summary>Users of every instruction in the function, in program order.</summary>
    public static Dictionary<IrValue, List<Instruction>> BuildUsers(IrFunction f)
    {
        var users = new Dictionary<IrValue, List<Instruction>>(ReferenceEqualityComparer.Instance);
        foreach (var b in f.Blocks)
        {
            foreach (var i in b.Instructions)
            {
                foreach (var o in i.Operands)
                {
                    if (o is Instruction)
                    {
                        if (!users.TryGetValue(o, out var list))
                        {
                            list = [];
                            users[o] = list;
                        }

                        list.Add(i);
                    }
                }
            }
        }

        return users;
    }
}
