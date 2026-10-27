using System.Collections.Generic;
using OneShader.Diagnostics;
using OneShader.Reflection;
using OneShader.Types;

namespace OneShader.IR;

/// <summary>
/// Finds which optional device features an entry point needs because of 16-bit and 64-bit scalar types, and where the first
/// use of each is, so a backend that cannot express a width can name the line.
/// </summary>
internal sealed class WideTypeUse
{
    /// <summary>The device features needed.</summary>
    public DeviceFeatures Features { get; private set; }

    /// <summary>The first use of a 16-bit float (unknown when none or when the optimizer dropped the position).</summary>
    public SourceSpan Float16Site { get; private set; }

    /// <summary>The first use of a 16-bit integer.</summary>
    public SourceSpan Int16Site { get; private set; }

    /// <summary>The first use of a 64-bit float.</summary>
    public SourceSpan Float64Site { get; private set; }

    /// <summary>The first use of a 64-bit integer.</summary>
    public SourceSpan Int64Site { get; private set; }

    /// <summary>The first 64-bit atomic operation.</summary>
    public SourceSpan Int64AtomicSite { get; private set; }

    /// <summary>The first 64-bit integer vector stored in a buffer (Metal has none).</summary>
    public SourceSpan Int64VectorSite { get; private set; }

    /// <summary>True when a 64-bit integer vector is stored in a buffer.</summary>
    public bool HasInt64Vector { get; private set; }

    /// <summary>The first 64-bit atomic that is not a plain unsigned min/max whose result is unused (the only form Metal 2.4 has).</summary>
    public SourceSpan Int64AtomicUnsupportedOnMetalSite { get; private set; }

    /// <summary>True when <see cref="Int64AtomicUnsupportedOnMetalSite"/> applies.</summary>
    public bool HasInt64AtomicUnsupportedOnMetal { get; private set; }

