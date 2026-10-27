using System.Collections.Generic;
using OneShader.IR;
using OneShader.Types;

namespace OneShader.Optimization;

/// <summary>
/// Folds constants and applies algebraic simplifications that are exact: identities that hold for every
/// input including NaN and signed zero, and structural ones (extracting from a construct, selecting on a
/// constant). Instructions marked <c>precise</c> only get constant folding.
/// </summary>
internal static class InstCombine
{
    public static bool Run(IrFunction f)
    {
        var map = IrUtil.NewMap();
        bool changed = false;
        foreach (var block in f.Blocks)
        {
            var output = new List<Instruction>(block.Instructions.Count);
            foreach (var i in block.Instructions)
            {
                var ops = i.Operands;
                for (int k = 0; k < ops.Count; k++)
                {
                    ops[k] = IrUtil.Resolve(map, ops[k]);
                }

                IrValue? replacement = null;
                if (IrUtil.IsRemovable(i) && i.Op != Opcode.Alloca)
                {
                    replacement = Simplify(i, output);
                }

                if (replacement is not null && !ReferenceEquals(replacement, i))
                {
                    map[i] = replacement;
                    changed = true;
                }
                else
                {
                    output.Add(i);
                }
            }

            block.Instructions.Clear();
            block.Instructions.AddRange(output);
        }

        if (map.Count > 0)
        {
            IrUtil.ApplyReplacements(f, map);
        }

        return changed || map.Count > 0;
    }

    private static IrValue? Simplify(Instruction i, List<Instruction> emit)
    {
        if (i.Op != Opcode.Phi && AllConstant(i) && ConstFold.Fold(i) is { } folded && folded.Type.Equals(i.Type))
        {
            return folded;
        }

        var ops = i.Operands;
        switch (i.Op)
        {
            case Opcode.Phi:
                return SimplifyPhi(i);
            case Opcode.Select:
                if (ops[0] is Constant { Type: ScalarType, ConstantKind: ConstantKind.Scalar } c)
                {
                    return c.Bits != 0 ? ops[1] : ops[2];
                }

                if (SameValue(ops[1], ops[2]))
                {
                    return ops[1];
                }

                if (i.Type.Equals(ScalarType.Bool) && ops[0].Type.Equals(ScalarType.Bool) && IsBool(ops[1], true) && IsBool(ops[2], false))
                {
                    return ops[0];
                }

                return null;
            case Opcode.Convert or Opcode.Bitcast:
                return i.Type.Equals(ops[0].Type) ? ops[0] : null;
            case Opcode.CompositeExtract:
                return SimplifyExtract(i, emit);
            case Opcode.VectorShuffle:
                return SimplifyShuffle(i, emit);
            case Opcode.LogicalNot or Opcode.BitNot or Opcode.Neg:
                if (ops[0] is Instruction inner && inner.Op == i.Op && !i.Precise && !inner.Precise)
                {
                    return inner.Operands[0];
                }

                return null;
        }

        if (i.Precise || ops.Count != 2)
        {
            return null;
        }

        return SimplifyBinary(i);
    }

    private static bool AllConstant(Instruction i)
    {
        if (i.Operands.Count == 0)
        {
            return false;
        }

        foreach (var o in i.Operands)
        {
            if (o is not Constant)
            {
                return false;
            }
        }

        return true;
    }

    private static IrValue? SimplifyBinary(Instruction i)
    {
        var a = i.Operands[0];
        var b = i.Operands[1];
        var kind = i.Type.ScalarElement?.ScalarKind;
        bool isInt = kind is ScalarKind.Int or ScalarKind.UInt;
        bool isFloat = kind is ScalarKind.Float or ScalarKind.Double;
        switch (i.Op)
        {
            case Opcode.Add:
                if (isInt && IsAll(b, 0))
                {
                    return a;
                }

                if (isInt && IsAll(a, 0))
                {
                    return b;
                }

                if (isFloat && IsNegativeZero(b))
                {
                    return a;
                }

                return null;
            case Opcode.Sub:
                if ((isInt || isFloat) && IsAll(b, 0))
                {
                    return a;
                }

                return null;
            case Opcode.Mul:
                if ((isInt || isFloat) && IsOne(b))
                {
                    return a;
                }

                if ((isInt || isFloat) && IsOne(a))
                {
                    return b;
                }

                if (isInt && (IsAll(b, 0) || IsAll(a, 0)))
                {
                    return Constant.Zero(i.Type);
                }

                return null;
            case Opcode.Div:
                return (isInt || isFloat) && IsOne(b) ? a : null;
            case Opcode.BitOr or Opcode.BitXor:
                if (isInt && IsAll(b, 0))
                {
                    return a;
                }

                if (isInt && IsAll(a, 0))
                {
                    return b;
                }

                return null;
            case Opcode.BitAnd:
                if (isInt && (IsAll(b, 0) || IsAll(a, 0)))
                {
                    return Constant.Zero(i.Type);
                }

                if (isInt && IsAll(b, 0xFFFF_FFFFUL))
                {
                    return a;
                }

                if (isInt && IsAll(a, 0xFFFF_FFFFUL))
                {
                    return b;
                }

                return null;
            case Opcode.Shl or Opcode.Shr:
                return isInt && IsAll(b, 0) ? a : null;
            case Opcode.LogicalAnd:
                if (IsBool(b, true))
                {
                    return a;
                }

                if (IsBool(a, true))
                {
                    return b;
                }

                return IsBool(a, false) || IsBool(b, false) ? Constant.Zero(i.Type) : null;
            case Opcode.LogicalOr:
                if (IsBool(b, false))
                {
                    return a;
                }

                if (IsBool(a, false))
                {
                    return b;
                }

                return null;
            default:
                return null;
        }
    }

