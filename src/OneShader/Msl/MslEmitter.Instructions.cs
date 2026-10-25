using System;
using System.Collections.Generic;
using System.Text;
using OneShader.Diagnostics;
using OneShader.IR;
using OneShader.Types;

namespace OneShader.Msl;

internal sealed partial class MslEmitter
{
    private enum NumKind
    {
        Bool,
        Float,
        SInt,
        UInt,
        Other,
    }

    private static NumKind KindOf(HlslType t)
    {
        var s = t.ScalarElement ?? (t as EnumType)?.Underlying;
        if (s is null)
        {
            return NumKind.Other;
        }

        if (s.ScalarKind == ScalarKind.Bool)
        {
            return NumKind.Bool;
        }

        if (s.IsFloatingPoint)
        {
            return NumKind.Float;
        }

        return s.IsSignedInteger ? NumKind.SInt : NumKind.UInt;
    }

    private void Define(Instruction i, string expr)
    {
        string n = NameOf(i);
        W(_hoisted.Contains(i) ? n + " = " + expr + ";" : T(i.Type) + " " + n + " = " + expr + ";");
    }

    /// <summary>Makes a value an expression that can be indexed or swizzled more than once without recomputation.</summary>
    private string Atom(string expr, HlslType type)
    {
        if (IsPlainName(expr))
        {
            return expr;
        }

        string tmp = Tmp("a");
        W(T(type) + " " + tmp + " = " + expr + ";");
        return tmp;
    }

