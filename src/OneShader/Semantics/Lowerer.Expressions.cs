using System;
using System.Collections.Generic;
using System.Globalization;
using OneShader.Diagnostics;
using OneShader.IR;
using OneShader.Syntax;
using OneShader.Types;

namespace OneShader.Semantics;

internal sealed partial class Lowerer
{
    private static Constant ConstOf(ConstValue cv, ScalarType t) => Constant.FromBits(t, cv.ConvertTo(t).Bits());

    private static ConstValue? ValueOf(IrValue v)
    {
        if (v is not Constant { ConstantKind: ConstantKind.Scalar } c || c.Type is not ScalarType t)
        {
            return null;
        }

        if (t.IsFloatingPoint)
        {
            return ConstValue.FromFloat(t, c.AsDouble());
        }

        long i = t.ScalarKind switch
        {
            ScalarKind.Int or ScalarKind.LiteralInt => (int)c.Bits,
            ScalarKind.Int16 or ScalarKind.Min16Int => (short)c.Bits,
            ScalarKind.UInt or ScalarKind.UInt16 or ScalarKind.Min16UInt => (long)c.Bits,
            ScalarKind.Bool => c.Bits != 0 ? 1 : 0,
            _ => unchecked((long)c.Bits),
        };
        return ConstValue.FromInt(t, i);
    }

    private IrValue ConvertScalar(IrValue v, ScalarType to)
    {
        if (to.IsRelaxed && to.ScalarKind is ScalarKind.Int or ScalarKind.UInt && v is Constant { ConstantKind: ConstantKind.Scalar, Type: ScalarType { IsInteger: true } } wide)
        {
            // A constant that becomes a min16int / min16uint is cut to 16 bits, though the arithmetic on it stays 32-bit (what DXC does).
            long cut = to.ScalarKind == ScalarKind.Int ? (short)wide.Bits : (ushort)wide.Bits;
            return Constant.FromBits(to, to.ScalarKind == ScalarKind.Int ? (uint)(int)cut : (uint)cut);
        }

        if (v.Type.Equals(to))
        {
            return v;
        }

        if (v is Constant exactSource && to.IsFloatingPoint && _exactLiterals.TryGetValue(exactSource, out double exact))
        {
            return ConstOf(new ConstValue(ScalarType.Float, 0, exact), to);
        }

        if (ValueOf(v) is { } cv)
        {
            return ConstOf(cv, to);
        }

        return _b.Convert(v, to);
    }

    private IrValue Extract(IrValue v, HlslType result, params int[] idx) => _b.Extract(v, result, idx);

    private IrValue Splat(IrValue scalar, HlslType to)
    {
        switch (to)
        {
            case VectorType v:
                return _b.Construct(v, ConvertScalar(scalar, v.Element));
            case MatrixType m:
            {
                var row = HlslType.Vector(m.Element, m.Columns);
                var r = m.Columns == 1 ? ConvertScalar(scalar, m.Element) : _b.Construct(row, ConvertScalar(scalar, m.Element));
                var rows = new IrValue[m.Rows];
                Array.Fill(rows, r);
                return _b.Construct(m, rows);
            }

            default:
                return ConvertScalar(scalar, (ScalarType)to);
        }
    }

    private void FlattenScalars(IrValue v, List<IrValue> into)
    {
        switch (v.Type)
        {
            case ScalarType:
                into.Add(v);
                break;
            case VectorType vt:
                for (int i = 0; i < vt.Count; i++)
                {
                    into.Add(Extract(v, vt.Element, i));
                }

                break;
            case MatrixType mt:
                for (int r = 0; r < mt.Rows; r++)
                {
                    for (int c = 0; c < mt.Columns; c++)
                    {
                        into.Add(Extract(v, mt.Element, r, c));
                    }
                }

                break;
        }
    }

