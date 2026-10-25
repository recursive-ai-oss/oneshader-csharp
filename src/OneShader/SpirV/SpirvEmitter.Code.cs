using System.Collections.Generic;
using OneShader.Diagnostics;
using OneShader.IR;
using OneShader.Semantics;
using OneShader.Types;

namespace OneShader.SpirV;

internal sealed partial class SpirvEmitter
{
    private const uint CopyObject = 83;

    private enum NK
    {
        Bool,
        Float,
        SInt,
        UInt,
        Other,
    }

    private readonly Dictionary<IrValue, uint> _ids = [];
    private readonly Dictionary<BasicBlock, uint> _labels = [];
    private IrFunction _fn = null!;
    private uint _glslImport;

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

    /// <summary>Components a value of this type has in SPIR-V (matrices count only when they degenerate to vectors).</summary>
    private static int Count(HlslType t) => t switch
    {
        VectorType v => v.Count,
        MatrixType m => m.Rows * m.Columns,
        _ => 1,
    };

    private static bool Degenerate(MatrixType m) => m.Rows == 1 || m.Columns == 1;

    private static int BitWidth(ScalarType s) => s.ScalarKind switch
    {
        ScalarKind.Int64 or ScalarKind.UInt64 or ScalarKind.Double => 64,
        ScalarKind.Int16 or ScalarKind.UInt16 => 16,
        _ => 32,
    };

    // ---- ids and emission helpers -------------------------------------------------------------

    private uint Id(IrValue v)
    {
        switch (v)
        {
            case Constant c:
                return ConstId(c);
            case GlobalVariable g:
                if (_globalIds.TryGetValue(g, out var gid))
                {
                    return gid;
                }

                Fail(SpirvCodes.MalformedIr, "The global '" + g.Name + "' is used but was not declared for this entry point.", g.Span);
                return 1;
            default:
                if (!_ids.TryGetValue(v, out var id))
                {
                    id = _s.NewId();
                    _ids[v] = id;
                }

                return id;
        }
    }

    /// <summary>The id of a pointer operand and the SPIR-V type it points to (layout-decorated inside buffers).</summary>
    private uint Ptr(IrValue v, out uint pointee)
    {
        if (v is GlobalVariable g && _sampleMasks.Contains(g))
        {
            // Address element 0 of the one-element sample-mask array.
            uint scalar = TypeId(g.ValueType);
            uint sc = g.GlobalKind == GlobalKind.Input ? Sc.Input : Sc.Output;
            uint r = Emit(Op.AccessChain, PointerTo(sc, scalar), Id(v), ConstId(Constant.UInt(0)));
            pointee = scalar;
            return r;
        }

        uint id = Id(v);
        if (!_ptrPointee.TryGetValue(v, out pointee))
        {
            pointee = v.Type is PointerType pt ? TypeId(pt.Pointee) : TypeId(v.Type);
        }

        return id;
    }

    private uint Emit(uint op, uint type, params uint[] operands)
    {
        uint r = _s.NewId();
        PutResult(op, type, r, operands);
        return r;
    }

    private void PutResult(uint op, uint type, uint result, uint[] operands)
    {
        var all = new uint[operands.Length + 2];
        all[0] = type;
        all[1] = result;
        operands.CopyTo(all, 2);
        SpirvModule.Put(_s.Code, op, all);
    }

    /// <summary>Emits a single SPIR-V instruction whose result is the IR instruction's own id.</summary>
    private uint Direct(Instruction i, uint op, uint type, params uint[] operands)
    {
        uint r = Id(i);
        PutResult(op, type, r, operands);
        NameValue(i, r);
        if (i.Precise && KindOf(i.Type) == NK.Float && op is Op.FAdd or Op.FSub or Op.FMul or Op.FDiv or Op.FNegate or Op.FRem)
        {
            _s.Decorate(r, Dec.NoContraction);
        }

        return r;
    }

    /// <summary>Records that <paramref name="produced"/> is the value of <paramref name="i"/>, copying only if something already refers to the instruction's own id.</summary>
    private void Finish(Instruction i, uint produced)
    {
        if (!_ids.TryGetValue(i, out var existing))
        {
            _ids[i] = produced;
            NameValue(i, produced);
        }
        else if (existing != produced)
        {
            SpirvModule.Put(_s.Code, CopyObject, TypeId(i.Type), existing, produced);
        }
    }

    private void NameValue(Instruction i, uint id)
    {
        if (_o.KeepNames && !string.IsNullOrEmpty(i.Name))
        {
            _s.Name(id, i.Name);
        }
    }

    private uint Glsl450()
    {
        if (_glslImport == 0)
        {
            _glslImport = _s.NewId();
            SpirvModule.PutWithString(_s.ExtImports, Op.ExtInstImport, [_glslImport], "GLSL.std.450", []);
        }

        return _glslImport;
    }

    private uint Ext(uint type, uint inst, params uint[] args)
    {
        var all = new uint[args.Length + 2];
        all[0] = Glsl450();
        all[1] = inst;
        args.CopyTo(all, 2);
        return Emit(Op.ExtInst, type, all);
    }

    private uint ExtDirect(Instruction i, uint inst, params uint[] args)
    {
        var all = new uint[args.Length + 2];
        all[0] = Glsl450();
        all[1] = inst;
        args.CopyTo(all, 2);
        return Direct(i, Op.ExtInst, TypeId(i.Type), all);
    }

    private uint UIntConst(uint v) => ConstId(Constant.UInt(v));

    // ---- functions and blocks -----------------------------------------------------------------

