using System.Collections.Generic;
using OneShader.IR;
using OneShader.Types;

namespace OneShader.Optimization;

/// <summary>
/// Splits function-local structs and small arrays into one variable per element when every access
/// names the element with a constant index (or loads/stores the whole value), so promotion to SSA can
/// treat each piece on its own.
/// </summary>
internal static class Sroa
{
    private const int MaxElements = 64;

    public static bool Run(IrFunction f)
    {
        bool any = false;
        for (int round = 0; round < 16 && RunRound(f); round++)
        {
            any = true;
        }

        return any;
    }

    private static bool RunRound(IrFunction f)
    {
        if (f.Entry is not { } entry)
        {
            return false;
        }

        var users = IrUtil.BuildUsers(f);
        var splittable = new Dictionary<Instruction, Instruction[]>(ReferenceEqualityComparer.Instance);
        foreach (var inst in entry.Instructions)
        {
            if (inst.Op != Opcode.Alloca || inst.Type is not PointerType { Pointee: var pointee } || ElementCount(pointee) is not { } count)
            {
                continue;
            }

            if (CanSplit(inst, count, users))
            {
                var pieces = new Instruction[count];
                for (int k = 0; k < count; k++)
                {
                    var et = ElementType(pointee, k);
                    var p = IrUtil.Make(entry, Opcode.Alloca, new PointerType(AddressSpace.Function, et));
                    p.Name = inst.Name is null ? null : inst.Name + "." + k;
                    p.Span = inst.Span;
                    p.InlinedAt = inst.InlinedAt;
                    pieces[k] = p;
                }

                splittable[inst] = pieces;
            }
        }

        if (splittable.Count == 0)
        {
            return false;
        }

        var map = IrUtil.NewMap();
        foreach (var block in f.Blocks)
        {
            var output = new List<Instruction>(block.Instructions.Count);
            foreach (var i in block.Instructions)
            {
                if (i.Op == Opcode.Alloca && splittable.TryGetValue(i, out var pieces))
                {
                    output.AddRange(pieces);
                    continue;
                }

                if (i.Op == Opcode.Load && i.Operands[0] is Instruction lp && splittable.TryGetValue(lp, out var lpieces))
                {
                    var parts = new IrValue[lpieces.Length];
                    for (int k = 0; k < parts.Length; k++)
                    {
                        var ld = IrUtil.Make(block, Opcode.Load, ((PointerType)lpieces[k].Type).Pointee, lpieces[k]);
                        ld.Span = i.Span;
                        ld.InlinedAt = i.InlinedAt;
                        output.Add(ld);
                        parts[k] = ld;
                    }

                    var whole = IrUtil.Make(block, Opcode.CompositeConstruct, i.Type, parts);
                    whole.Span = i.Span;
                    whole.InlinedAt = i.InlinedAt;
                    whole.Name = i.Name;
                    output.Add(whole);
                    map[i] = whole;
                    continue;
                }

                if (i.Op == Opcode.Store && i.Operands[0] is Instruction sp && splittable.TryGetValue(sp, out var spieces))
                {
                    for (int k = 0; k < spieces.Length; k++)
                    {
                        var ex = IrUtil.Make(block, Opcode.CompositeExtract, ((PointerType)spieces[k].Type).Pointee, i.Operands[1]);
                        ex.Immediates.Add(k);
                        ex.Span = i.Span;
                        ex.InlinedAt = i.InlinedAt;
                        output.Add(ex);
                        var st = IrUtil.Make(block, Opcode.Store, HlslType.Void, spieces[k], ex);
                        st.Span = i.Span;
                        st.InlinedAt = i.InlinedAt;
                        output.Add(st);
                    }

                    continue;
                }

                if (i.Op == Opcode.AccessChain && i.Operands[0] is Instruction cp && splittable.TryGetValue(cp, out var cpieces))
                {
                    int index = (int)((Constant)i.Operands[1]).Bits;
                    if (i.Operands.Count == 2)
                    {
                        map[i] = cpieces[index];
                        continue;
                    }

                    i.Operands[0] = cpieces[index];
                    i.Operands.RemoveAt(1);
                }

                output.Add(i);
            }

            block.Instructions.Clear();
            block.Instructions.AddRange(output);
        }

        IrUtil.ApplyReplacements(f, map);
        return true;
    }

    private static int? ElementCount(HlslType t) => t switch
    {
        StructType s when s.Fields.Count is > 0 and <= MaxElements => s.Fields.Count,
        ArrayType a when a.Length is > 0 and <= MaxElements => a.Length,
        _ => null,
    };

    private static HlslType ElementType(HlslType t, int k) => t is StructType s ? s.Fields[k].Type : ((ArrayType)t).Element;

    private static bool CanSplit(Instruction alloca, int count, Dictionary<IrValue, List<Instruction>> users)
    {
        if (!users.TryGetValue(alloca, out var list))
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
                    // The alloca may be the destination but must not be the value being stored (that would be a pointer copy).
                    if (!ReferenceEquals(u.Operands[0], alloca) || ReferenceEquals(u.Operands[1], alloca))
                    {
                        return false;
                    }

                    break;
                case Opcode.AccessChain:
                    if (!ReferenceEquals(u.Operands[0], alloca) || u.Operands[1] is not Constant { ConstantKind: ConstantKind.Scalar } idx
                        || idx.Type is not ScalarType { ScalarKind: ScalarKind.Int or ScalarKind.UInt } || (int)idx.Bits < 0 || (int)idx.Bits >= count)
                    {
                        return false;
                    }

                    for (int k = 2; k < u.Operands.Count; k++)
                    {
                        if (ReferenceEquals(u.Operands[k], alloca))
                        {
                            return false;
                        }
                    }

                    break;
                default:
                    return false;
            }
        }

        return true;
    }
}