    private IrValue Rebuild(List<IrValue> scalars, HlslType to)
    {
        switch (to)
        {
            case ScalarType s:
                return ConvertScalar(scalars[0], s);
            case VectorType v:
            {
                var parts = new IrValue[v.Count];
                for (int i = 0; i < parts.Length; i++)
                {
                    parts[i] = ConvertScalar(scalars[i], v.Element);
                }

                return _b.Construct(v, parts);
            }

            case MatrixType m:
            {
                var rows = new IrValue[m.Rows];
                var rowType = HlslType.Vector(m.Element, m.Columns);
                for (int r = 0; r < m.Rows; r++)
                {
                    var parts = new IrValue[m.Columns];
                    for (int c = 0; c < m.Columns; c++)
                    {
                        parts[c] = ConvertScalar(scalars[r * m.Columns + c], m.Element);
                    }

                    rows[r] = m.Columns == 1 ? parts[0] : _b.Construct(rowType, parts);
                }

                return _b.Construct(m, rows);
            }

            default:
                return scalars[0];
        }
    }

    /// <summary>Converts a value to another type with HLSL's implicit/explicit numeric conversion rules.</summary>
    private IrValue Coerce(IrValue v, HlslType to)
    {
        var from = v.Type;
        if (to is ScalarType { IsRelaxed: true } relaxedTarget && from is ScalarType)
        {
            return ConvertScalar(v, relaxedTarget);
        }

        if (from.Equals(to) || to.IsError || from.IsError)
        {
            return v;
        }

        if (from is StructType derived && to is StructType && derived.IsDerivedFrom((StructType)to))
        {
            // Passing a derived struct where its base is expected copies the base part, which is the first member.
            IrValue cur = v;
            for (var s = derived; s.BaseStruct is { } bs; s = bs)
            {
                cur = Extract(cur, bs, 0);
                if (bs.Equals(to))
                {
                    break;
                }
            }

            return cur;
        }

        var fs = from.ScalarElement;
        var ts = to.ScalarElement;
        if (fs is null || ts is null)
        {
            return v;
        }

        switch (to)
        {
            case ScalarType t:
            {
                if (from is ScalarType)
                {
                    return ConvertScalar(v, t);
                }

                var scalars = new List<IrValue>();
                if (from is VectorType)
                {
                    scalars.Add(Extract(v, fs, 0));
                }
                else
                {
                    scalars.Add(Extract(v, fs, 0, 0));
                }

                return ConvertScalar(scalars[0], t);
            }

            case VectorType tv:
            {
                if (from is ScalarType)
                {
                    return Splat(v, to);
                }

                if (from is VectorType fv)
                {
                    IrValue w = v;
                    var mid = (HlslType)fv;
                    if (fv.Count != tv.Count)
                    {
                        mid = HlslType.Vector(fv.Element, tv.Count);
                        if (tv.Count == 1)
                        {
                            w = Extract(v, fv.Element, 0);
                            mid = fv.Element;
                        }
                        else
                        {
                            var comps = new int[tv.Count];
                            for (int i = 0; i < comps.Length; i++)
                            {
                                comps[i] = i;
                            }

                            w = _b.Shuffle(v, v, mid, comps);
                        }
                    }

                    return mid.Equals(to) ? w : ConvertVector(w, to);
                }

                var flat = new List<IrValue>();
                FlattenScalars(v, flat);
                return Rebuild(flat, to);
            }

            default:
            {
                var tm = (MatrixType)to;
                if (from is ScalarType)
                {
                    return Splat(v, to);
                }

                if (from is MatrixType fm && fm.Rows == tm.Rows && fm.Columns == tm.Columns)
                {
                    return ConvertVector(v, to);
                }

                var flat = new List<IrValue>();
                if (from is MatrixType fm2)
                {
                    for (int r = 0; r < tm.Rows; r++)
                    {
                        for (int c = 0; c < tm.Columns; c++)
                        {
                            flat.Add(Extract(v, fm2.Element, r, c));
                        }
                    }
                }
                else
                {
                    FlattenScalars(v, flat);
                }

                return Rebuild(flat, to);
            }
        }
    }

    private IrValue ConvertVector(IrValue v, HlslType to) => v.Type.Equals(to) ? v : _b.Convert(v, to);

    // ------------------------------------------------------------ addresses

    private IrValue? VarPointer(VariableSymbol v)
    {
        switch (v.Kind)
        {
            case VarKind.CBufferMember:
            {
                var g = v.Buffer!.Resource!.Global!;
                return _b.AccessChain(g, v.Type, Constant.Int(v.MemberIndex));
            }

            case VarKind.Resource:
                return v.Resource?.Global ?? v.Ir;
            case VarKind.Spec:
                return null;
            default:
                return v.Ir;
        }
    }

