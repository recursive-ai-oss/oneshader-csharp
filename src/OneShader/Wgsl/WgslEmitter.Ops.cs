using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using OneShader.Diagnostics;
using OneShader.IR;
using OneShader.Types;

namespace OneShader.Wgsl;

internal sealed partial class WgslEmitter
{
    private enum NK
    {
        Bool,
        Float,
        SInt,
        UInt,
        Other,
    }

    private static NK KindOf(HlslType t)
    {
        var s = t.ScalarElement;
        if (s is null)
        {
            return NK.Other;
        }

        if (s.ScalarKind == ScalarKind.Bool)
        {
            return NK.Bool;
        }

        if (s.IsFloatingPoint)
        {
            return NK.Float;
        }

        return s.IsSignedInteger ? NK.SInt : NK.UInt;
    }

    private static int Count(HlslType t) => t switch
    {
        VectorType v => v.Count,
        MatrixType m => m.Rows * m.Columns,
        _ => 1,
    };

    private static bool Degenerate(MatrixType m) => m.Rows == 1 || m.Columns == 1;

    private static HlslType Shape(ScalarType t, int n) => n == 1 ? t : HlslType.Vector(t, n);

    private static string Swizzle(int count) => "xyzw".Substring(0, count);

    // ---- conversions ------------------------------------------------------------------------------------

    private string CastScalars(string e, ScalarType fs, ScalarType ts, int n)
    {
        var target = Shape(ts, n);
        if (ScalarName(fs) == ScalarName(ts))
        {
            return e;
        }

        return TypeName(target) + "(" + e + ")";
    }

    private string ConvertExpr(string e, HlslType from, HlslType to)
    {
        if (TypeName(from) == TypeName(to))
        {
            return e;
        }

        var fs = from.ScalarElement;
        var ts = to.ScalarElement;
        if (fs is null || ts is null)
        {
            Unsupported("Cannot convert " + from + " to " + to + ".");
            return e;
        }

        if (from is MatrixType fm && to is MatrixType tm && !Degenerate(fm) && !Degenerate(tm) && fm.Rows == tm.Rows && fm.Columns == tm.Columns)
        {
            e = Simple(e);
            var rows = new string[fm.Rows];
            for (int r = 0; r < rows.Length; r++)
            {
                rows[r] = CastScalars(e + "[" + Inv(r) + "]", fs, ts, fm.Columns);
            }

            return TypeName(to) + "(" + string.Join(", ", rows) + ")";
        }

        int fc = Count(from);
        int tc = Count(to);
        if (fc == tc)
        {
            return CastScalars(e, fs, ts, fc);
        }

        if (fc == 1)
        {
            string s = CastScalars(e, fs, ts, 1);
            return TypeName(Shape(ts, tc)) + "(" + s + ")";
        }

        if (tc < fc)
        {
            string cut = tc == 1 ? "(" + e + ").x" : "(" + e + ")." + Swizzle(tc);
            return CastScalars(cut, fs, ts, tc);
        }

        Unsupported("Cannot widen " + from + " to " + to + " with a conversion.");
        return e;
    }

    // ---- instructions -------------------------------------------------------------------------------------

