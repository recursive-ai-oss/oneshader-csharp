using System.Collections.Generic;
using OneShader.Diagnostics;
using OneShader.Reflection;

namespace OneShader.IR;

/// <summary>
/// Finds what an entry point needs from the device because of unbounded resource arrays, descriptor heaps and non-uniform indexing,
/// and where the first use of each is, so a backend that cannot express them can name the line.
/// </summary>
internal sealed class BindlessUse
{
    /// <summary>The device features needed.</summary>
    public DeviceFeatures Features { get; private set; }

    /// <summary>The heaps read.</summary>
    public DescriptorHeaps Heaps { get; private set; }

    /// <summary>The first access to a resource array whose size is not fixed (unbounded array or heap), or unknown.</summary>
    public SourceSpan UnboundedSite { get; private set; }

    /// <summary>The first access to a descriptor heap, or unknown.</summary>
    public SourceSpan HeapSite { get; private set; }

    /// <summary>Scans the entry point's function and everything it calls.</summary>
    public static BindlessUse Scan(EntryPoint entry)
    {
        var use = new BindlessUse();
        var seen = new HashSet<IrFunction>();
        var pending = new Stack<IrFunction>();
        pending.Push(entry.Function);
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
                        if (o is GlobalVariable { GlobalKind: GlobalKind.Resource } g)
                        {
                            use.Note(g, i);
                        }
                    }

                    if (i.Op == Opcode.AccessChain && i.Operands.Count > 1 && i.Operands[0] is GlobalVariable { GlobalKind: GlobalKind.Resource, ResourceCount: not 1 } && IsNonUniform(i.Operands[1]))
                    {
                        use.Features |= DeviceFeatures.NonUniformIndexing;
                    }

                    if (i.Callee is { } callee)
                    {
                        pending.Push(callee);
                    }
                }
            }
        }

        return use;
    }

    private void Note(GlobalVariable g, Instruction at)
    {
        if (g.ResourceCount == 0)
        {
            Features |= DeviceFeatures.RuntimeDescriptorArray;
            if (!UnboundedSite.IsKnown)
            {
                UnboundedSite = at.Span;
            }
        }

        if (g.DescriptorHeap != DescriptorHeaps.None)
        {
            Heaps |= g.DescriptorHeap;
            if (!HeapSite.IsKnown)
            {
                HeapSite = at.Span;
            }
        }
    }

    /// <summary>True when the index went through <c>NonUniformResourceIndex</c>, possibly via a sign conversion.</summary>
    internal static bool IsNonUniform(IrValue v)
    {
        while (v is Instruction { Op: Opcode.Bitcast or Opcode.Convert } c && c.Operands.Count > 0)
        {
            v = c.Operands[0];
        }

        return v is Instruction { Op: Opcode.CallIntrinsic, Intrinsic: Intrinsic.NonUniformIndex };
    }
}