    /// <summary>The pointer to a resource variable or an element of a resource array.</summary>
    private IrValue? ResourcePointer(Expression e)
    {
        e = UnwrapParen(e);
        switch (e)
        {
            case ParenthesizedExpression p:
                return ResourcePointer(p.Inner);
            case NameExpression { Info.Symbol: VariableSymbol v }:
            {
                var vp = VarPointer(v);
                if (vp is null && v.Kind == VarKind.Local)
                {
                    _diag.Error(SemanticCodes.BadResource, "The resource variable '" + v.Name + "' is used before it is assigned a resource. Initialize it where it is declared, or assign it on every path before this use.", e.Span);
                    // A stand-in of the right type, so the code that follows lowers without a second, confusing failure.
                    return _b.Alloca(v.Type, v.Name);
                }

                return vp;
            }

            case CastExpression { Operand.Type: DescriptorHeapType } cast:
                return ResourcePointer(cast.Operand);
            case IndexExpression { Info.HeapDecl: { Global: { } heapGlobal } heapDecl } heapIndex:
                // ResourceDescriptorHeap[i] read as a resource type: element i of the unbounded array for that type.
                return _b.AccessChain(heapGlobal, ((ArrayType)heapDecl.Type).Element, Coerce(Rv(heapIndex.Index), ScalarType.Int));
            case IndexExpression ix when ix.Target.Type is ArrayType at:
            {
                var basePtr = ResourcePointer(ix.Target);
                if (basePtr is null)
                {
                    return null;
                }

                return _b.AccessChain(basePtr, at.Element, Coerce(Rv(ix.Index), ScalarType.Int));
            }

            default:
                return null;
        }
    }

    private IrValue? Address(Expression e)
    {
        e = UnwrapParen(e);
        switch (e)
        {
            case ThisExpression { Info.Symbol: VariableSymbol thisVar }:
                return VarPointer(thisVar);
            case NameExpression { Info.Symbol: VariableSymbol v } when v.Kind != VarKind.Resource:
                return VarPointer(v);
            case MemberAccessExpression m:
                return AddressOfMember(m);
            case IndexExpression ix:
                return AddressOfIndex(ix);
            default:
                return null;
        }
    }

    private IrValue? AddressOfMember(MemberAccessExpression m)
    {
        var tt = m.Target.Type;
        if (m.SwizzleComponents is { } sw)
        {
            if (sw.Count != 1 || tt is ScalarType)
            {
                return null;
            }

            var bp = Address(m.Target);
            if (bp is null)
            {
                return null;
            }

            if (tt is MatrixType mt)
            {
                return _b.AccessChain(bp, mt.Element, Constant.Int(sw[0] / 4), Constant.Int(sw[0] % 4));
            }

            return _b.AccessChain(bp, ((VectorType)tt!).Element, Constant.Int(sw[0]));
        }

        int idx = m.Info?.FieldIndex ?? -1;
        if (idx < 0)
        {
            return null;
        }

        IrValue? basePtr = tt is ResourceType ? ResourcePointer(m.Target) : Address(m.Target);
        if (basePtr is null)
        {
            return null;
        }

        return _b.AccessChain(basePtr, m.Type!, Constant.Int(idx));
    }

    private IrValue? AddressOfIndex(IndexExpression ix)
    {
        var tt = ix.Target.Type;
        if (tt is ResourceType rt)
        {
            if (rt.ResourceKind is ResourceTypeKind.StructuredBuffer or ResourceTypeKind.RWStructuredBuffer)
            {
                var bp = ResourcePointer(ix.Target);
                return bp is null ? null : _b.AccessChain(bp, rt.Element!, Coerce(Rv(ix.Index), ScalarType.Int));
            }

            return null;
        }

        var elem = TypeOps.IndexedElement(tt!);
        var basePtr = Address(ix.Target);
        if (basePtr is null || elem is null)
        {
            return null;
        }

        return _b.AccessChain(basePtr, elem, IndexValue(ix.Index));
    }

    private IrValue IndexValue(Expression index)
    {
        var v = Rv(index);
        if (v.Type is ScalarType { IsInteger: true } s && TypeOps.BitWidth(s) == 32)
        {
            return v;
        }

        return Coerce(v, ScalarType.Int);
    }