    private void EmitFunction(IrFunction f)
    {
        _fn = f;
        _labels.Clear();
        var blocks = OrderBlocks(f);
        foreach (var b in blocks)
        {
            _labels[b] = _s.NewId();
        }

        uint ret = TypeId(f.ReturnType);
        var fnTypeOps = new List<uint> { ret };
        foreach (var p in f.Parameters)
        {
            fnTypeOps.Add(TypeId(p.Type));
        }

        string key = "fn:" + string.Join(':', fnTypeOps);
        uint fnType = _s.DeclType(key, Op.TypeFunction, fnTypeOps.ToArray());
        uint fnId = _fnIds[f];
        SpirvModule.Put(_s.Code, Op.Function, ret, fnId, 0, fnType);
        if (_o.KeepNames)
        {
            _s.Name(fnId, f.Name);
        }

        foreach (var p in f.Parameters)
        {
            uint pid = Id(p);
            SpirvModule.Put(_s.Code, Op.FunctionParameter, TypeId(p.Type), pid);
            if (_o.KeepNames && !string.IsNullOrEmpty(p.Name))
            {
                _s.Name(pid, p.Name);
            }
        }

        foreach (var b in blocks)
        {
            SpirvModule.Put(_s.Code, Op.Label, _labels[b]);
            EmitBlock(b);
        }

        SpirvModule.Put(_s.Code, Op.FunctionEnd);
    }

    /// <summary>
    /// Orders blocks so every block follows its dominators and each construct's merge (and loop continue)
    /// block follows the blocks inside it, as SPIR-V requires. Reverse post-order of a depth-first search
    /// that treats a header's merge and continue blocks as its first children gives exactly that.
    /// Unreachable blocks are dropped. Uses an explicit stack.
    /// </summary>
    private static List<BasicBlock> OrderBlocks(IrFunction f)
    {
        var post = new List<BasicBlock>(f.Blocks.Count);
        if (f.Entry is null)
        {
            return post;
        }

        var visited = new HashSet<BasicBlock> { f.Entry };
        var stack = new Stack<(BasicBlock Block, List<BasicBlock> Kids, int Next)>();
        stack.Push((f.Entry, Children(f.Entry), 0));
        while (stack.Count > 0)
        {
            var (block, kids, next) = stack.Pop();
            if (next < kids.Count)
            {
                stack.Push((block, kids, next + 1));
                var k = kids[next];
                if (visited.Add(k))
                {
                    stack.Push((k, Children(k), 0));
                }
            }
            else
            {
                post.Add(block);
            }
        }

        post.Reverse();
        return post;
    }

    private static List<BasicBlock> Children(BasicBlock b)
    {
        var kids = new List<BasicBlock>();
        if (b.MergeBlock is { } m && b.Structure != StructureKind.None)
        {
            kids.Add(m);
        }

        if (b.ContinueBlock is { } c && b.Structure == StructureKind.Loop)
        {
            kids.Add(c);
        }

        var succ = b.Successors;
        for (int i = succ.Count - 1; i >= 0; i--)
        {
            kids.Add(succ[i]);
        }

        return kids;
    }

    private uint Label(BasicBlock b)
    {
        if (_labels.TryGetValue(b, out var l))
        {
            return l;
        }

        Fail(SpirvCodes.MalformedIr, "A branch targets a block that cannot be reached from the function entry.");
        return 1;
    }

    private void EmitBlock(BasicBlock b)
    {
        bool terminated = false;
        foreach (var inst in b.Instructions)
        {
            if (inst.IsTerminator)
            {
                EmitMerge(b, inst);
                EmitTerminator(inst);
                terminated = true;
                break;
            }

            EmitInstruction(inst);
        }

        if (!terminated)
        {
            SpirvModule.Put(_s.Code, Op.Unreachable);
        }
    }

    private void EmitMerge(BasicBlock b, Instruction term)
    {
        if (b.MergeBlock is not { } merge)
        {
            return;
        }

        if (b.Structure == StructureKind.Loop && b.ContinueBlock is { } cont)
        {
            uint control = b.Hint switch { ControlHint.Unroll => 1u, ControlHint.DontUnroll => 2u, _ => 0u };
            SpirvModule.Put(_s.Code, Op.LoopMerge, Label(merge), Label(cont), control);
        }
        else if (b.Structure == StructureKind.Selection && term.Op is Opcode.CondBranch or Opcode.Switch)
        {
            uint control = b.Hint switch { ControlHint.Flatten => 1u, ControlHint.Branch => 2u, _ => 0u };
            SpirvModule.Put(_s.Code, Op.SelectionMerge, Label(merge), control);
        }
    }

    private void EmitTerminator(Instruction t)
    {
        switch (t.Op)
        {
            case Opcode.Branch:
                SpirvModule.Put(_s.Code, Op.Branch, Label(t.Targets[0]));
                break;
            case Opcode.CondBranch:
                SpirvModule.Put(_s.Code, Op.BranchConditional, Id(t.Operands[0]), Label(t.Targets[0]), Label(t.Targets[1]));
                break;
            case Opcode.Switch:
            {
                var ops = new List<uint> { Id(t.Operands[0]), Label(t.Targets[0]) };
                for (int k = 0; k < t.Immediates.Count; k++)
                {
                    ops.Add((uint)t.Immediates[k]);
                    ops.Add(Label(t.Targets[k + 1]));
                }

                SpirvModule.Put(_s.Code, Op.Switch, ops.ToArray());
                break;
            }

            case Opcode.Return:
                if (t.Operands.Count == 1)
                {
                    SpirvModule.Put(_s.Code, Op.ReturnValue, Id(t.Operands[0]));
                }
                else
                {
                    SpirvModule.Put(_s.Code, Op.Return);
                }

                break;
            case Opcode.Kill:
                // OpKill is valid in SPIR-V 1.5; OpTerminateInvocation needs a newer environment or an extension.
                SpirvModule.Put(_s.Code, Op.Kill);
                break;
            default:
                SpirvModule.Put(_s.Code, Op.Unreachable);
                break;
        }
    }

    // ---- instructions -------------------------------------------------------------------------