    private void EmitInstruction(Instruction i)
    {
        var o = i.Operands;
        switch (i.Op)
        {
            case Opcode.Alloca or Opcode.Phi or Opcode.AccessChain:
                break;
            case Opcode.Load:
                Define(i, LoadValue(PlaceOf(o[0])));
                break;
            case Opcode.Store:
                StoreValue(PlaceOf(o[0]), V(o[1]));
                break;
            case Opcode.ArrayLength:
                Unsupported("Metal has no way to ask a buffer for its length. Pass the element count in a constant buffer instead of using GetDimensions.", i.Span);
                Define(i, "0u");
                break;
            case Opcode.AtomicRmw or Opcode.AtomicCompareExchange:
                EmitAtomic(i);
                break;
            case Opcode.Barrier:
                EmitBarrier(i);
                break;
            case Opcode.Add or Opcode.Sub or Opcode.Mul or Opcode.Div or Opcode.Rem:
                EmitArith(i);
                break;
            case Opcode.Neg:
                EmitNeg(i);
                break;
            case Opcode.BitAnd or Opcode.BitOr or Opcode.BitXor:
            {
                bool isBool = KindOf(i.Type) == NumKind.Bool;
                string op = i.Op switch
                {
                    Opcode.BitAnd => isBool ? "&&" : "&",
                    Opcode.BitOr => isBool ? "||" : "|",
                    _ => isBool ? "!=" : "^",
                };
                Define(i, V(o[0]) + " " + op + " " + V(o[1]));
                break;
            }

            case Opcode.BitNot:
                Define(i, (KindOf(i.Type) == NumKind.Bool ? "!" : "~") + V(o[0]));
                break;
            case Opcode.Shl or Opcode.Shr:
                EmitShift(i);
                break;
            case Opcode.LogicalAnd:
                Define(i, V(o[0]) + " && " + V(o[1]));
                break;
            case Opcode.LogicalOr:
                Define(i, V(o[0]) + " || " + V(o[1]));
                break;
            case Opcode.LogicalNot:
                Define(i, "!" + V(o[0]));
                break;
            case Opcode.Eq or Opcode.Ne or Opcode.Lt or Opcode.Le or Opcode.Gt or Opcode.Ge:
                EmitCompare(i);
                break;
            case Opcode.Select:
                if (o[0].Type is VectorType { Count: > 1 })
                {
                    Define(i, "select(" + V(o[2]) + ", " + V(o[1]) + ", " + V(o[0]) + ")");
                }
                else
                {
                    Define(i, "(" + V(o[0]) + " ? " + V(o[1]) + " : " + V(o[2]) + ")");
                }

                break;
            case Opcode.Convert:
                Define(i, ConvertExpr(V(o[0]), o[0].Type, i.Type));
                break;
            case Opcode.Bitcast:
                if (T(o[0].Type) == T(i.Type))
                {
                    Define(i, V(o[0]));
                }
                else if (IsFullMatrix(i.Type) || KindOf(i.Type) == NumKind.Bool || KindOf(o[0].Type) == NumKind.Bool)
                {
                    Unsupported("Reinterpreting the bits of a matrix or bool value is not supported.", i.Span);
                    Define(i, T(i.Type) + "()");
                }
                else
                {
                    Define(i, "as_type<" + T(i.Type) + ">(" + V(o[0]) + ")");
                }

                break;
            case Opcode.CompositeConstruct:
                EmitConstruct(i);
                break;
            case Opcode.CompositeExtract:
                Define(i, ExtractPath(V(o[0]), o[0].Type, i.Immediates, i.Span, out _));
                break;
            case Opcode.CompositeInsert:
            {
                string tmp = Tmp("c");
                W(T(i.Type) + " " + tmp + " = " + V(o[0]) + ";");
                W(ExtractPath(tmp, o[0].Type, i.Immediates, i.Span, out _) + " = " + V(o[1]) + ";");
                Define(i, tmp);
                break;
            }

            case Opcode.VectorShuffle:
                EmitShuffle(i);
                break;
            case Opcode.VectorExtractDynamic:
                Define(i, Comp(o[0].Type) == 1 ? V(o[0]) : V(o[0]) + "[" + V(o[1]) + "]");
                break;
            case Opcode.VectorInsertDynamic:
                if (Comp(o[0].Type) == 1)
                {
                    Define(i, V(o[1]));
                }
                else
                {
                    string tmp = Tmp("c");
                    W(T(i.Type) + " " + tmp + " = " + V(o[0]) + ";");
                    W(tmp + "[" + V(o[2]) + "] = " + V(o[1]) + ";");
                    Define(i, tmp);
                }

                break;
            case Opcode.MatMul:
                EmitMatMul(i);
                break;
            case Opcode.Transpose:
                if (IsFullMatrix(i.Type) && IsFullMatrix(o[0].Type))
                {
                    Define(i, "transpose(" + V(o[0]) + ")");
                }
                else if (!IsFullMatrix(i.Type) && !IsFullMatrix(o[0].Type))
                {
                    Define(i, V(o[0]));
                }
                else
                {
                    Unsupported("This transpose changes between a vector-shaped and a full matrix; it is not supported.", i.Span);
                    Define(i, T(i.Type) + "()");
                }

                break;
            case Opcode.OuterProduct:
            {
                var m = (MatrixType)i.Type;
                string a = Atom(V(o[0]), o[0].Type);
                string b = Atom(V(o[1]), o[1].Type);
                var rows = new List<string>();
                for (int r = 0; r < m.Rows; r++)
                {
                    rows.Add(b + " * " + (Comp(o[0].Type) == 1 ? a : a + "." + Swz(r)));
                }

                Define(i, T(m) + "(" + string.Join(", ", rows) + ")");
                break;
            }

            case Opcode.Call:
                EmitCall(i);
                break;
            case Opcode.CallIntrinsic:
                EmitIntrinsic(i);
                break;
            default:
                if (i.Op >= Opcode.TextureSample && i.Op <= Opcode.ByteAddressStore)
                {
                    EmitImageOrBuffer(i);
                }
                else
                {
                    Unsupported("The instruction '" + i.Op + "' is not supported by the MSL backend.", i.Span);
                }

                break;
        }
    }

    // ---- arithmetic ---------------------------------------------------------------------------------------