    // ------------------------------------------------------------ rvalues

    private IrValue Rv(Expression e)
    {
        if (e.Info?.Rewritten is { } rewritten)
        {
            return Rv(rewritten);
        }

        if (e.Info?.Const is { } cv && e.Type is ScalarType st)
        {
            var constant = ConstOf(cv, st);
            if (st.ScalarKind == ScalarKind.Float && cv.Float != (double)(float)cv.Float && !double.IsNaN(cv.Float))
            {
                // An unsuffixed literal such as 0.1 only becomes a float if nothing wider asks for it first.
                _exactLiterals[constant] = cv.Float;
            }

            return constant;
        }

        var saved = _b.Span;
        _b.Span = e.Span;
        if (++_depth > 1024)
        {
            _depth--;
            Fail("The expression is nested too deeply to compile.", e.Span);
            return Constant.Zero(e.Type ?? ScalarType.Int);
        }

        try
        {
            return RvCore(e);
        }
        finally
        {
            _depth--;
            _b.Span = saved;
        }
    }

    private IrValue RvCore(Expression e)
    {
        switch (e)
        {
            case ParenthesizedExpression p:
                return Rv(p.Inner);
            case ThisExpression { Info.Symbol: VariableSymbol thisVar }:
                return _b.Load(VarPointer(thisVar)!);
            case NameExpression n:
            {
                if (n.Info?.Symbol is VariableSymbol { Kind: VarKind.Spec, Ir: IrValue specValue })
                {
                    return specValue;
                }

                if (n.Info?.Symbol is VariableSymbol v)
                {
                    var ptr = VarPointer(v);
                    if (ptr is null)
                    {
                        if (_inStaticInit)
                        {
                            _diag.Error(SemanticCodes.BadVariable, "The initializer of a static variable cannot use '" + v.Name + "': it runs once at shader start, before any local variable or parameter exists. Use constants and globals, or assign the value in the function.", e.Span);
                            return Constant.Zero(e.Type!);
                        }

                        Fail("The variable '" + v.Name + "' has no storage here.", e.Span);
                        return Constant.Zero(e.Type!);
                    }

                    if (v.Type is ResourceType { ResourceKind: ResourceTypeKind.ConstantBuffer, Element: StructType contents })
                    {
                        // Naming a ConstantBuffer<S> as a value copies the whole struct (S s = cb;). A chain with no index
                        // addresses the buffer's contents, which is how member accesses start.
                        return _b.Load(_b.AccessChain(ptr, contents));
                    }

                    return _b.Load(ptr);
                }

                Fail("Cannot use '" + n.Name + "' as a value.", e.Span);
                return Constant.Zero(e.Type!);
            }

            case UnaryExpression u:
                return RvUnary(u);
            case BinaryExpression b:
                return RvBinary(b);
            case AssignmentExpression a:
                return RvAssign(a);
            case ConditionalExpression c:
                return RvConditional(c);
            case CommaExpression c:
                Rv(c.Left);
                return Rv(c.Right);
            case CallExpression call:
                return RvCall(call);
            case ConstructExpression ce:
                return RvConstruct(ce.Info!.TargetType!, ce.Arguments);
            case CastExpression ca:
                return Coerce(Rv(ca.Operand), ca.Info!.TargetType!);
            case MemberAccessExpression m:
                return RvMember(m);
            case IndexExpression ix:
                return RvIndex(ix);
            default:
                Fail("This expression cannot be compiled.", e.Span);
                return Constant.Zero(e.Type ?? ScalarType.Int);
        }
    }

    private static Opcode ArithOp(BinaryOperator op) => op switch
    {
        BinaryOperator.Add => Opcode.Add,
        BinaryOperator.Subtract => Opcode.Sub,
        BinaryOperator.Multiply => Opcode.Mul,
        BinaryOperator.Divide => Opcode.Div,
        BinaryOperator.Modulo => Opcode.Rem,
        BinaryOperator.ShiftLeft => Opcode.Shl,
        BinaryOperator.ShiftRight => Opcode.Shr,
        BinaryOperator.BitwiseAnd => Opcode.BitAnd,
        BinaryOperator.BitwiseOr => Opcode.BitOr,
        BinaryOperator.BitwiseXor => Opcode.BitXor,
        BinaryOperator.LogicalAnd => Opcode.LogicalAnd,
        BinaryOperator.LogicalOr => Opcode.LogicalOr,
        BinaryOperator.Equal => Opcode.Eq,
        BinaryOperator.NotEqual => Opcode.Ne,
        BinaryOperator.Less => Opcode.Lt,
        BinaryOperator.LessEqual => Opcode.Le,
        BinaryOperator.Greater => Opcode.Gt,
        _ => Opcode.Ge,
    };

