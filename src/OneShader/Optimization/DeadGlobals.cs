using System.Collections.Generic;
using OneShader.IR;

namespace OneShader.Optimization;

/// <summary>
/// Removes <c>static</c> and <c>groupshared</c> variables that are never read, together with the stores into them.
/// Resources and stage inputs and outputs are the shader's interface and always stay.
/// </summary>
internal static class DeadGlobals
{
    public static bool Run(IrModule module)
    {
        var candidates = new HashSet<GlobalVariable>(ReferenceEqualityComparer.Instance);
        foreach (var g in module.Globals)
        {
            if (g.GlobalKind is GlobalKind.Private or GlobalKind.Workgroup)
            {
                candidates.Add(g);
            }
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        // A candidate stays when anything but a store (directly or through access chains) mentions it.
        var removable = new Dictionary<GlobalVariable, List<(IrFunction Function, List<Instruction> Writes)>>(ReferenceEqualityComparer.Instance);
        var live = new HashSet<GlobalVariable>(ReferenceEqualityComparer.Instance);
        foreach (var f in module.Functions)
        {
            var users = IrUtil.BuildUsers(f);
            var perGlobal = new Dictionary<GlobalVariable, List<Instruction>>(ReferenceEqualityComparer.Instance);
            foreach (var b in f.Blocks)
            {
                foreach (var i in b.Instructions)
                {
                    foreach (var o in i.Operands)
                    {
                        if (o is not GlobalVariable g || !candidates.Contains(g) || live.Contains(g))
                        {
                            continue;
                        }

                        var writes = new List<Instruction>();
                        if (i.Op == Opcode.Store && ReferenceEquals(i.Operands[0], g) && !ReferenceEquals(i.Operands[1], g))
                        {
                            writes.Add(i);
                        }
                        else if (i.Op == Opcode.AccessChain && ReferenceEquals(i.Operands[0], g) && Dce.OnlyWritten(i, users, writes, 0))
                        {
                            writes.Add(i);
                        }
                        else
                        {
                            live.Add(g);
                            continue;
                        }

                        if (!perGlobal.TryGetValue(g, out var list))
                        {
                            list = [];
                            perGlobal[g] = list;
                        }

                        list.AddRange(writes);
                    }
                }
            }

            foreach (var (g, writes) in perGlobal)
            {
                if (!removable.TryGetValue(g, out var l))
                {
                    l = [];
                    removable[g] = l;
                }

                l.Add((f, writes));
            }
        }

        bool changed = false;
        var gone = new HashSet<GlobalVariable>(ReferenceEqualityComparer.Instance);
        foreach (var g in module.Globals)
        {
            if (!candidates.Contains(g) || live.Contains(g))
            {
                continue;
            }

            if (removable.TryGetValue(g, out var parts))
            {
                foreach (var (f, writes) in parts)
                {
                    IrUtil.RemoveInstructions(f, new HashSet<Instruction>(writes, ReferenceEqualityComparer.Instance));
                }
            }

            gone.Add(g);
            changed = true;
        }

        module.Globals.RemoveAll(gone.Contains);
        return changed;
    }
}