    private void EmitArith(Instruction i)
    {
        var k = KindOf(i.Type);
        if (k is NumKind.Bool or NumKind.Other)
        {
            Unsupported("Arithmetic on " + i.Type + " is not supported.", i.Span);
            Define(i, T(i.Type) + "()");
            return;
        }

        string a = V(i.Operands[0]);
        string b = V(i.Operands[1]);
        string sym = i.Op switch { Opcode.Add => "+", Opcode.Sub => "-", Opcode.Mul => "*", Opcode.Div => "/", _ => "%" };
        if (i.Type is MatrixType m && IsFullMatrix(m))
        {
            if (i.Op is Opcode.Add or Opcode.Sub)
            {
                Define(i, a + " " + sym + " " + b);
                return;
            }

            // MSL's * on matrices is the matrix product, so component-wise operations go row by row.
            a = Atom(a, i.Operands[0].Type);
            b = Atom(b, i.Operands[1].Type);
            var rows = new List<string>();
            for (int r = 0; r < m.Rows; r++)
            {
                rows.Add(i.Op == Opcode.Rem ? "fmod(" + a + "[" + Inv(r) + "], " + b + "[" + Inv(r) + "])" : a + "[" + Inv(r) + "] " + sym + " " + b + "[" + Inv(r) + "]");
            }

            Define(i, T(m) + "(" + string.Join(", ", rows) + ")");
            return;
        }

        if (i.Op == Opcode.Rem && k == NumKind.Float)
        {
            Define(i, "fmod(" + Fit(a, i.Operands[0].Type, i.Type) + ", " + Fit(b, i.Operands[1].Type, i.Type) + ")");
            return;
        }

        Define(i, a + " " + sym + " " + b);
    }

    /// <summary>Widens a scalar to the vector type the operation works on; other values pass through.</summary>
    private string Fit(string e, HlslType from, HlslType to) =>
        Comp(from) == 1 && Comp(to) > 1 ? T(to) + "(" + e + ")" : e;

    private void EmitNeg(Instruction i)
    {
        string a = V(i.Operands[0]);
        if (i.Type is MatrixType m && IsFullMatrix(m))
        {
            a = Atom(a, i.Operands[0].Type);
            var rows = new List<string>();
            for (int r = 0; r < m.Rows; r++)
            {
                rows.Add("-" + a + "[" + Inv(r) + "]");
            }

            Define(i, T(m) + "(" + string.Join(", ", rows) + ")");
            return;
        }

        Define(i, "-" + a);
    }

    private void EmitShift(Instruction i)
    {
        // HLSL masks the shift count to the operand width; C++ leaves larger counts undefined.
        var scalar = i.Type.ScalarElement ?? ScalarType.UInt;
        int bits = scalar.ScalarKind is ScalarKind.Int64 or ScalarKind.UInt64 ? 64 : scalar.ScalarKind is ScalarKind.Int16 or ScalarKind.UInt16 ? 16 : 32;
        long mask = bits - 1;
        string sym = i.Op == Opcode.Shl ? "<<" : ">>";
        string count;
        if (i.Operands[1] is Constant { ConstantKind: ConstantKind.Scalar } c)
        {
            count = Inv((long)(c.Bits & (ulong)mask)) + "u";
            if (i.Type is VectorType { Count: > 1 })
            {
                count = T(i.Type) + "(" + count + ")";
            }
        }
        else
        {
            string maskText = T(i.Type) + "(" + Inv(mask) + ")";
            count = "(" + T(i.Type) + "(" + V(i.Operands[1]) + ") & " + maskText + ")";
        }

        Define(i, V(i.Operands[0]) + " " + sym + " " + count);
    }

    private void EmitCompare(Instruction i)
    {
        var operandType = i.Operands[0].Type;
        var k = KindOf(operandType);
        if (k == NumKind.Other || IsFullMatrix(operandType) || k == NumKind.Bool && i.Op is not (Opcode.Eq or Opcode.Ne))
        {
            Unsupported("Cannot compare values of type " + operandType + " in MSL output" + (IsFullMatrix(operandType) ? "; compare rows or elements." : "."), i.Span);
            Define(i, T(i.Type) + "()");
            return;
        }

        string sym = i.Op switch { Opcode.Eq => "==", Opcode.Ne => "!=", Opcode.Lt => "<", Opcode.Le => "<=", Opcode.Gt => ">", _ => ">=" };
        Define(i, V(i.Operands[0]) + " " + sym + " " + V(i.Operands[1]));
    }

    // ---- conversions and composites -----------------------------------------------------------------------