    private IrValue BinaryOp(BinaryOperator op, IrValue l, IrValue r, HlslType operand)
    {
        var o = ArithOp(op);
        if (op is BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.Less or BinaryOperator.LessEqual or BinaryOperator.Greater or BinaryOperator.GreaterEqual)
        {
            return _b.Compare(o, l, r);
        }

        // The result takes the checker's operand type, not the left value's: a min-precision value mixed with a plain one is plain.
        return operand.Equals(l.Type) ? _b.Emit(o, operand, l, r) : _b.Binary(o, l, r);
    }

    private IrValue RvBinary(BinaryExpression b)
    {
        var info = b.Info!;
        if (info.Function is not null)
        {
            return CallUser(b.Span, info.Function, info.ThisArg, info.Args ?? [b.Right]);
        }

        var operand = info.OperandType!;
        if (b.Operator is BinaryOperator.LogicalAnd or BinaryOperator.LogicalOr && operand is ScalarType && !IsPure(b.Right))
        {
            return ShortCircuit(b);
        }

        var l = Coerce(Rv(b.Left), operand);
        HlslType rtype = operand;
        var r = Coerce(Rv(b.Right), rtype);
        return BinaryOp(b.Operator, l, r, operand);
    }

    private IrValue ShortCircuit(BinaryExpression b)
    {
        bool isAnd = b.Operator == BinaryOperator.LogicalAnd;
        var l = Coerce(Rv(b.Left), ScalarType.Bool);
        var lhsBlock = _b.Block!;
        var rhsBlock = _f.AddBlock(isAnd ? "and.rhs" : "or.rhs");
        var merge = _f.AddBlock(isAnd ? "and.end" : "or.end");
        if (isAnd)
        {
            CondBr(l, rhsBlock, merge);
        }
        else
        {
            CondBr(l, merge, rhsBlock);
        }

        IrBuilder.MarkSelection(lhsBlock, merge);
        _b.Block = rhsBlock;
        var r = Coerce(Rv(b.Right), ScalarType.Bool);
        var rhsEnd = _b.Block!;
        Br(merge);
        _b.Block = merge;
        return _b.Phi(merge, ScalarType.Bool, [new(lhsBlock, Constant.Bool(!isAnd)), new(rhsEnd, r)]);
    }

    private IrValue RvUnary(UnaryExpression u)
    {
        var info = u.Info!;
        switch (u.Operator)
        {
            case UnaryOperator.Plus:
                return Coerce(Rv(u.Operand), info.OperandType!);
            case UnaryOperator.Minus:
                return _b.Unary(Opcode.Neg, Coerce(Rv(u.Operand), info.OperandType!));
            case UnaryOperator.LogicalNot:
                return _b.Unary(Opcode.LogicalNot, Coerce(Rv(u.Operand), info.OperandType!));
            case UnaryOperator.BitwiseNot:
                return _b.Unary(Opcode.BitNot, Coerce(Rv(u.Operand), info.OperandType!));
            default:
            {
                var ptr = Address(u.Operand);
                var t = u.Operand.Type!;
                if (ptr is null)
                {
                    Fail("Cannot modify this expression.", u.Span);
                    return Constant.Zero(t);
                }

                var old = _b.Load(ptr);
                var one = Splat(Constant.Int(1), t);
                bool inc = u.Operator is UnaryOperator.PreIncrement or UnaryOperator.PostIncrement;
                var nv = _b.Binary(inc ? Opcode.Add : Opcode.Sub, old, one);
                _b.Store(ptr, nv);
                return u.Operator is UnaryOperator.PreIncrement or UnaryOperator.PreDecrement ? nv : old;
            }
        }
    }