    private static bool SameValue(IrValue a, IrValue b) =>
        ReferenceEquals(a, b) || (a is Constant ca && b is Constant cb && ca.ConstantKind != ConstantKind.Undef && ca.ValueEquals(cb));

    private static IEnumerable<Constant> Scalars(IrValue v)
    {
        if (v is Constant { ConstantKind: ConstantKind.Scalar } s)
        {
            yield return s;
        }
        else if (v is Constant { ConstantKind: ConstantKind.Composite, Type: VectorType } c)
        {
            foreach (var e in c.Elements)
            {
                yield return e;
            }
        }
    }

    private static bool IsAll(IrValue v, ulong bits)
    {
        bool any = false;
        foreach (var s in Scalars(v))
        {
            any = true;
            if (s.Bits != bits)
            {
                return false;
            }
        }

        return any;
    }

    private static bool IsBool(IrValue v, bool value) =>
        v.Type.ScalarElement?.ScalarKind == ScalarKind.Bool && IsAll(v, value ? 1UL : 0UL);

    private static bool IsNegativeZero(IrValue v)
    {
        bool any = false;
        foreach (var s in Scalars(v))
        {
            any = true;
            ulong expected = s.Type is ScalarType { ScalarKind: ScalarKind.Double } ? 0x8000_0000_0000_0000UL : 0x8000_0000UL;
            if (s.Bits != expected)
            {
                return false;
            }
        }

        return any;
    }

    private static bool IsOne(IrValue v)
    {
        bool any = false;
        foreach (var s in Scalars(v))
        {
            any = true;
            ulong one = s.Type is ScalarType t
                ? t.ScalarKind switch
                {
                    ScalarKind.Float => 0x3F80_0000UL,
                    ScalarKind.Double => 0x3FF0_0000_0000_0000UL,
                    ScalarKind.Int or ScalarKind.UInt => 1UL,
                    _ => ulong.MaxValue,
                }
                : ulong.MaxValue;
            if (s.Bits != one)
            {
                return false;
            }
        }

        return any;
    }

    private static IrValue? SimplifyPhi(Instruction phi)
    {
        IrValue? only = null;
        bool sawUndef = false;
        for (int k = 0; k < phi.Operands.Count; k++)
        {
            var v = phi.Operands[k];
            if (ReferenceEquals(v, phi))
            {
                continue;
            }

            if (v is Constant { ConstantKind: ConstantKind.Undef })
            {
                sawUndef = true;
                continue;
            }

            if (only is null)
            {
                only = v;
            }
            else if (!SameValue(only, v))
            {
                return null;
            }
        }

        if (only is null)
        {
            return null;
        }

        // An undefined path may take any value, so it can be ignored when the other value is available
        // everywhere: constants, arguments and globals always are; an instruction might not dominate the phi.
        if (sawUndef && only is Instruction)
        {
            return null;
        }

        return only;
    }