    private string ConvertExpr(string e, HlslType from, HlslType to)
    {
        if (T(from) == T(to))
        {
            return e;
        }

        var fs = from.ScalarElement ?? (from as EnumType)?.Underlying;
        var ts = to.ScalarElement ?? (to as EnumType)?.Underlying;
        if (fs is null || ts is null)
        {
            Unsupported("Cannot convert " + from + " to " + to + ".");
            return T(to) + "()";
        }

        if (from is MatrixType fm && to is MatrixType tm && IsFullMatrix(fm) && IsFullMatrix(tm) && fm.Rows == tm.Rows && fm.Columns == tm.Columns)
        {
            string a = Atom(e, from);
            var rows = new List<string>();
            for (int r = 0; r < fm.Rows; r++)
            {
                rows.Add(ScalarName(ts) + Inv(fm.Columns) + "(" + a + "[" + Inv(r) + "])");
            }

            return T(to) + "(" + string.Join(", ", rows) + ")";
        }

        int fc = Comp(from);
        int tc = Comp(to);
        if (fc == tc || fc == 1)
        {
            return T(to) + "(" + e + ")";
        }

        if (tc < fc)
        {
            string cut = Head(Atom(e, from), tc, fc);
            return ScalarName(fs) == ScalarName(ts) ? cut : T(to) + "(" + cut + ")";
        }

        Unsupported("Cannot widen " + from + " to " + to + " with a conversion.");
        return T(to) + "()";
    }

    private void EmitConstruct(Instruction i)
    {
        var t = i.Type;
        var ops = new List<string>();
        foreach (var op in i.Operands)
        {
            ops.Add(V(op));
        }

        switch (t)
        {
            case VectorType { Count: 1 }:
                Define(i, ops[0]);
                return;
            case MatrixType m when IsFullMatrix(m):
                if (ops.Count == m.Rows * m.Columns && m.Rows * m.Columns != m.Rows)
                {
                    var rows = new List<string>();
                    for (int r = 0; r < m.Rows; r++)
                    {
                        rows.Add(ScalarName(m.Element) + Inv(m.Columns) + "(" + string.Join(", ", ops.GetRange(r * m.Columns, m.Columns)) + ")");
                    }

                    Define(i, T(m) + "(" + string.Join(", ", rows) + ")");
                }
                else
                {
                    Define(i, T(m) + "(" + string.Join(", ", ops) + ")");
                }

                return;
            case ArrayType or StructType:
                Define(i, T(t) + "{" + string.Join(", ", ops) + "}");
                return;
            case MatrixType m2 when m2.Rows * m2.Columns == 1:
                Define(i, ops[0]);
                return;
            default:
                Define(i, T(t) + "(" + string.Join(", ", ops) + ")");
                return;
        }
    }

    /// <summary>The expression reaching into <paramref name="baseExpr"/> along literal indices.</summary>
    private string ExtractPath(string baseExpr, HlslType type, List<int> indices, SourceSpan span, out HlslType result)
    {
        string e = baseExpr;
        var cur = type;
        foreach (int ix in indices)
        {
            switch (cur)
            {
                case StructType st when ix >= 0 && ix < st.Fields.Count:
                    e += "." + FieldName(st, ix);
                    cur = st.Fields[ix].Type;
                    break;
                case ArrayType a:
                    e += "[" + Inv(ix) + "]";
                    cur = a.Element;
                    break;
                case VectorType v:
                    if (v.Count > 1)
                    {
                        e += "." + Swz(ix);
                    }

                    cur = v.Element;
                    break;
                case MatrixType m:
                    if (m.Rows == 1)
                    {
                        cur = HlslType.Vector(m.Element, m.Columns);
                    }
                    else if (m.Columns == 1)
                    {
                        e += "." + Swz(ix);
                        cur = m.Element;
                    }
                    else
                    {
                        e += "[" + Inv(ix) + "]";
                        cur = HlslType.Vector(m.Element, m.Columns);
                    }

                    break;
                default:
                    Malformed("An extract or insert index does not fit the value's type.", span);
                    result = HlslType.Error;
                    return e;
            }
        }

        result = cur;
        return e;
    }