    private IrValue RvAssign(AssignmentExpression a)
    {
        var tt = a.Target.Type!;
        if (tt is ResourceType or SamplerType && UnwrapParen(a.Target) is NameExpression { Info.Symbol: VariableSymbol { Kind: VarKind.Local } resourceVar })
        {
            BindResourceLocal(resourceVar, a.Value, a.Span);
            return Constant.Zero(ScalarType.Int);
        }

        IrValue value;
        // Texture writes: tex[coord] = value.
        if (UnwrapParen(a.Target) is IndexExpression { Target.Type: ResourceType { IsTexture: true } rt } tix)
        {
            var tex = ResourcePointer(tix.Target)!;
            int dims = tix.Index.Type is VectorType iv ? iv.Count : 1;
            var coord = Coerce(Rv(tix.Index), dims == 1 ? ScalarType.UInt : HlslType.Vector(ScalarType.UInt, dims));
            IrValue nv;
            if (a.Operator is { } cop)
            {
                var cur = _b.Emit(Opcode.TextureLoad, rt.Element!, tex, coord);
                nv = Coerce(BinaryOp(cop, Coerce(cur, a.Info!.OperandType!), Coerce(Rv(a.Value), a.Info.OperandType!), a.Info.OperandType!), tt);
            }
            else
            {
                nv = Coerce(Rv(a.Value), tt);
            }

            _b.Emit(Opcode.TextureStore, HlslType.Void, tex, coord, nv);
            return nv;
        }

        bool savedPrecise = _b.Precise;
        bool precise = UnwrapParen(a.Target) is NameExpression { Info.Symbol: VariableSymbol { } pv } && (pv.Mods & Modifiers.Precise) != 0;
        if (a.Operator is null)
        {
            _b.Precise = precise || savedPrecise;
            value = Coerce(Rv(a.Value), tt);
            _b.Precise = savedPrecise;
            Store(a.Target, value);
            return value;
        }

        var op = a.Operator.Value;
        var operand = a.Info!.OperandType!;
        var cur2 = Rv(a.Target);
        _b.Precise = precise || savedPrecise;
        var rhs = Coerce(Rv(a.Value), operand);
        var res = Coerce(BinaryOp(op, Coerce(cur2, operand), rhs, operand), tt);
        _b.Precise = savedPrecise;
        Store(a.Target, res);
        return res;
    }

    private static Expression UnwrapParen(Expression e)
    {
        while (true)
        {
            if (e is ParenthesizedExpression p)
            {
                e = p.Inner;
            }
            else if (e.Info?.Rewritten is { } r)
            {
                e = r;
            }
            else
            {
                return e;
            }
        }
    }

    private void Store(Expression target, IrValue value)
    {
        target = UnwrapParen(target);
        if (target is MemberAccessExpression { SwizzleComponents: { } sw } m && (sw.Count > 1 || m.Target.Type is MatrixType))
        {
            var bp = Address(m.Target);
            if (bp is null)
            {
                Fail("Cannot assign to this swizzle.", target.Span);
                return;
            }

            for (int i = 0; i < sw.Count; i++)
            {
                IrValue comp = sw.Count == 1 ? value : Extract(value, value.Type.ScalarElement!, i);
                IrValue ptr = m.Target.Type is MatrixType mt
                    ? _b.AccessChain(bp, mt.Element, Constant.Int(sw[i] / 4), Constant.Int(sw[i] % 4))
                    : _b.AccessChain(bp, ((VectorType)m.Target.Type!).Element, Constant.Int(sw[i]));
                _b.Store(ptr, comp);
            }

            return;
        }

        if (target is MemberAccessExpression { SwizzleComponents.Count: 1, Target.Type: ScalarType } scalarSwizzle)
        {
            // 'x.x = v' on a scalar: the swizzle names the scalar itself.
            Store(scalarSwizzle.Target, value);
            return;
        }

        var p = Address(target);
        if (p is null)
        {
            Fail("Cannot assign to this expression.", target.Span);
            return;
        }

        _b.Store(p, value);
    }