    private void EmitInstruction(Instruction i)
    {
        switch (i.Op)
        {
            case Opcode.Alloca:
            {
                uint pointee = TypeId(((PointerType)i.Type).Pointee);
                uint r = Id(i);
                SpirvModule.Put(_s.Code, Op.Variable, PointerTo(Sc.Function, pointee), r, Sc.Function);
                _ptrPointee[i] = pointee;
                NameValue(i, r);
                break;
            }

            case Opcode.Load:
                EmitLoad(i);
                break;
            case Opcode.Store:
                EmitStore(i);
                break;
            case Opcode.AccessChain:
                EmitAccessChain(i);
                break;
            case Opcode.ArrayLength:
            {
                uint p = Ptr(i.Operands[0], out _);
                Direct(i, Op.ArrayLength, TypeId(i.Type), p, 0);
                break;
            }

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
                uint op = i.Op switch
                {
                    Opcode.BitAnd => k == NK.Bool ? Op.LogicalAnd : Op.BitwiseAnd,
                    Opcode.BitOr => k == NK.Bool ? Op.LogicalOr : Op.BitwiseOr,
                    _ => k == NK.Bool ? Op.LogicalNotEqual : Op.BitwiseXor,
                };
                Direct(i, op, TypeId(i.Type), Id(i.Operands[0]), Id(i.Operands[1]));
                break;
            }

            case Opcode.BitNot:
                Direct(i, KindOf(i.Type) == NK.Bool ? Op.LogicalNot : Op.Not, TypeId(i.Type), Id(i.Operands[0]));
                break;
            case Opcode.Shl or Opcode.Shr:
            {
                uint amount = ShiftAmount(i.Operands[1]);
                uint op = i.Op == Opcode.Shl ? Op.ShiftLeftLogical : KindOf(i.Type) == NK.SInt ? Op.ShiftRightArithmetic : Op.ShiftRightLogical;
                Direct(i, op, TypeId(i.Type), Id(i.Operands[0]), amount);
                break;
            }

            case Opcode.LogicalAnd:
                Direct(i, Op.LogicalAnd, TypeId(i.Type), Id(i.Operands[0]), Id(i.Operands[1]));
                break;
            case Opcode.LogicalOr:
                Direct(i, Op.LogicalOr, TypeId(i.Type), Id(i.Operands[0]), Id(i.Operands[1]));
                break;
            case Opcode.LogicalNot:
                Direct(i, Op.LogicalNot, TypeId(i.Type), Id(i.Operands[0]));
                break;
            case Opcode.Eq or Opcode.Ne or Opcode.Lt or Opcode.Le or Opcode.Gt or Opcode.Ge:
                EmitCompare(i);
                break;
            case Opcode.Select:
                Direct(i, Op.Select, TypeId(i.Type), Id(i.Operands[0]), Id(i.Operands[1]), Id(i.Operands[2]));
                break;
            case Opcode.Convert:
                Finish(i, ConvertValue(Id(i.Operands[0]), i.Operands[0].Type, i.Type));
                break;
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
                    Finish(i, Id(i.Operands[0]));
                }
                else
                {
                    Direct(i, Op.VectorExtractDynamic, TypeId(i.Type), Id(i.Operands[0]), Id(i.Operands[1]));
                }

                break;
            case Opcode.VectorInsertDynamic:
                if (Count(i.Operands[0].Type) == 1)
                {
                    Finish(i, Id(i.Operands[1]));
                }
                else
                {
                    Direct(i, Op.VectorInsertDynamic, TypeId(i.Type), Id(i.Operands[0]), Id(i.Operands[1]), Id(i.Operands[2]));
                }

                break;
            case Opcode.MatMul:
                EmitMatMul(i);
                break;
            case Opcode.Transpose:
                if (i.Type is MatrixType tm && !Degenerate(tm) && i.Operands[0].Type is MatrixType sm && !Degenerate(sm))
                {
                    Direct(i, Op.Transpose, TypeId(i.Type), Id(i.Operands[0]));
                }
                else if (i.Type is MatrixType tm2 && i.Operands[0].Type is MatrixType sm2 && Degenerate(tm2) && Degenerate(sm2))
                {
                    Finish(i, Id(i.Operands[0]));
                }
                else
                {
                    Unsupported("This transpose changes between a vector-shaped and a full matrix; it is not supported.", i.Span);
                    Finish(i, Zero(i.Type));
                }

                break;
            case Opcode.OuterProduct:
                // SPIR-V columns are HLSL rows, so the operands swap to get a[i] * b[j] at row i, column j.
                Direct(i, Op.OuterProduct, TypeId(i.Type), Id(i.Operands[1]), Id(i.Operands[0]));
                break;
            case Opcode.Call:
                EmitCall(i);
                break;
            case Opcode.CallIntrinsic:
                EmitIntrinsic(i);
                break;
            case Opcode.Phi:
            {
                var ops = new List<uint>();
                for (int k = 0; k < i.Operands.Count; k++)
                {
                    if (_labels.TryGetValue(i.Targets[k], out var pred))
                    {
                        ops.Add(Id(i.Operands[k]));
                        ops.Add(pred);
                    }
                }

                Direct(i, Op.Phi, TypeId(i.Type), ops.ToArray());
                break;
            }

            default:
                if (i.Op >= Opcode.TextureSample && i.Op <= Opcode.ByteAddressStore)
                {
                    EmitImageOrBuffer(i);
                }
                else
                {
                    Unsupported("The instruction '" + i.Op + "' is not supported by the SPIR-V backend.", i.Span);
                    if (!i.IsVoid)
                    {
                        Finish(i, Zero(i.Type));
                    }
                }