    private void EmitShuffle(Instruction i)
    {
        var a = i.Operands[0];
        var b = i.Operands[1];
        int na = Comp(a.Type);
        int nb = Comp(b.Type);
        int nr = i.Immediates.Count;
        string ea = V(a);
        string eb = V(b);
        if (ReferenceEquals(a, b) || ea == eb)
        {
            bool inRange = true;
            foreach (int ix in i.Immediates)
            {
                inRange &= ix < na;
            }

            if (na >= 2 && inRange)
            {
                var sb = new StringBuilder();
                foreach (int ix in i.Immediates)
                {
                    sb.Append(Swz(ix));
                }

                Define(i, Atom(ea, a.Type) + "." + sb);
                return;
            }
        }

        ea = na > 1 ? Atom(ea, a.Type) : ea;
        eb = nb > 1 ? Atom(eb, b.Type) : eb;
        var comps = new List<string>();
        foreach (int ix in i.Immediates)
        {
            comps.Add(ix < na ? (na == 1 ? ea : ea + "." + Swz(ix)) : (nb == 1 ? eb : eb + "." + Swz(ix - na)));
        }

        Define(i, nr == 1 ? comps[0] : T(i.Type) + "(" + string.Join(", ", comps) + ")");
    }

    private void EmitMatMul(Instruction i)
    {
        var at = i.Operands[0].Type;
        var bt = i.Operands[1].Type;
        string a = V(i.Operands[0]);
        string b = V(i.Operands[1]);
        bool aMat = IsFullMatrix(at);
        bool bMat = IsFullMatrix(bt);
        // HLSL matrices map transposed (one MSL column per HLSL row), so every product swaps its operands.
        if (aMat && bMat || at is VectorType && bMat || aMat && bt is VectorType)
        {
            Define(i, b + " * " + a);
        }
        else if (at is VectorType && bt is VectorType)
        {
            Define(i, "dot(" + a + ", " + b + ")");
        }
        else
        {
            Unsupported("This matrix product involves a single-row or single-column matrix; use vectors or float2x2 and larger.", i.Span);
            Define(i, T(i.Type) + "()");
        }
    }

    // ---- calls ----------------------------------------------------------------------------------------------

    private void EmitCall(Instruction i)
    {
        var callee = i.Callee!;
        if (!_fnNames.TryGetValue(callee, out var name))
        {
            Malformed("The call to '" + callee.Name + "' targets a function that is not part of the module.", i.Span);
            return;
        }

        var args = new List<string>();
        foreach (var a in i.Operands)
        {
            if (a.Type is PointerType)
            {
                var p = PlaceOf(a);
                if (p.Kind != PlaceKind.Reg)
                {
                    Unsupported("A buffer or resource is passed to '" + callee.Name + "' as an argument; the function must be inlined first (mark it [forceinline]).", i.Span);
                    args.Add("0");
                }
                else
                {
                    args.Add(p.Expr);
                }
            }
            else
            {
                args.Add(V(a));
            }
        }

        if (_needed.TryGetValue(callee, out var lifted))
        {
            foreach (var g in lifted)
            {
                args.Add(NameOf(g));
            }
        }

        string call = name + "(" + string.Join(", ", args) + ")";
        if (i.IsVoid)
        {
            W(call + ";");
        }
        else
        {
            Define(i, call);
        }
    }

    // ---- atomics and barriers -------------------------------------------------------------------------------