    private IrValue RvConditional(ConditionalExpression c)
    {
        var rtype = c.Info!.OperandType!;
        var cond = Rv(c.Condition);
        if (cond.Type is VectorType)
        {
            cond = Coerce(cond, TypeOps.Reshape(cond.Type, ScalarType.Bool));
            return _b.Select(cond, Coerce(Rv(c.WhenTrue), rtype), Coerce(Rv(c.WhenFalse), rtype));
        }

        cond = Coerce(cond, ScalarType.Bool);
        if (IsPure(c.WhenTrue) && IsPure(c.WhenFalse))
        {
            var a = Coerce(Rv(c.WhenTrue), rtype);
            var b2 = Coerce(Rv(c.WhenFalse), rtype);
            if (rtype is VectorType or MatrixType)
            {
                // Selects need a condition of the same shape for vectors; branch on the scalar instead.
                return BranchSelect(cond, c, rtype, a, b2);
            }

            return _b.Select(cond, a, b2);
        }

        var hdr = _b.Block!;
        var tb = _f.AddBlock("cond.true");
        var fb = _f.AddBlock("cond.false");
        var merge = _f.AddBlock("cond.end");
        CondBr(cond, tb, fb);
        IrBuilder.MarkSelection(hdr, merge);
        _b.Block = tb;
        var tv = Coerce(Rv(c.WhenTrue), rtype);
        var tEnd = _b.Block!;
        Br(merge);
        _b.Block = fb;
        var fv = Coerce(Rv(c.WhenFalse), rtype);
        var fEnd = _b.Block!;
        Br(merge);
        _b.Block = merge;
        return _b.Phi(merge, rtype, [new(tEnd, tv), new(fEnd, fv)]);
    }

    private IrValue BranchSelect(IrValue cond, ConditionalExpression c, HlslType rtype, IrValue a, IrValue b)
    {
        if (rtype is VectorType vt && vt.Count > 1)
        {
            var bcond = _b.Construct(HlslType.Vector(ScalarType.Bool, vt.Count), cond);
            return _b.Select(bcond, a, b);
        }

        if (rtype is not MatrixType m)
        {
            // A scalar or a one-component vector.
            return _b.Select(cond, a, b);
        }

        // Matrices: select row by row.
        var rows = new IrValue[m.Rows];
        var rowType = HlslType.Vector(m.Element, m.Columns);
        for (int r = 0; r < m.Rows; r++)
        {
            var ar = Extract(a, rowType, r);
            var br = Extract(b, rowType, r);
            rows[r] = _b.Select(m.Columns == 1 ? cond : _b.Construct(HlslType.Vector(ScalarType.Bool, m.Columns), cond), ar, br);
        }

        _ = c;
        return _b.Construct(m, rows);
    }

    private static bool IsPure(Expression e)
    {
        e = UnwrapParen(e);
        switch (e)
        {
            case LiteralExpression or NameExpression:
                return true;
            case ParenthesizedExpression p:
                return IsPure(p.Inner);
            case UnaryExpression u:
                return u.Operator is UnaryOperator.Plus or UnaryOperator.Minus or UnaryOperator.LogicalNot or UnaryOperator.BitwiseNot && IsPure(u.Operand);
            case BinaryExpression b:
                return IsPure(b.Left) && IsPure(b.Right);
            case ConditionalExpression c:
                return IsPure(c.Condition) && IsPure(c.WhenTrue) && IsPure(c.WhenFalse);
            case CastExpression ca:
                return IsPure(ca.Operand);
            case ConstructExpression ce:
                foreach (var a in ce.Arguments)
                {
                    if (!IsPure(a))
                    {
                        return false;
                    }
                }

                return true;
            case MemberAccessExpression m:
                return IsPure(m.Target);
            case IndexExpression ix:
                return ix.Target.Type is not ResourceType && IsPure(ix.Target) && IsPure(ix.Index);
            case CallExpression c:
            {
                var bi = c.Info?.Builtin;
                if (bi is null || bi.Kind is BuiltinKind.Texture or BuiltinKind.GetDimensions or BuiltinKind.Clip or BuiltinKind.BufferLoad or BuiltinKind.Append or BuiltinKind.Consume)
                {
                    return false;
                }

                // Derivatives and wave operations depend on which invocations run them, so they must not be skipped or duplicated.
                if (bi.Kind == BuiltinKind.Intrinsic && (bi.Ir is >= Intrinsic.Ddx and <= Intrinsic.QuadReadLaneAt or Intrinsic.WaveActiveAllEqual))
                {
                    return false;
                }

                foreach (var a in c.Arguments)
                {
                    if (!IsPure(a))
                    {
                        return false;
                    }
                }

                return true;
            }

            default:
                return false;
        }
    }