    private void EmitInstruction(Instruction i)
    {
        switch (i.Op)
        {
            case Opcode.Alloca:
            {
                var pointee = ((PointerType)i.Type).Pointee;
                string name = NewLocal(i.Name);
                _prologue.Add("var " + name + ": " + TypeName(pointee) + ";");
                _mems[i] = new PlainMem(pointee, name, null);
                break;
            }

            case Opcode.Load:
            {
                var m = MemOf(i.Operands[0]);
                if (m is null)
                {
                    Fail(WgslCodes.MalformedIr, "A load does not address a variable or buffer.", i.Span);
                    break;
                }

                Define(i, ReadMem(m));
                break;
            }

            case Opcode.Store:
            {
                var m = MemOf(i.Operands[0]);
                if (m is null)
                {
                    Fail(WgslCodes.MalformedIr, "A store does not address a variable or buffer.", i.Span);
                    break;
                }

                WriteMem(m, Val(i.Operands[1]));
                break;
            }

            case Opcode.AccessChain:
            {
                var m = ChainMem(i);
                if (m is not null)
                {
                    _mems[i] = m;
                }

                break;
            }

            case Opcode.ArrayLength:
                EmitArrayLength(i);
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
                NK k = KindOf(i.Type);
                string a = Val(i.Operands[0]);
                string b = Val(i.Operands[1]);
                string op = i.Op switch
                {
                    Opcode.BitAnd => "&",
                    Opcode.BitOr => "|",
                    _ => k == NK.Bool ? "!=" : "^",
                };
                Define(i, a + " " + op + " " + b);
                break;
            }

            case Opcode.BitNot:
                Define(i, (KindOf(i.Type) == NK.Bool ? "!" : "~") + Val(i.Operands[0]));
                break;
            case Opcode.Shl or Opcode.Shr:
                EmitShift(i);
                break;
            case Opcode.LogicalAnd:
                Define(i, Val(i.Operands[0]) + " & " + Val(i.Operands[1]));
                break;
            case Opcode.LogicalOr:
                Define(i, Val(i.Operands[0]) + " | " + Val(i.Operands[1]));
                break;
            case Opcode.LogicalNot:
                Define(i, "!" + Val(i.Operands[0]));
                break;
            case Opcode.Eq or Opcode.Ne or Opcode.Lt or Opcode.Le or Opcode.Gt or Opcode.Ge:
                EmitCompare(i);
                break;
            case Opcode.Select:
                EmitSelect(i);
                break;
            case Opcode.Convert:
            {
                string e = Val(i.Operands[0]);
                string r = ConvertExpr(e, i.Operands[0].Type, i.Type);
                if (r == e)
                {
                    AliasTo(i, i.Operands[0]);
                }
                else
                {
                    Define(i, r);
                }

                break;
            }

            case Opcode.Bitcast:
                EmitBitcast(i);
                break;
            case Opcode.CompositeConstruct:
                EmitConstruct(i);
                break;
            case Opcode.CompositeExtract or Opcode.CompositeInsert:
                EmitExtractInsert(i);
                break;
            case Opcode.VectorShuffle:
                EmitShuffle(i);
                break;
            case Opcode.VectorExtractDynamic:
                if (Count(i.Operands[0].Type) == 1)
                {
                    AliasTo(i, i.Operands[0]);
                }
                else
                {
                    Define(i, Paren(Val(i.Operands[0])) + "[" + Val(ClampIndex(i.Operands[1], i.Operands[0].Type)) + "]");
                }

                break;
            case Opcode.VectorInsertDynamic:
                if (Count(i.Operands[0].Type) == 1)
                {
                    AliasTo(i, i.Operands[1]);
                }
                else
                {
                    string name = Define(i, Val(i.Operands[0]), true);
                    Put(name + "[" + Val(ClampIndex(i.Operands[2], i.Operands[0].Type)) + "] = " + Val(i.Operands[1]) + ";");
                }

                break;
            case Opcode.MatMul:
                EmitMatMul(i);
                break;
            case Opcode.Transpose:
                if (i.Type is MatrixType tm && !Degenerate(tm) && i.Operands[0].Type is MatrixType sm && !Degenerate(sm))
                {
                    Define(i, "transpose(" + Val(i.Operands[0]) + ")");
                }
                else if (i.Type is MatrixType tm2 && i.Operands[0].Type is MatrixType sm2 && Degenerate(tm2) && Degenerate(sm2))
                {
                    AliasTo(i, i.Operands[0]);
                }
                else
                {
                    Unsupported("This transpose changes between a vector-shaped and a full matrix; it is not supported.", i.Span);
                }

                break;
            case Opcode.OuterProduct:
                EmitOuterProduct(i);
                break;
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
                    Unsupported("The instruction '" + i.Op + "' is not supported by the WGSL backend.", i.Span);
                }