    private static IrValue? SimplifyExtract(Instruction i, List<Instruction> emit)
    {
        var src = i.Operands[0];
        if (i.Immediates.Count == 0)
        {
            return src;
        }

        if (src is not Instruction def)
        {
            return null;
        }

        int first = i.Immediates[0];
        switch (def.Op)
        {
            case Opcode.CompositeConstruct:
                if (def.Type is VectorType && i.Immediates.Count == 1)
                {
                    return ExtractFromVectorConstruct(def, first, i, emit);
                }

                if (def.Type is StructType or ArrayType && first >= 0 && first < def.Operands.Count)
                {
                    return ExtractRest(def.Operands[first], i, 1, emit);
                }

                return null;
            case Opcode.CompositeInsert:
            {
                int n = System.Math.Min(def.Immediates.Count, i.Immediates.Count);
                bool equalPrefix = true;
                for (int k = 0; k < n; k++)
                {
                    if (def.Immediates[k] != i.Immediates[k])
                    {
                        if (def.Immediates.Count >= 1)
                        {
                            // Different element at this depth: the insert does not touch what we read.
                            return ExtractFromOther(def.Operands[0], i, emit);
                        }

                        equalPrefix = false;
                        break;
                    }
                }

                if (equalPrefix && def.Immediates.Count == i.Immediates.Count)
                {
                    return def.Operands[1];
                }

                if (equalPrefix && def.Immediates.Count < i.Immediates.Count)
                {
                    return ExtractRest(def.Operands[1], i, def.Immediates.Count, emit);
                }

                return null;
            }

            case Opcode.VectorShuffle when i.Immediates.Count == 1 && first >= 0 && first < def.Immediates.Count:
            {
                int sel = def.Immediates[first];
                int leftCount = (def.Operands[0].Type as VectorType)?.Count ?? 0;
                if (leftCount == 0)
                {
                    return null;
                }

                var source = sel < leftCount ? def.Operands[0] : def.Operands[1];
                int index = sel < leftCount ? sel : sel - leftCount;
                return MakeExtract(source, [index], i.Type, i, emit);
            }

            default:
                return null;
        }
    }

    private static IrValue? ExtractFromOther(IrValue other, Instruction i, List<Instruction> emit) =>
        MakeExtract(other, i.Immediates.ToArray(), i.Type, i, emit);

    private static IrValue? ExtractRest(IrValue value, Instruction i, int skip, List<Instruction> emit)
    {
        if (skip == i.Immediates.Count)
        {
            return value;
        }

        var rest = new int[i.Immediates.Count - skip];
        i.Immediates.CopyTo(skip, rest, 0, rest.Length);
        return MakeExtract(value, rest, i.Type, i, emit);
    }

    private static IrValue MakeExtract(IrValue source, int[] path, HlslType type, Instruction like, List<Instruction> emit)
    {
        if (path.Length == 0)
        {
            return source;
        }

        var e = IrUtil.Make(like.Parent!, Opcode.CompositeExtract, type, source);
        e.Immediates.AddRange(path);
        e.Span = like.Span;
        e.InlinedAt = like.InlinedAt;
        e.Name = like.Name;
        emit.Add(e);
        return e;
    }

    private static IrValue? ExtractFromVectorConstruct(Instruction construct, int index, Instruction like, List<Instruction> emit)
    {
        var ops = construct.Operands;
        if (ops.Count == 1 && ops[0].Type is ScalarType)
        {
            return ops[0]; // splat
        }

        int at = 0;
        foreach (var o in ops)
        {
            int width = o.Type is VectorType v ? v.Count : 1;
            if (index < at + width)
            {
                return o.Type is VectorType ? MakeExtract(o, [index - at], like.Type, like, emit) : o;
            }

            at += width;
        }

        return null;
    }

    private static IrValue? SimplifyShuffle(Instruction i, List<Instruction> emit)
    {
        var a = i.Operands[0];
        var b = i.Operands[1];
        if (a.Type is not VectorType va)
        {
            return null;
        }

        bool identity = i.Immediates.Count == va.Count && i.Type.Equals(a.Type);
        for (int k = 0; identity && k < i.Immediates.Count; k++)
        {
            identity = i.Immediates[k] == k;
        }

        if (identity)
        {
            return a;
        }

        if (i.Immediates.Count == 0 || i.Type is not VectorType rt)
        {
            return null;
        }

        // A swizzle of a construct of scalars is a smaller construct: no extra instructions, no shuffle.
        if (a is Instruction { Op: Opcode.CompositeConstruct } c && ReferenceEquals(a, b) && AllScalar(c) && c.Operands.Count == va.Count)
        {
            var parts = new IrValue[i.Immediates.Count];
            for (int k = 0; k < parts.Length; k++)
            {
                if (i.Immediates[k] < 0 || i.Immediates[k] >= c.Operands.Count)
                {
                    return null;
                }

                parts[k] = c.Operands[i.Immediates[k]];
            }

            var made = IrUtil.Make(i.Parent!, Opcode.CompositeConstruct, rt, parts);
            made.Span = i.Span;
            made.InlinedAt = i.InlinedAt;
            made.Name = i.Name;
            emit.Add(made);
            return made;
        }

        return null;
    }

    private static bool AllScalar(Instruction c)
    {
        foreach (var o in c.Operands)
        {
            if (o.Type is not ScalarType)
            {
                return false;
            }
        }

        return true;
    }
}