    private IrValue RvMember(MemberAccessExpression m)
    {
        var info = m.Info!;
        if (m.SwizzleComponents is { } sw)
        {
            var tt = m.Target.Type!;
            var v = Rv(m.Target);
            var res = m.Type!;
            if (tt is ScalarType)
            {
                return sw.Count == 1 ? v : _b.Construct(res, v);
            }

            if (tt is MatrixType mt)
            {
                if (sw.Count == 1)
                {
                    return Extract(v, mt.Element, sw[0] / 4, sw[0] % 4);
                }

                var parts = new IrValue[sw.Count];
                for (int i = 0; i < parts.Length; i++)
                {
                    parts[i] = Extract(v, mt.Element, sw[i] / 4, sw[i] % 4);
                }

                return _b.Construct(res, parts);
            }

            if (sw.Count == 1)
            {
                return Extract(v, res, sw[0]);
            }

            return _b.Shuffle(v, v, res, [.. sw]);
        }

        var addr = AddressOfMember(m);
        if (addr is not null)
        {
            return _b.Load(addr);
        }

        var sv = Rv(m.Target);
        return Extract(sv, m.Type!, info.FieldIndex);
    }

    private IrValue RvIndex(IndexExpression ix)
    {
        if (ix.Info?.Function is not null)
        {
            return CallUser(ix.Span, ix.Info.Function, ix.Info.ThisArg, ix.Info.Args ?? [ix.Index]);
        }

        var tt = ix.Target.Type!;
        if (tt is ResourceType rt)
        {
            if (rt.ResourceKind is ResourceTypeKind.StructuredBuffer or ResourceTypeKind.RWStructuredBuffer)
            {
                return _b.Load(AddressOfIndex(ix)!);
            }

            var tex = ResourcePointer(ix.Target)!;
            int dims = ix.Index.Type is VectorType iv ? iv.Count : 1;
            var coord = Coerce(Rv(ix.Index), dims == 1 ? ScalarType.Int : HlslType.Vector(ScalarType.Int, dims));
            if (rt.IsReadWrite || rt.ResourceKind == ResourceTypeKind.Buffer)
            {
                return _b.Emit(Opcode.TextureLoad, rt.Element!, tex, coord);
            }

            return _b.Emit(Opcode.TextureLoad, rt.Element!, tex, coord, Constant.Int(0));
        }

        var addr = AddressOfIndex(ix);
        if (addr is not null)
        {
            return _b.Load(addr);
        }

        var elem = ix.Type!;
        var v = Rv(ix.Target);
        var idx = IndexValue(ix.Index);
        if (idx is Constant { ConstantKind: ConstantKind.Scalar } ci)
        {
            return Extract(v, elem, (int)ValueOf(ci)!.Value.Int);
        }

        if (tt is VectorType)
        {
            return _b.Emit(Opcode.VectorExtractDynamic, elem, v, idx);
        }

        // Dynamic index into a value: spill to a temporary.
        var tmp = _b.Alloca(tt, "tmp");
        _b.Store(tmp, v);
        return _b.Load(_b.AccessChain(tmp, elem, idx));
    }

    // ------------------------------------------------------------ construction and initializers

    private IrValue RvConstruct(HlslType target, IReadOnlyList<Expression> args)
    {
        if (target is StructType st)
        {
            var fields = new IrValue[st.Fields.Count];
            for (int i = 0; i < fields.Length; i++)
            {
                fields[i] = Coerce(Rv(args[i]), st.Fields[i].Type);
            }

            return _b.Construct(st, fields);
        }

        if (args.Count == 1)
        {
            return Coerce(Rv(args[0]), target);
        }

        var scalars = new List<IrValue>();
        foreach (var a in args)
        {
            FlattenScalars(Rv(a), scalars);
        }

        return Rebuild(scalars, target);
    }

    private IrValue LowerInitializer(Expression init, HlslType target)
    {
        if (init is InitializerListExpression il)
        {
            return LowerInitList(il, target);
        }

        return Coerce(Rv(init), target);
    }
}