                break;
        }
    }

    private static string Paren(string e) => IsIdentifier(e) ? e : "(" + e + ")";

    private void EmitCall(Instruction i)
    {
        var args = new string[i.Operands.Count];
        for (int k = 0; k < args.Length; k++)
        {
            var a = i.Operands[k];
            if (a.Type is PointerType)
            {
                if (MemOf(a) is PlainMem p)
                {
                    args[k] = "&" + Render(p);
                }
                else
                {
                    Unsupported("A resource or buffer is passed to '" + i.Callee!.Name + "' as an argument; the function must be inlined first (mark it [forceinline]).", i.Span);
                    args[k] = "0";
                }
            }
            else
            {
                args[k] = Val(a);
            }
        }

        if (!_fnNames.TryGetValue(i.Callee!, out var callee))
        {
            Fail(WgslCodes.MalformedIr, "The call to '" + i.Callee!.Name + "' targets a function that is not part of the module.", i.Span);
            return;
        }

        string call = callee + "(" + string.Join(", ", args) + ")";
        if (i.IsVoid)
        {
            Put(call + ";");
        }
        else
        {
            Define(i, call);
        }
    }

    // ---- arithmetic ---------------------------------------------------------------------------------------

    private void EmitArith(Instruction i)
    {
        NK k = KindOf(i.Type);
        if (k is NK.Bool or NK.Other)
        {
            Unsupported("Arithmetic on " + i.Type + " is not supported.", i.Span);
            return;
        }

        string op = i.Op switch
        {
            Opcode.Add => "+",
            Opcode.Sub => "-",
            Opcode.Mul => "*",
            Opcode.Div => "/",
            _ => "%",
        };
        string a = Val(i.Operands[0]);
        IrValue divisor = i.Operands[1];
        if (i.Op is Opcode.Div or Opcode.Rem && k is NK.SInt or NK.UInt && divisor is Constant dc)
        {
            // WGSL rejects a constant zero divisor at shader creation; at run time x / 0 is x and x % 0 is 0, which is what x / 1 and x % 1 give.
            divisor = NonZero(dc);
        }

        string b = Val(divisor);
        if (i.Type is MatrixType m && !Degenerate(m) && i.Op is not (Opcode.Add or Opcode.Sub))
        {
            // WGSL's * on matrices is the matrix product and / and % do not exist for them, so work column by column.
            a = Simple(a);
            b = Simple(b);
            var cols = new string[m.Rows];
            for (int r = 0; r < cols.Length; r++)
            {
                string ca = i.Operands[0].Type is MatrixType ? a + "[" + Inv(r) + "]" : a;
                string cb = i.Operands[1].Type is MatrixType ? b + "[" + Inv(r) + "]" : b;
                cols[r] = ca + " " + op + " " + cb;
            }

            Define(i, TypeName(m) + "(" + string.Join(", ", cols) + ")");
            return;
        }

        Define(i, a + " " + op + " " + b);
    }

    private static Constant NonZero(Constant c)
    {
        if (c.ConstantKind == ConstantKind.Scalar)
        {
            return c.Bits == 0 && c.Type is ScalarType st ? Constant.FromBits(st, 1) : c;
        }

        if (c.ConstantKind != ConstantKind.Composite)
        {
            return c;
        }

        var list = new Constant[c.Elements.Count];
        for (int n = 0; n < list.Length; n++)
        {
            list[n] = NonZero(c.Elements[n]);
        }

        return Constant.Composite(c.Type, list);
    }

    private void EmitNeg(Instruction i)
    {
        NK k = KindOf(i.Type);
        string a = Val(i.Operands[0]);
        if (i.Type is MatrixType m && !Degenerate(m))
        {
            a = Simple(a);
            var cols = new string[m.Rows];
            for (int r = 0; r < cols.Length; r++)
            {
                cols[r] = "-" + a + "[" + Inv(r) + "]";
            }

            Define(i, TypeName(m) + "(" + string.Join(", ", cols) + ")");
            return;
        }

        // WGSL has no unary minus on u32; two's complement negation gives the wrapped result HLSL has.
        Define(i, k == NK.UInt ? "~" + Paren(a) + " + 1u" : "-" + Paren(a));
    }

    private void EmitShift(Instruction i)
    {
        var count = i.Operands[1];
        var a = i.Operands[0];
        int n = Count(a.Type);
        string shifted;
        if (count is Constant { ConstantKind: ConstantKind.Scalar } c)
        {
            // WGSL rejects a constant shift count of 32 or more; HLSL uses the low five bits.
            shifted = Inv((uint)(c.Bits & 31)) + "u";
            if (n > 1)
            {
                shifted = "vec" + Inv(n) + "<u32>(" + shifted + ")";
            }
        }
        else
        {
            var uintShape = Shape(ScalarType.UInt, n);
            shifted = ConvertExpr(Val(count), count.Type, uintShape);
            string mask = n > 1 ? "vec" + Inv(n) + "<u32>(31u)" : "31u";
            shifted = "(" + shifted + " & " + mask + ")";
        }

        Define(i, Val(a) + (i.Op == Opcode.Shl ? " << " : " >> ") + shifted);
    }

    private void EmitCompare(Instruction i)
    {
        var operandType = i.Operands[0].Type;
        if (KindOf(operandType) == NK.Other || operandType is MatrixType { } mt && !Degenerate(mt))
        {
            Unsupported(operandType is MatrixType ? "Comparing whole matrices is not supported; compare rows or elements." : "Cannot compare values of type " + operandType + ".", i.Span);
            return;
        }

        if (KindOf(operandType) == NK.Bool && i.Op is not (Opcode.Eq or Opcode.Ne))
        {
            Unsupported("Ordering comparisons on bool are not supported.", i.Span);
            return;
        }

        string op = i.Op switch
        {
            Opcode.Eq => "==",
            Opcode.Ne => "!=",
            Opcode.Lt => "<",
            Opcode.Le => "<=",
            Opcode.Gt => ">",
            _ => ">=",
        };
        Define(i, Val(i.Operands[0]) + " " + op + " " + Val(i.Operands[1]));
    }

    private void EmitSelect(Instruction i)
    {
        string c = Val(i.Operands[0]);
        string t = Val(i.Operands[1]);
        string f = Val(i.Operands[2]);
        if (i.Type is ScalarType or VectorType || i.Type is MatrixType { } m && Degenerate(m))
        {
            Define(i, "select(" + f + ", " + t + ", " + c + ")");
            return;
        }

        // select() only takes scalars and vectors; other types go through a variable.
        string name = Define(i, f, true);
        Open("if (" + c + ")");
        Put(name + " = " + t + ";");
        Close();
    }

    private void EmitBitcast(Instruction i)
    {
        string e = Val(i.Operands[0]);
        if (TypeName(i.Operands[0].Type) == TypeName(i.Type))
        {
            AliasTo(i, i.Operands[0]);
            return;
        }

        if (i.Type is MatrixType { } m && !Degenerate(m))
        {
            Unsupported("Reinterpreting a full matrix's bits is not supported.", i.Span);
            return;
        }

        if (KindOf(i.Type) == NK.Bool || KindOf(i.Operands[0].Type) == NK.Bool)
        {
            Unsupported("Reinterpreting bool bits is not supported.", i.Span);
            return;
        }

        Define(i, "bitcast<" + TypeName(i.Type) + ">(" + e + ")");
    }

    // ---- composites ---------------------------------------------------------------------------------------

    private void EmitConstruct(Instruction i)
    {
        var t = i.Type;
        var parts = new string[i.Operands.Count];
        for (int k = 0; k < parts.Length; k++)
        {
            parts[k] = Val(i.Operands[k]);
        }

        if (Count(t) == 1 && t is not (ArrayType or StructType))
        {
            AliasTo(i, i.Operands[0]);
            return;
        }

        if (t is MatrixType { Rows: 1 })
        {
            AliasTo(i, i.Operands[0]);
            return;
        }

        Define(i, TypeName(t) + "(" + string.Join(", ", parts) + ")");
    }

    private string IndexSuffix(HlslType start, IReadOnlyList<int> path)
    {
        var sb = new StringBuilder();
        var cur = start;
        foreach (int imm in path)
        {
            if (StepType(cur, Constant.Int(imm), out var next, out int field))
            {
                if (field >= 0)
                {
                    sb.Append('.').Append(FieldName((StructType)cur, field));
                }
                else
                {
                    sb.Append('[').Append(Inv(imm)).Append(']');
                }
            }

            cur = next;
        }

        return sb.ToString();
    }

    private void EmitExtractInsert(Instruction i)
    {
        string suffix = IndexSuffix(i.Operands[0].Type, i.Immediates);
        if (i.Op == Opcode.CompositeInsert)
        {
            if (suffix.Length == 0)
            {
                AliasTo(i, i.Operands[1]);
                return;
            }

            string name = Define(i, Val(i.Operands[0]), true);
            Put(name + suffix + " = " + Val(i.Operands[1]) + ";");
            return;
        }

        if (suffix.Length == 0)
        {
            AliasTo(i, i.Operands[0]);
            return;
        }

        Define(i, Paren(Val(i.Operands[0])) + suffix);
    }

    private void EmitShuffle(Instruction i)
    {
        var a = i.Operands[0];
        var b = i.Operands[1];
        int na = Count(a.Type);
        int nb = Count(b.Type);
        int nr = i.Immediates.Count;
        string ea = Val(a);
        string eb = Val(b);
        bool allA = true;
        foreach (int c in i.Immediates)
        {
            allA &= c < na;
        }

        if (allA && na >= 2 && nr >= 1)
        {
            var sb = new StringBuilder();
            foreach (int c in i.Immediates)
            {
                sb.Append("xyzw"[c]);
            }

            Define(i, Paren(ea) + "." + sb);
            return;
        }

        var parts = new string[nr];
        for (int k = 0; k < nr; k++)
        {
            int comp = i.Immediates[k];
            bool fromA = comp < na;
            int local = fromA ? comp : comp - na;
            string src = fromA ? ea : eb;
            int n = fromA ? na : nb;
            parts[k] = n == 1 ? src : Paren(src) + "." + "xyzw"[local];
        }

        if (nr == 1)
        {
            Define(i, parts[0]);
        }
        else
        {
            Define(i, TypeName(i.Type) + "(" + string.Join(", ", parts) + ")");
        }
    }

    private void EmitMatMul(Instruction i)
    {
        var a = i.Operands[0].Type;
        var b = i.Operands[1].Type;
        string ea = Val(i.Operands[0]);
        string eb = Val(i.Operands[1]);
        bool aMat = a is MatrixType am && !Degenerate(am);
        bool bMat = b is MatrixType bm && !Degenerate(bm);
        // An HLSL row is a WGSL column, so every product swaps its operands (docs/wgsl-backend.md).
        if ((aMat && bMat) || (a is VectorType && bMat) || (aMat && b is VectorType))
        {
            Define(i, eb + " * " + ea);
        }
        else if (a is VectorType && b is VectorType)
        {
            Define(i, "dot(" + ea + ", " + eb + ")");
        }
        else
        {
            Unsupported("This matrix product involves a single-row or single-column matrix; use vectors or float2x2 and larger.", i.Span);
        }
    }

    private void EmitOuterProduct(Instruction i)
    {
        if (i.Type is not MatrixType m || Degenerate(m))
        {
            Unsupported("An outer product with a one-component vector is not supported.", i.Span);
            return;
        }

        string a = Simple(Val(i.Operands[0]));
        string b = Val(i.Operands[1]);
        var cols = new string[m.Rows];
        for (int r = 0; r < cols.Length; r++)
        {
            cols[r] = b + " * " + a + "." + "xyzw"[r];
        }

        Define(i, TypeName(m) + "(" + string.Join(", ", cols) + ")");
    }

    // ---- atomics, barriers, array length ----------------------------------------------------------------------

    private void EmitArrayLength(Instruction i)
    {
        if (i.Operands[0] is not GlobalVariable g || !_bufs.TryGetValue(g, out var info))
        {
            Fail(WgslCodes.MalformedIr, "An array length is requested of something that is not a storage buffer.", i.Span);
            return;
        }

        string words = "arrayLength(&" + info.Name + ")";
        Define(i, info.ByteAddress ? words : "(" + words + " * 4u) / " + Inv(info.Stride) + "u");
    }

    private void EmitBarrier(Instruction i)
    {
        var flags = (BarrierFlags)i.Immediates[0];
        // WGSL only has barriers that also synchronize the invocations of the workgroup, so a memory-only barrier
        // becomes the matching full barrier; that is stronger, never weaker, but must sit in uniform control flow.
        bool group = flags.HasFlag(BarrierFlags.GroupMemory);
        bool device = flags.HasFlag(BarrierFlags.DeviceMemory);
        if (device)
        {
            Put("storageBarrier();");
        }

        if (group || (flags.HasFlag(BarrierFlags.GroupSync) && !device))
        {
            Put("workgroupBarrier();");
        }
    }

    private void EmitAtomic(Instruction i)
    {
        if (i.Operands[0].Type is PointerType { Pointee: ResourceType { IsTexture: true } })
        {
            Unsupported("Atomic operations on textures are not available in WGSL; use a structured buffer.", i.Span);
            return;
        }

        string? addr = AtomicAddress(i.Operands[0]);
        if (addr is null)
        {
            return;
        }

        bool storage = MemOf(i.Operands[0]) is BufMem;
        bool signed = KindOf(i.Type) == NK.SInt;
        bool viaBits = storage && signed;
        string Operand(int index)
        {
            string v = Val(i.Operands[index]);
            return viaBits ? "bitcast<u32>(" + v + ")" : v;
        }

        string call;
        if (i.Op == Opcode.AtomicCompareExchange)
        {
            string helper = StrongCompareExchange(storage, signed || !storage && KindOf(i.Type) == NK.SInt);
            call = helper + "(" + addr + ", " + Operand(1) + ", " + Operand(2) + ")";
        }
        else
        {
            var op = (AtomicOp)i.Immediates[0];
            if (op is AtomicOp.Min or AtomicOp.Max && viaBits)
            {
                call = SignedStorageMinMax(op == AtomicOp.Min) + "(" + addr + ", " + Val(i.Operands[1]) + ")";
                Define(i, call);
                return;
            }

            string fn = op switch
            {
                AtomicOp.Add => "atomicAdd",
                AtomicOp.Min => "atomicMin",
                AtomicOp.Max => "atomicMax",
                AtomicOp.And => "atomicAnd",
                AtomicOp.Or => "atomicOr",
                AtomicOp.Xor => "atomicXor",
                _ => "atomicExchange",
            };
            call = fn + "(" + addr + ", " + Operand(1) + ")";
        }

        Define(i, viaBits ? "bitcast<i32>(" + call + ")" : call);
    }

    private readonly Dictionary<string, string> _helperTexts = [];

    private void AddHelper(string name, string text)
    {
        if (!_helperTexts.ContainsKey(name))
        {
            _helperTexts[name] = text;
        }
    }

    /// <summary>
    /// WGSL only has a weak compare-exchange that may fail although the value matched; HLSL callers read the returned
    /// value to learn whether the swap happened, so retry until the outcome is definite.
    /// </summary>
    private string StrongCompareExchange(bool storage, bool signed)
    {
        string t = storage ? "u32" : signed ? "i32" : "u32";
        string ptr = storage ? "ptr<storage, atomic<u32>, read_write>" : "ptr<workgroup, atomic<" + t + ">>";
        string name = "osh_atomic_cas_" + (storage ? "storage" : "workgroup") + "_" + t;
        AddHelper(name, "fn " + name + "(p: " + ptr + ", compare: " + t + ", value: " + t + ") -> " + t + " {\n" +
            "  loop {\n" +
            "    let r = atomicCompareExchangeWeak(p, compare, value);\n" +
            "    if (r.exchanged || r.old_value != compare) {\n" +
            "      return r.old_value;\n" +
            "    }\n" +
            "  }\n" +
            "}\n");
        _moduleNames.Add(name);
        return name;
    }

    /// <summary>Storage words are 32-bit unsigned atomics, so a signed minimum or maximum is a compare-exchange loop.</summary>
    private string SignedStorageMinMax(bool min)
    {
        string name = min ? "osh_atomic_min_i32" : "osh_atomic_max_i32";
        string pick = min ? "min" : "max";
        AddHelper(name, "fn " + name + "(p: ptr<storage, atomic<u32>, read_write>, value: i32) -> i32 {\n" +
            "  var old = atomicLoad(p);\n" +
            "  loop {\n" +
            "    let wanted = bitcast<u32>(" + pick + "(bitcast<i32>(old), value));\n" +
            "    let r = atomicCompareExchangeWeak(p, old, wanted);\n" +
            "    if (r.exchanged) {\n" +
            "      return bitcast<i32>(old);\n" +
            "    }\n" +
            "    old = r.old_value;\n" +
            "  }\n" +
            "}\n");
        _moduleNames.Add(name);
        return name;
    }
}