                break;
        }
    }

    private void EmitCall(Instruction i)
    {
        var args = new uint[i.Operands.Count];
        for (int k = 0; k < args.Length; k++)
        {
            var a = i.Operands[k];
            if (a.Type is PointerType { Space: AddressSpace.UniformConstant or AddressSpace.Uniform or AddressSpace.StorageBuffer })
            {
                Unsupported("A resource is passed to '" + i.Callee!.Name + "' as an argument; the function must be inlined first (mark it [forceinline]).", i.Span);
            }

            args[k] = a.Type is PointerType ? Ptr(a, out _) : Id(a);
        }

        var all = new uint[args.Length + 1];
        all[0] = _fnIds.TryGetValue(i.Callee!, out var fid) ? fid : 0;
        if (all[0] == 0)
        {
            Fail(SpirvCodes.MalformedIr, "The call to '" + i.Callee!.Name + "' targets a function that is not part of the module.", i.Span);
        }

        args.CopyTo(all, 1);
        if (i.IsVoid)
        {
            Emit(Op.FunctionCall, TypeId(i.Type), all);
        }
        else
        {
            Direct(i, Op.FunctionCall, TypeId(i.Type), all);
        }
    }

    // ---- memory ---------------------------------------------------------------------------------

    private void EmitLoad(Instruction i)
    {
        uint p = Ptr(i.Operands[0], out uint pointee);
        uint want = TypeId(i.Type);
        if (pointee == want)
        {
            Direct(i, Op.Load, want, p);
            return;
        }

        uint loaded = Emit(Op.Load, pointee, p);
        Finish(i, LayoutToPlain(loaded, pointee, i.Type));
    }

    private void EmitStore(Instruction i)
    {
        uint p = Ptr(i.Operands[0], out uint pointee);
        var value = i.Operands[1];
        uint v = Id(value);
        if (TypeId(value.Type) != pointee)
        {
            v = PlainToLayout(v, value.Type, pointee);
        }

        SpirvModule.Put(_s.Code, Op.Store, p, v);
    }

    private const int MaxCopiedElements = 1024;

    private static Constant OnesOf(HlslType t)
    {
        if (t is VectorType v)
        {
            var elems = new Constant[v.Count];
            System.Array.Fill(elems, Constant.UInt(1));
            return Constant.Composite(t, elems);
        }

        return Constant.UInt(1);
    }

    /// <summary>Rebuilds a value read from a buffer (offset-decorated structs, strided arrays) as the plain type used in registers.</summary>
    private uint LayoutToPlain(uint v, uint srcType, HlslType dst)
    {
        if (TypeId(dst) == srcType)
        {
            return v;
        }

        if (dst.ScalarElement is { ScalarKind: ScalarKind.Bool } && dst is ScalarType or VectorType)
        {
            // Stored as 0 or 1 in a 32-bit unsigned integer.
            return Emit(Op.INotEqual, TypeId(dst), v, Zero(TypeOps.Reshape(dst, ScalarType.UInt)));
        }

        var info = _s.Info(srcType);
        if (info is null || _depth > MaxDepth)
        {
            Unsupported("A value of type '" + dst + "' cannot be converted from its buffer representation.");
            return Zero(dst);
        }

        _depth++;
        uint result;
        switch (dst)
        {
            case StructType st when info.Kind == TypeInfoKind.Struct && info.Children.Length == st.Fields.Count:
            {
                var parts = new uint[info.Children.Length];
                for (int k = 0; k < parts.Length; k++)
                {
                    uint sub = Emit(Op.CompositeExtract, info.Children[k], v, (uint)k);
                    parts[k] = LayoutToPlain(sub, info.Children[k], st.Fields[k].Type);
                }

                result = Emit(Op.CompositeConstruct, TypeId(dst), parts);
                break;
            }

            case ArrayType a when info.Kind == TypeInfoKind.Array && a.Length > 0 && a.Length <= MaxCopiedElements:
            {
                var parts = new uint[a.Length];
                for (int k = 0; k < parts.Length; k++)
                {
                    uint sub = Emit(Op.CompositeExtract, info.Children[0], v, (uint)k);
                    parts[k] = LayoutToPlain(sub, info.Children[0], a.Element);
                }

                result = Emit(Op.CompositeConstruct, TypeId(dst), parts);
                break;
            }

            default:
                Unsupported("A value of type '" + dst + "' cannot be converted from its buffer representation.");
                result = Zero(dst);
                break;
        }

        _depth--;
        return result;
    }

    /// <summary>The reverse of <see cref="LayoutToPlain"/>: builds the buffer representation of a register value.</summary>
    private uint PlainToLayout(uint v, HlslType src, uint dstType)
    {
        if (TypeId(src) == dstType)
        {
            return v;
        }

        if (src.ScalarElement is { ScalarKind: ScalarKind.Bool } && src is ScalarType or VectorType)
        {
            var stored = TypeOps.Reshape(src, ScalarType.UInt);
            return Emit(Op.Select, TypeId(stored), v, ConstId(OnesOf(stored)), Zero(stored));
        }

        var info = _s.Info(dstType);
        if (info is null || _depth > MaxDepth)
        {
            Unsupported("A value of type '" + src + "' cannot be converted to its buffer representation.");
            return v;
        }

        _depth++;
        uint result;
        switch (src)
        {
            case StructType st when info.Kind == TypeInfoKind.Struct && info.Children.Length == st.Fields.Count:
            {
                var parts = new uint[info.Children.Length];
                for (int k = 0; k < parts.Length; k++)
                {
                    uint sub = Emit(Op.CompositeExtract, TypeId(st.Fields[k].Type), v, (uint)k);
                    parts[k] = PlainToLayout(sub, st.Fields[k].Type, info.Children[k]);
                }

                result = Emit(Op.CompositeConstruct, dstType, parts);
                break;
            }

            case ArrayType a when info.Kind == TypeInfoKind.Array && a.Length > 0 && a.Length <= MaxCopiedElements:
            {
                var parts = new uint[a.Length];
                for (int k = 0; k < parts.Length; k++)
                {
                    uint sub = Emit(Op.CompositeExtract, TypeId(a.Element), v, (uint)k);
                    parts[k] = PlainToLayout(sub, a.Element, info.Children[0]);
                }

                result = Emit(Op.CompositeConstruct, dstType, parts);
                break;
            }

            default:
                Unsupported("A value of type '" + src + "' cannot be converted to its buffer representation.");
                result = v;
                break;
        }

        _depth--;
        return result;
    }

    /// <summary>
    /// Advances <paramref name="cur"/> by one index and says whether SPIR-V needs that index: single-component
    /// vectors and one-row matrices are plain scalars/vectors there, so their index disappears.
    /// </summary>
    private bool Step(ref HlslType cur, int? constIndex, SourceSpan span)
    {
        switch (cur)
        {
            case StructType st:
                if (constIndex is not { } c || c < 0 || c >= st.Fields.Count)
                {
                    Fail(SpirvCodes.MalformedIr, "A struct member index must be a constant inside the struct.", span);
                    cur = HlslType.Error;
                    return false;
                }

                cur = st.Fields[c].Type;
                return true;
            case ArrayType a:
                cur = a.Element;
                return true;
            case VectorType v:
                cur = v.Element;
                return v.Count != 1;
            case MatrixType m:
                cur = HlslType.Vector(m.Element, m.Columns);
                return m.Rows != 1;
            default:
                Fail(SpirvCodes.MalformedIr, "An index is applied to a value that cannot be indexed.", span);
                cur = HlslType.Error;
                return false;
        }
    }

    private uint ChildType(uint parent, int index, SourceSpan span)
    {
        var info = _s.Info(parent);
        if (info is null || info.Children.Length == 0)
        {
            Fail(SpirvCodes.MalformedIr, "An index is applied to a value that cannot be indexed.", span);
            return parent;
        }

        return info.Kind == TypeInfoKind.Struct ? info.Children[index < info.Children.Length ? index : 0] : info.Children[0];
    }

    /// <summary>
    /// The id for an access-chain index. A negative constant is emitted as the unsigned value with the same bits: the access is out of
    /// bounds either way, but spirv-val rejects negative constant indices outright.
    /// </summary>
    private uint ChainIndexId(IrValue ix) =>
        ix is Constant { ConstantKind: ConstantKind.Scalar } c && c.Type == ScalarType.Int && (int)c.Bits < 0 ? UIntConst((uint)c.Bits) : Id(ix);

    private void EmitAccessChain(Instruction i)
    {
        var baseValue = i.Operands[0];
        uint bp = Ptr(baseValue, out uint pe);
        var cur = ((PointerType)baseValue.Type).Pointee;
        var idx = new List<uint>();
        int k = 1;

        if (baseValue is GlobalVariable { ResourceCount: not 1 } && k < i.Operands.Count)
        {
            // Indexing an array of resources; a non-constant index needs the matching dynamic-indexing capability.
            if (i.Operands[k] is not Constant)
            {
                _s.Capabilities.Add(DynamicIndexingCapability(cur is ArrayType resourceElements ? resourceElements.Element : cur));
            }

            idx.Add(ChainIndexId(i.Operands[k]));
            pe = ChildType(pe, 0, i.Span);
            cur = cur is ArrayType resourceArray ? resourceArray.Element : cur;
            k++;
        }

        if (cur is ResourceType rt)
        {
            if (rt.ResourceKind == ResourceTypeKind.ConstantBuffer)
            {
                cur = rt.Element ?? HlslType.Error;
            }
            else if (rt.IsStorageBuffer && k < i.Operands.Count)
            {
                // Storage buffers are a block holding one runtime array: step into member 0, then the element.
                idx.Add(UIntConst(0));
                pe = ChildType(pe, 0, i.Span);
                idx.Add(ChainIndexId(i.Operands[k]));
                pe = ChildType(pe, 0, i.Span);
                cur = rt.Element ?? ScalarType.UInt;
                k++;
            }
        }

        for (; k < i.Operands.Count; k++)
        {
            var ix = i.Operands[k];
            int? c = ix is Constant { ConstantKind: ConstantKind.Scalar } cc ? (int)cc.Bits : null;
            if (Step(ref cur, c, i.Span))
            {
                idx.Add(ChainIndexId(ix));
                pe = ChildType(pe, c ?? 0, i.Span);
            }
        }

        _ptrPointee[i] = pe;
        bool nonUniformChain = baseValue is GlobalVariable { ResourceCount: not 1 } && i.Operands.Count > 1 && IsNonUniform(i.Operands[1]);
        if (idx.Count == 0)
        {
            Finish(i, bp);
            return;
        }

        uint sc = StorageClassOf(((PointerType)i.Type).Space);
        var ops = new uint[idx.Count + 1];
        ops[0] = bp;
        idx.CopyTo(ops, 1);
        Direct(i, Op.AccessChain, PointerTo(sc, pe), ops);
        if (nonUniformChain)
        {
            MarkNonUniform(i);
            var element = ((PointerType)baseValue.Type).Pointee is ArrayType resourceElements ? resourceElements.Element : cur;
            _s.Capabilities.Add(5301); // ShaderNonUniform
            _s.Capabilities.Add(NonUniformIndexingCapability(element));
        }
    }

    private readonly HashSet<IrValue> _nonUniform = [];

    // The index may pass through a sign conversion on its way into the access chain.
    private static bool IsNonUniformIntrinsic(IrValue v) => v is Instruction { Op: Opcode.CallIntrinsic, Intrinsic: Intrinsic.NonUniformIndex };

    private static bool IsNonUniform(IrValue v)
    {
        while (v is Instruction { Op: Opcode.Bitcast or Opcode.Convert } c && c.Operands.Count > 0)
        {
            v = c.Operands[0];
        }

        return IsNonUniformIntrinsic(v);
    }

    /// <summary>Decorates a value derived from NonUniformResourceIndex so drivers do not assume the whole wave agrees on it.</summary>
    private void MarkNonUniform(IrValue v)
    {
        _nonUniform.Add(v);
        _s.Capabilities.Add(5301); // ShaderNonUniform
        _s.Decorate(Id(v), Dec.NonUniform);
    }

    /// <summary>Decorates a descriptor loaded through a non-uniform index (and the sampled image built from it).</summary>
    private uint MarkLoaded(IrValue pointer, uint loaded)
    {
        if (_nonUniform.Contains(pointer))
        {
            _s.Decorate(loaded, Dec.NonUniform);
        }

        return loaded;
    }

    // Vulkan: the shaderXxxArrayNonUniformIndexing features, one capability per descriptor type.
    private static uint NonUniformIndexingCapability(HlslType resource) => resource switch
    {
        ResourceType { ResourceKind: ResourceTypeKind.ConstantBuffer } => 5306,
        ResourceType { IsStorageBuffer: true } => 5308,
        ResourceType { ResourceKind: ResourceTypeKind.Buffer } => 5311,
        ResourceType { ResourceKind: ResourceTypeKind.RWBuffer } => 5312,
        ResourceType { IsReadWrite: true } => 5309,
        _ => 5307,
    };

    private static uint DynamicIndexingCapability(HlslType resource) => resource switch
    {
        ResourceType { ResourceKind: ResourceTypeKind.ConstantBuffer } => 28,
        ResourceType { IsStorageBuffer: true } => 30,
        ResourceType { ResourceKind: ResourceTypeKind.Buffer } => 5304,
        ResourceType { ResourceKind: ResourceTypeKind.RWBuffer } => 5305,
        ResourceType { IsReadWrite: true } => 31,
        _ => 29,
    };

    private void EmitExtractInsert(Instruction i)
    {
        bool insert = i.Op == Opcode.CompositeInsert;
        var cur = i.Operands[0].Type;
        var lits = new List<uint>();
        foreach (int imm in i.Immediates)
        {
            if (Step(ref cur, imm, i.Span))
            {
                lits.Add((uint)imm);
            }
        }

        if (insert)
        {
            if (lits.Count == 0)
            {
                Finish(i, Id(i.Operands[1]));
                return;
            }

            var ops = new uint[lits.Count + 2];
            ops[0] = Id(i.Operands[1]);
            ops[1] = Id(i.Operands[0]);
            lits.CopyTo(ops, 2);
            Direct(i, Op.CompositeInsert, TypeId(i.Type), ops);
            return;
        }

        if (lits.Count == 0)
        {
            Finish(i, Id(i.Operands[0]));
            return;
        }

        var eops = new uint[lits.Count + 1];
        eops[0] = Id(i.Operands[0]);
        lits.CopyTo(eops, 1);
        Direct(i, Op.CompositeExtract, TypeId(i.Type), eops);
    }

    private void EmitConstruct(Instruction i)
    {
        var t = i.Type;
        var ids = new uint[i.Operands.Count];
        for (int k = 0; k < ids.Length; k++)
        {
            ids[k] = Id(i.Operands[k]);
        }

        if (Count(t) == 1 && t is not (ArrayType or StructType))
        {
            Finish(i, ids[0]);
            return;
        }

        if (t is VectorType v && ids.Length == 1 && Count(i.Operands[0].Type) == 1)
        {
            var splat = new uint[v.Count];
            System.Array.Fill(splat, ids[0]);
            ids = splat;
        }
        else if (t is MatrixType { Rows: 1 })
        {
            Finish(i, ids[0]);
            return;
        }

        Direct(i, Op.CompositeConstruct, TypeId(t), ids);
    }

    private void EmitShuffle(Instruction i)
    {
        var a = i.Operands[0];
        var b = i.Operands[1];
        int na = Count(a.Type);
        int nb = Count(b.Type);
        int nr = i.Immediates.Count;
        uint ida = Id(a);
        uint idb = Id(b);
        if (na >= 2 && nb >= 2 && nr >= 2)
        {
            var ops = new uint[nr + 2];
            ops[0] = ida;
            ops[1] = idb;
            for (int k = 0; k < nr; k++)
            {
                ops[k + 2] = (uint)i.Immediates[k];
            }

            Direct(i, Op.VectorShuffle, TypeId(i.Type), ops);
            return;
        }

        var scalar = TypeId(i.Type.ScalarElement ?? ScalarType.Float);
        var parts = new uint[nr];
        for (int k = 0; k < nr; k++)
        {
            int comp = i.Immediates[k];
            bool fromA = comp < na;
            int local = fromA ? comp : comp - na;
            uint src = fromA ? ida : idb;
            int n = fromA ? na : nb;
            parts[k] = n == 1 ? src : Emit(Op.CompositeExtract, scalar, src, (uint)local);
        }

        if (nr == 1)
        {
            Finish(i, parts[0]);
        }
        else
        {
            Direct(i, Op.CompositeConstruct, TypeId(i.Type), parts);
        }
    }

    private void EmitMatMul(Instruction i)
    {
        var a = i.Operands[0].Type;
        var b = i.Operands[1].Type;
        uint ida = Id(i.Operands[0]);
        uint idb = Id(i.Operands[1]);
        uint rt = TypeId(i.Type);
        bool aMat = a is MatrixType am && !Degenerate(am);
        bool bMat = b is MatrixType bm && !Degenerate(bm);
        // HLSL matrices are transposed in SPIR-V, so every product swaps its operands.
        if (aMat && bMat)
        {
            Direct(i, Op.MatrixTimesMatrix, rt, idb, ida);
        }
        else if (a is VectorType && bMat)
        {
            Direct(i, Op.MatrixTimesVector, rt, idb, ida);
        }
        else if (aMat && b is VectorType)
        {
            Direct(i, Op.VectorTimesMatrix, rt, idb, ida);
        }
        else if (a is VectorType && b is VectorType)
        {
            Direct(i, Op.Dot, rt, ida, idb);
        }
        else
        {
            Unsupported("This matrix product involves a single-row or single-column matrix; use vectors or float2x2 and larger.", i.Span);
            Finish(i, Zero(i.Type));
        }
    }

    // ---- arithmetic -----------------------------------------------------------------------------

    private static uint ArithOp(Opcode op, NK k) => (op, k) switch
    {
        (Opcode.Add, NK.Float) => Op.FAdd,
        (Opcode.Sub, NK.Float) => Op.FSub,
        (Opcode.Mul, NK.Float) => Op.FMul,
        (Opcode.Div, NK.Float) => Op.FDiv,
        (Opcode.Rem, NK.Float) => Op.FRem,
        (Opcode.Add, _) => Op.IAdd,
        (Opcode.Sub, _) => Op.ISub,
        (Opcode.Mul, _) => Op.IMul,
        (Opcode.Div, NK.SInt) => Op.SDiv,
        (Opcode.Div, _) => Op.UDiv,
        (Opcode.Rem, NK.SInt) => Op.SRem,
        _ => Op.UMod,
    };

    private void EmitArith(Instruction i)
    {
        NK k = KindOf(i.Type);
        if (k is NK.Bool or NK.Other)
        {
            Unsupported("Arithmetic on " + i.Type + " is not supported.", i.Span);
            Finish(i, Zero(i.Type));
            return;
        }

        uint op = ArithOp(i.Op, k);
        if (i.Type is MatrixType m && !Degenerate(m))
        {
            uint rowType = TypeId(HlslType.Vector(m.Element, m.Columns));
            var rows = new uint[m.Rows];
            for (int r = 0; r < rows.Length; r++)
            {
                uint x = Emit(Op.CompositeExtract, rowType, Id(i.Operands[0]), (uint)r);
                uint y = Emit(Op.CompositeExtract, rowType, Id(i.Operands[1]), (uint)r);
                rows[r] = Emit(op, rowType, x, y);
            }

            Direct(i, Op.CompositeConstruct, TypeId(i.Type), rows);
            return;
        }

        Direct(i, op, TypeId(i.Type), Id(i.Operands[0]), Id(i.Operands[1]));
    }

    private void EmitNeg(Instruction i)
    {
        NK k = KindOf(i.Type);
        uint op = k == NK.Float ? Op.FNegate : Op.SNegate;
        if (i.Type is MatrixType m && !Degenerate(m))
        {
            uint rowType = TypeId(HlslType.Vector(m.Element, m.Columns));
            var rows = new uint[m.Rows];
            for (int r = 0; r < rows.Length; r++)
            {
                uint x = Emit(Op.CompositeExtract, rowType, Id(i.Operands[0]), (uint)r);
                rows[r] = Emit(op, rowType, x);
            }

            Direct(i, Op.CompositeConstruct, TypeId(i.Type), rows);
            return;
        }

        Direct(i, op, TypeId(i.Type), Id(i.Operands[0]));
    }

    /// <summary>HLSL masks shift counts to the operand width; SPIR-V leaves larger counts undefined.</summary>
    private uint ShiftAmount(IrValue count)
    {
        var scalar = count.Type.ScalarElement ?? ScalarType.UInt;
        ulong mask = (ulong)BitWidth(scalar) - 1;
        if (count is Constant { ConstantKind: ConstantKind.Scalar } c)
        {
            return ConstId(Constant.FromBits(scalar, c.Bits & mask));
        }

        var maskConst = Constant.FromBits(scalar, mask);
        if (count.Type is VectorType v)
        {
            var elems = new Constant[v.Count];
            System.Array.Fill(elems, maskConst);
            maskConst = Constant.Composite(count.Type, elems);
        }

        return Emit(Op.BitwiseAnd, TypeId(count.Type), Id(count), ConstId(maskConst));
    }

    private void EmitCompare(Instruction i)
    {
        var operandType = i.Operands[0].Type;
        NK k = KindOf(operandType);
        uint op;
        switch (k)
        {
            case NK.Float:
                op = i.Op switch
                {
                    Opcode.Eq => Op.FOrdEqual,
                    Opcode.Ne => Op.FUnordNotEqual, // HLSL: NaN != x is true.
                    Opcode.Lt => Op.FOrdLessThan,
                    Opcode.Le => Op.FOrdLessThanEqual,
                    Opcode.Gt => Op.FOrdGreaterThan,
                    _ => Op.FOrdGreaterThanEqual,
                };
                break;
            case NK.Bool:
                op = i.Op == Opcode.Eq ? Op.LogicalEqual : Op.LogicalNotEqual;
                if (i.Op is not (Opcode.Eq or Opcode.Ne))
                {
                    Unsupported("Ordering comparisons on bool are not supported.", i.Span);
                }

                break;
            case NK.SInt:
                op = i.Op switch
                {
                    Opcode.Eq => Op.IEqual,
                    Opcode.Ne => Op.INotEqual,
                    Opcode.Lt => Op.SLessThan,
                    Opcode.Le => Op.SLessThanEqual,
                    Opcode.Gt => Op.SGreaterThan,
                    _ => Op.SGreaterThanEqual,
                };
                break;
            case NK.UInt:
                op = i.Op switch
                {
                    Opcode.Eq => Op.IEqual,
                    Opcode.Ne => Op.INotEqual,
                    Opcode.Lt => Op.ULessThan,
                    Opcode.Le => Op.ULessThanEqual,
                    Opcode.Gt => Op.UGreaterThan,
                    _ => Op.UGreaterThanEqual,
                };
                break;
            default:
                Unsupported("Cannot compare values of type " + operandType + ".", i.Span);
                Finish(i, Zero(i.Type));
                return;
        }

        if (operandType is MatrixType m && !Degenerate(m))
        {
            Unsupported("Comparing whole matrices is not supported; compare rows or elements.", i.Span);
            Finish(i, Zero(i.Type));
            return;
        }

        Direct(i, op, TypeId(i.Type), Id(i.Operands[0]), Id(i.Operands[1]));
    }

    private void EmitBitcast(Instruction i)
    {
        uint want = TypeId(i.Type);
        uint v = Id(i.Operands[0]);
        if (TypeId(i.Operands[0].Type) == want)
        {
            Finish(i, v);
            return;
        }

        if (i.Type is MatrixType m && !Degenerate(m))
        {
            Unsupported("Reinterpreting a full matrix's bits is not supported.", i.Span);
            Finish(i, Zero(i.Type));
            return;
        }

        Direct(i, Op.Bitcast, want, v);
    }

    // ---- conversions ----------------------------------------------------------------------------

    private static Constant ScalarOf(ScalarType t, double value)
    {
        return t.ScalarKind switch
        {
            ScalarKind.Bool => Constant.Bool(value != 0),
            ScalarKind.Double => Constant.Double(value),
            _ when t.IsFloatingPoint => Constant.Float((float)value),
            _ => Constant.FromBits(t, (ulong)(long)value),
        };
    }

    private uint SplatConst(ScalarType t, double value, int n)
    {
        var c = ScalarOf(t, value);
        if (n == 1)
        {
            return ConstId(c);
        }

        var elems = new Constant[n];
        System.Array.Fill(elems, c);
        return ConstId(Constant.Composite(HlslType.Vector(t, n), elems));
    }

    private static HlslType Shape(ScalarType t, int n) => n == 1 ? t : HlslType.Vector(t, n);

    private uint ConvertValue(uint v, HlslType from, HlslType to)
    {
        if (TypeId(from) == TypeId(to))
        {
            return v;
        }

        var fs = from.ScalarElement;
        var ts = to.ScalarElement;
        if (fs is null || ts is null)
        {
            Unsupported("Cannot convert " + from + " to " + to + ".");
            return Zero(to);
        }

        if (from is MatrixType fm && to is MatrixType tm && !Degenerate(fm) && !Degenerate(tm) && fm.Rows == tm.Rows && fm.Columns == tm.Columns)
        {
            var rowFrom = HlslType.Vector(fs, fm.Columns);
            var rowTo = HlslType.Vector(ts, tm.Columns);
            var rows = new uint[fm.Rows];
            for (int r = 0; r < rows.Length; r++)
            {
                uint row = Emit(Op.CompositeExtract, TypeId(rowFrom), v, (uint)r);
                rows[r] = CastScalars(row, fs, ts, fm.Columns);
            }

            return Emit(Op.CompositeConstruct, TypeId(to), rows);
        }

        int fc = Count(from);
        int tc = Count(to);
        if (fc == tc)
        {
            return CastScalars(v, fs, ts, fc);
        }

        if (fc == 1)
        {
            uint s = CastScalars(v, fs, ts, 1);
            var parts = new uint[tc];
            System.Array.Fill(parts, s);
            return Emit(Op.CompositeConstruct, TypeId(to), parts);
        }

        if (tc < fc)
        {
            uint cut;
            if (tc == 1)
            {
                cut = Emit(Op.CompositeExtract, TypeId(fs), v, 0);
            }
            else
            {
                var ops = new uint[tc + 2];
                ops[0] = v;
                ops[1] = v;
                for (int k = 0; k < tc; k++)
                {
                    ops[k + 2] = (uint)k;
                }

                cut = Emit(Op.VectorShuffle, TypeId(Shape(fs, tc)), ops);
            }

            return CastScalars(cut, fs, ts, tc);
        }

        Unsupported("Cannot widen " + from + " to " + to + " with a conversion.");
        return Zero(to);
    }

    private uint CastScalars(uint v, ScalarType fs, ScalarType ts, int n)
    {
        uint fromT = TypeId(Shape(fs, n));
        uint toT = TypeId(Shape(ts, n));
        if (fromT == toT)
        {
            return v;
        }

        NK fk = KindOf(fs);
        NK tk = KindOf(ts);
        if (fk == NK.Bool)
        {
            return Emit(Op.Select, toT, v, SplatConst(ts, 1, n), SplatConst(ts, 0, n));
        }

        if (tk == NK.Bool)
        {
            return fk == NK.Float
                ? Emit(Op.FUnordNotEqual, toT, v, SplatConst(fs, 0, n))
                : Emit(Op.INotEqual, toT, v, SplatConst(fs, 0, n));
        }

        if (fk == NK.Float)
        {
            return tk switch
            {
                NK.Float => Emit(Op.FConvert, toT, v),
                NK.SInt => Emit(Op.ConvertFToS, toT, v),
                _ => Emit(Op.ConvertFToU, toT, v),
            };
        }

        if (tk == NK.Float)
        {
            return Emit(fk == NK.SInt ? Op.ConvertSToF : Op.ConvertUToF, toT, v);
        }

        if (BitWidth(fs) != BitWidth(ts))
        {
            return Emit(fk == NK.SInt ? Op.SConvert : Op.UConvert, toT, v);
        }

        return Emit(Op.Bitcast, toT, v);
    }

    // ---- atomics and barriers -----------------------------------------------------------------------

    private void EmitAtomic(Instruction i)
    {
        // A texture operand (a pointer to a resource) means the atomic works on one texel: texture, coordinate, then the values.
        bool texel = ResourceOf(i.Operands[0]) is not null;
        int first = texel ? 2 : 1;
        uint p;
        uint scope;
        if (texel)
        {
            uint imgPtr = Ptr(i.Operands[0], out _);
            uint texelPtr = Emit(Op.ImageTexelPointer, PointerTo(Sc.Image, TypeId(i.Type)), imgPtr, Id(i.Operands[1]), UIntConst(0));
            p = texelPtr;
            scope = UIntConst(1u);
        }
        else
        {
            p = Ptr(i.Operands[0], out _);
            scope = UIntConst(((PointerType)i.Operands[0].Type).Space == AddressSpace.Workgroup ? 2u : 1u);
        }

        uint none = UIntConst(0);
        if (i.Op == Opcode.AtomicCompareExchange)
        {
            // SPIR-V takes the new value before the comparand; the IR keeps HLSL's order (comparand, value).
            Direct(i, Op.AtomicCompareExchange, TypeId(i.Type), p, scope, none, none, Id(i.Operands[first + 1]), Id(i.Operands[first]));
            return;
        }

        bool signed = KindOf(i.Operands[first].Type) == NK.SInt;
        uint op = (AtomicOp)i.Immediates[0] switch
        {
            AtomicOp.Add => Op.AtomicIAdd,
            AtomicOp.Min => signed ? Op.AtomicSMin : Op.AtomicUMin,
            AtomicOp.Max => signed ? Op.AtomicSMax : Op.AtomicUMax,
            AtomicOp.And => Op.AtomicAnd,
            AtomicOp.Or => Op.AtomicOr,
            AtomicOp.Xor => Op.AtomicXor,
            _ => Op.AtomicExchange,
        };
        Direct(i, op, TypeId(i.Type), p, scope, none, Id(i.Operands[first]));
    }

    private void EmitBarrier(Instruction i)
    {
        var flags = (BarrierFlags)i.Immediates[0];
        uint semantics = 0;
        if (flags.HasFlag(BarrierFlags.GroupMemory))
        {
            semantics |= 0x8 | 0x100; // AcquireRelease | WorkgroupMemory
        }

        if (flags.HasFlag(BarrierFlags.DeviceMemory))
        {
            semantics |= 0x8 | 0x40 | 0x800; // AcquireRelease | UniformMemory (buffers) | ImageMemory
        }

        bool device = flags.HasFlag(BarrierFlags.DeviceMemory);
        uint memScope = UIntConst(device ? 1u : 2u);
        if (flags.HasFlag(BarrierFlags.GroupSync))
        {
            SpirvModule.Put(_s.Code, Op.ControlBarrier, UIntConst(2), memScope, UIntConst(semantics));
        }
        else if (semantics != 0)
        {
            SpirvModule.Put(_s.Code, Op.MemoryBarrier, memScope, UIntConst(semantics));
        }
    }
}
