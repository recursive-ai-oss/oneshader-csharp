using System;
using System.Collections.Generic;
using OneShader.IR;
using OneShader.Types;

namespace OneShader.Optimization;

/// <summary>
/// Replaces an instruction by an identical earlier one that dominates it. Only pure instructions take
/// part: arithmetic, conversions, composites, address computation, math intrinsics, and loads from
/// memory no shader invocation can write (constant buffers, bound resources, stage inputs).
/// </summary>
internal static class Cse
{
    private sealed class Key : IEquatable<Key>
    {
        private readonly Instruction _i;
        private readonly int _hash;

        public Key(Instruction i)
        {
            _i = i;
            var h = new HashCode();
            h.Add((int)i.Op);
            h.Add((int)i.Intrinsic);
            h.Add(i.Type.GetHashCode());
            foreach (var o in i.Operands)
            {
                h.Add(OperandHash(o));
            }

            foreach (int imm in i.Immediates)
            {
                h.Add(imm);
            }

            _hash = h.ToHashCode();
        }

        private static int OperandHash(IrValue v)
        {
            if (v is Constant c)
            {
                var h = new HashCode();
                h.Add((int)c.ConstantKind);
                h.Add(c.Bits);
                h.Add(c.Type.GetHashCode());
                foreach (var e in c.Elements)
                {
                    h.Add(OperandHash(e));
                }

                return h.ToHashCode();
            }

            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(v);
        }

        public bool Equals(Key? other)
        {
            if (other is null)
            {
                return false;
            }

            var a = _i;
            var b = other._i;
            if (a.Op != b.Op || a.Intrinsic != b.Intrinsic || a.Precise != b.Precise || !a.Type.Equals(b.Type)
                || a.Operands.Count != b.Operands.Count || a.Immediates.Count != b.Immediates.Count)
            {
                return false;
            }

            for (int k = 0; k < a.Operands.Count; k++)
            {
                var x = a.Operands[k];
                var y = b.Operands[k];
                if (!ReferenceEquals(x, y) && !(x is Constant cx && y is Constant cy && cx.ConstantKind != ConstantKind.Undef && cx.ValueEquals(cy)))
                {
                    return false;
                }
            }

            for (int k = 0; k < a.Immediates.Count; k++)
            {
                if (a.Immediates[k] != b.Immediates[k])
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is Key k && Equals(k);

        public override int GetHashCode() => _hash;
    }

    public static bool Run(IrFunction f)
    {
        if (f.Blocks.Count == 0)
        {
            return false;
        }

        var cfg = new Cfg(f);
        var table = new Dictionary<Key, Instruction>();
        var map = IrUtil.NewMap();
        var stack = new Stack<(int Block, int NextChild, List<Key> Added)>();
        stack.Push((0, 0, Visit(0)));
        while (stack.Count > 0)
        {
            var (b, next, added) = stack.Pop();
            if (next < cfg.DomChildren[b].Count)
            {
                stack.Push((b, next + 1, added));
                int child = cfg.DomChildren[b][next];
                stack.Push((child, 0, Visit(child)));
            }
            else
            {
                foreach (var k in added)
                {
                    table.Remove(k);
                }
            }
        }

        List<Key> Visit(int b)
        {
            var added = new List<Key>();
            foreach (var i in cfg.Blocks[b].Instructions)
            {
                for (int k = 0; k < i.Operands.Count; k++)
                {
                    i.Operands[k] = IrUtil.Resolve(map, i.Operands[k]);
                }

                if (!IsPure(i))
                {
                    continue;
                }

                var key = new Key(i);
                if (table.TryGetValue(key, out var earlier))
                {
                    map[i] = earlier;
                }
                else
                {
                    table[key] = i;
                    added.Add(key);
                }
            }

            return added;
        }

        if (map.Count == 0)
        {
            return false;
        }

        IrUtil.ApplyReplacements(f, map);
        var gone = new HashSet<Instruction>(ReferenceEqualityComparer.Instance);
        foreach (var k in map.Keys)
        {
            gone.Add((Instruction)k);
        }

        IrUtil.RemoveInstructions(f, gone);
        return true;
    }

    private static bool IsPure(Instruction i)
    {
        switch (i.Op)
        {
            case Opcode.Add or Opcode.Sub or Opcode.Mul or Opcode.Div or Opcode.Rem or Opcode.Neg or Opcode.BitAnd or Opcode.BitOr
                or Opcode.BitXor or Opcode.BitNot or Opcode.Shl or Opcode.Shr or Opcode.LogicalAnd or Opcode.LogicalOr or Opcode.LogicalNot
                or Opcode.Eq or Opcode.Ne or Opcode.Lt or Opcode.Le or Opcode.Gt or Opcode.Ge or Opcode.Select or Opcode.Convert
                or Opcode.Bitcast or Opcode.CompositeConstruct or Opcode.CompositeExtract or Opcode.CompositeInsert
                or Opcode.VectorShuffle or Opcode.VectorExtractDynamic or Opcode.VectorInsertDynamic or Opcode.MatMul
                or Opcode.Transpose or Opcode.OuterProduct or Opcode.AccessChain:
                return true;
            case Opcode.Load:
                return i.Operands[0].Type is PointerType { Space: AddressSpace.Uniform or AddressSpace.UniformConstant or AddressSpace.Input };
            case Opcode.CallIntrinsic:
                return i.Intrinsic is not (Intrinsic.Frexp or Intrinsic.Modf or Intrinsic.SinCos)
                    && i.Intrinsic is < Intrinsic.Ddx;
            default:
                return false;
        }
    }
}
