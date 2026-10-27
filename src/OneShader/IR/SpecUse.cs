using System.Collections.Generic;

namespace OneShader.IR;

/// <summary>Finds which specialization constants a function and everything it calls use.</summary>
internal static class SpecUse
{
    /// <summary>The constants used by <paramref name="root"/> and its callees, in module declaration order (walked with an explicit stack).</summary>
    public static List<SpecConstant> Scan(IrModule module, IrFunction root)
    {
        var used = new HashSet<SpecConstant>(ReferenceEqualityComparer.Instance);
        var seen = new HashSet<IrFunction>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<IrFunction>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var f = pending.Pop();
            if (!seen.Add(f))
            {
                continue;
            }

            foreach (var b in f.Blocks)
            {
                foreach (var i in b.Instructions)
                {
                    foreach (var o in i.Operands)
                    {
                        if (o is SpecConstant sc)
                        {
                            used.Add(sc);
                        }
                    }

                    if (i.Callee is { } callee)
                    {
                        pending.Push(callee);
                    }
                }
            }
        }

        var list = new List<SpecConstant>();
        foreach (var sc in module.SpecConstants)
        {
            if (used.Contains(sc))
            {
                list.Add(sc);
            }
        }

        return list;
    }
}