    /// <summary>Scans the entry point's function, everything it calls, and the globals they touch.</summary>
    public static WideTypeUse Scan(IrModule module, EntryPoint entry)
    {
        var use = new WideTypeUse();
        bool e16 = module.LayoutOptions.Enable16BitTypes;
        var seen = new HashSet<IrFunction>();
        var seenGlobals = new HashSet<GlobalVariable>();
        var pending = new Stack<IrFunction>();
        pending.Push(entry.Function);
        foreach (var g in entry.Inputs)
        {
            use.Global(g, e16, seenGlobals, g.Span);
        }

        foreach (var g in entry.Outputs)
        {
            use.Global(g, e16, seenGlobals, g.Span);
        }

        while (pending.Count > 0)
        {
            var f = pending.Pop();
            if (!seen.Add(f))
            {
                continue;
            }

            var fspan = f.Span;
            foreach (var p in f.Parameters)
            {
                use.Type(p.Type, e16, fspan);
            }

            foreach (var b in f.Blocks)
            {
                foreach (var i in b.Instructions)
                {
                    use.Type(i.Type, e16, i.Span);
                    foreach (var o in i.Operands)
                    {
                        if (o is GlobalVariable g)
                        {
                            use.Global(g, e16, seenGlobals, i.Span);
                        }
                        else
                        {
                            use.Type(o.Type, e16, i.Span);
                        }
                    }

                    if (i.Op is Opcode.AtomicRmw or Opcode.AtomicCompareExchange && i.Operands.Count > 1
                        && TypeFeatures.Of(i.Operands[^1].Type, e16).HasFlag(WideTypes.Int64))
                    {
                        use.Features |= DeviceFeatures.Int64Atomics;
                        if (!use.Int64AtomicSite.IsKnown)
                        {
                            use.Int64AtomicSite = i.Span;
                        }

                        // Metal 2.4: atomic_ulong has fetch max / min on device memory only, and no result.
                        if (!IsMetalInt64Atomic(i) && !use.HasInt64AtomicUnsupportedOnMetal)
                        {
                            use.HasInt64AtomicUnsupportedOnMetal = true;
                            use.Int64AtomicUnsupportedOnMetalSite = i.Span;
                        }
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

    /// <summary>
    /// True for the one 64-bit atomic Metal 2.4 has: unsigned <c>atomic_fetch_max</c> / <c>min</c> on device memory, with the old value
    /// not used (MSL spec, atomic functions on <c>atomic_ulong</c>).
    /// </summary>
    public static bool IsMetalInt64Atomic(Instruction i) =>
        i.Op == Opcode.AtomicRmw && i.Immediates.Count > 0
        && (AtomicOp)i.Immediates[0] is AtomicOp.Max or AtomicOp.Min
        && i.Type is ScalarType { ScalarKind: ScalarKind.UInt64 }
        && i.Operands[0].Type is PointerType { Space: AddressSpace.StorageBuffer }
        && !IsUsed(i);

    private static bool IsUsed(Instruction def)
    {
        var f = def.Parent?.Parent;
        if (f is null)
        {
            return true;
        }

        foreach (var b in f.Blocks)
        {
            foreach (var i in b.Instructions)
            {
                foreach (var o in i.Operands)
                {
                    if (ReferenceEquals(o, def))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private void Global(GlobalVariable g, bool e16, HashSet<GlobalVariable> seen, SourceSpan span)
    {
        if (!seen.Add(g))
        {
            return;
        }

        var element = g.ValueType is ResourceType { Element: { } el } ? el : g.ValueType;
        var w = TypeFeatures.Of(element, e16);
        Type(element, e16, span);
        if ((w & WideTypes.Bits16) != 0)
        {
            switch (g.Type is PointerType p ? p.Space : AddressSpace.Private)
            {
                case AddressSpace.StorageBuffer:
                    Features |= DeviceFeatures.StorageBuffer16;
                    break;
                case AddressSpace.Uniform:
                    Features |= DeviceFeatures.UniformBuffer16;
                    break;
                case AddressSpace.PushConstant:
                    Features |= DeviceFeatures.PushConstant16;
                    break;
                case AddressSpace.Input or AddressSpace.Output:
                    Features |= DeviceFeatures.InputOutput16;
                    break;
                default:
                    break;
            }
        }

        if ((w & WideTypes.Int64) != 0 && g.ValueType is ResourceType && ContainsInt64Vector(element, 0))
        {
            HasInt64Vector = true;
            if (!Int64VectorSite.IsKnown)
            {
                Int64VectorSite = span;
            }
        }
    }

    private static bool ContainsInt64Vector(HlslType t, int depth)
    {
        if (depth > 64)
        {
            return false;
        }

        switch (t)
        {
            case VectorType v:
                return v.Element.ScalarKind is ScalarKind.Int64 or ScalarKind.UInt64;
            case MatrixType m:
                return m.Element.ScalarKind is ScalarKind.Int64 or ScalarKind.UInt64;
            case ArrayType a:
                return ContainsInt64Vector(a.Element, depth + 1);
            case StructType s:
                foreach (var f in s.Fields)
                {
                    if (ContainsInt64Vector(f.Type, depth + 1))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    private void Type(HlslType t, bool e16, SourceSpan span)
    {
        var w = TypeFeatures.Of(t, e16);
        if (w == WideTypes.None)
        {
            return;
        }

        if ((w & WideTypes.Float16) != 0)
        {
            Features |= DeviceFeatures.Float16;
            if (!Float16Site.IsKnown)
            {
                Float16Site = span;
            }
        }

        if ((w & WideTypes.Int16) != 0)
        {
            Features |= DeviceFeatures.Int16;
            if (!Int16Site.IsKnown)
            {
                Int16Site = span;
            }
        }

        if ((w & WideTypes.Float64) != 0)
        {
            Features |= DeviceFeatures.Float64;
            if (!Float64Site.IsKnown)
            {
                Float64Site = span;
            }
        }

        if ((w & WideTypes.Int64) != 0)
        {
            Features |= DeviceFeatures.Int64;
            if (!Int64Site.IsKnown)
            {
                Int64Site = span;
            }
        }
    }
}