    private void EmitAtomic(Instruction i)
    {
        if (i.Operands[0].Type is PointerType { Pointee: ResourceType })
        {
            Unsupported("Atomic operations on a texel need Metal 3.1; this output targets Metal 2.4. Use a buffer for atomics.", i.Span);
            Define(i, T(i.Type) + "()");
            return;
        }

        var place = PlaceOf(i.Operands[0]);
        if (place.Kind is not (PlaceKind.Reg or PlaceKind.Mem))
        {
            Unsupported("An atomic operation addresses something that is not a scalar in memory.", i.Span);
            Define(i, T(i.Type) + "()");
            return;
        }

        bool isSigned = KindOf(i.Type) == NumKind.SInt;
        string scalar = isSigned ? "int" : "uint";
        if (KindOf(i.Type) is not (NumKind.SInt or NumKind.UInt) || (i.Type.ScalarElement?.ScalarKind is ScalarKind.Int64 or ScalarKind.UInt64))
        {
            Unsupported("Metal atomics work on 32-bit integers only.", i.Span);
            Define(i, T(i.Type) + "()");
            return;
        }

        string expr = place.Expr;
        if (place.Space == "thread")
        {
            // A thread-private variable needs no atomicity.
            EmitPlainReadModifyWrite(i, expr);
            return;
        }

        string ptr = "(" + place.Space + " atomic_" + scalar + "*)&" + expr;
        if (i.Op == Opcode.AtomicCompareExchange)
        {
            string cmp = V(i.Operands[1]);
            string value = V(i.Operands[2]);
            string exp = Tmp("e");
            W(scalar + " " + exp + " = " + cmp + ";");
            W("while (!atomic_compare_exchange_weak_explicit(" + ptr + ", &" + exp + ", " + value + ", memory_order_relaxed, memory_order_relaxed))");
            W("{");
            _indent++;
            W("if (" + exp + " != " + cmp + ")");
            W("{");
            _indent++;
            W("break;");
            _indent--;
            W("}");
            _indent--;
            W("}");
            Define(i, exp);
            return;
        }

        string v = V(i.Operands[1]);
        string fn = (AtomicOp)i.Immediates[0] switch
        {
            AtomicOp.Add => "atomic_fetch_add_explicit",
            AtomicOp.Min => "atomic_fetch_min_explicit",
            AtomicOp.Max => "atomic_fetch_max_explicit",
            AtomicOp.And => "atomic_fetch_and_explicit",
            AtomicOp.Or => "atomic_fetch_or_explicit",
            AtomicOp.Xor => "atomic_fetch_xor_explicit",
            _ => "atomic_exchange_explicit",
        };
        Define(i, fn + "(" + ptr + ", " + v + ", memory_order_relaxed)");
    }

    private void EmitPlainReadModifyWrite(Instruction i, string expr)
    {
        string old = Tmp("o");
        W(T(i.Type) + " " + old + " = " + expr + ";");
        if (i.Op == Opcode.AtomicCompareExchange)
        {
            W("if (" + old + " == " + V(i.Operands[1]) + ")");
            W("{");
            _indent++;
            W(expr + " = " + V(i.Operands[2]) + ";");
            _indent--;
            W("}");
        }
        else
        {
            string v = V(i.Operands[1]);
            string update = (AtomicOp)i.Immediates[0] switch
            {
                AtomicOp.Add => old + " + " + v,
                AtomicOp.Min => "min(" + old + ", " + v + ")",
                AtomicOp.Max => "max(" + old + ", " + v + ")",
                AtomicOp.And => old + " & " + v,
                AtomicOp.Or => old + " | " + v,
                AtomicOp.Xor => old + " ^ " + v,
                _ => v,
            };
            W(expr + " = " + update + ";");
        }

        Define(i, old);
    }

    private void EmitBarrier(Instruction i)
    {
        var flags = (BarrierFlags)i.Immediates[0];
        var parts = new List<string>();
        if (flags.HasFlag(BarrierFlags.GroupMemory))
        {
            parts.Add("mem_flags::mem_threadgroup");
        }

        if (flags.HasFlag(BarrierFlags.DeviceMemory))
        {
            parts.Add("mem_flags::mem_device");
            parts.Add("mem_flags::mem_texture");
        }

        string arg = parts.Count == 0 ? "mem_flags::mem_none" : string.Join(" | ", parts);
        if (parts.Count == 0 && !flags.HasFlag(BarrierFlags.GroupSync))
        {
            return;
        }

        // MSL 2.4 has no fence without a barrier (atomic_thread_fence needs a scope added in later versions), so a memory-only
        // barrier is written as a full threadgroup barrier: stronger, but every thread of the group must reach it.
        W("threadgroup_barrier(" + arg + ");");
    }
}
