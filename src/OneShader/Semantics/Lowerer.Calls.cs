using System;
using System.Collections.Generic;
using OneShader.Diagnostics;
using OneShader.IR;
using OneShader.Syntax;
using OneShader.Types;

namespace OneShader.Semantics;

internal sealed partial class Lowerer
{
    private void Br(BasicBlock target)
    {
        _b.Branch(target);
        _reached.Add(target);
    }

    private void CondBr(IrValue cond, BasicBlock a, BasicBlock b)
    {
        _b.CondBranch(cond, a, b);
        _reached.Add(a);
        _reached.Add(b);
    }

    // ------------------------------------------------------------ calls

    private IrValue RvCall(CallExpression c)
    {
        var info = c.Info!;
        if (info.Function is not null)
        {
            return CallUser(c.Span, info.Function, info.ThisArg, info.Args ?? c.Arguments);
        }

        if (info.Builtin is not null)
        {
            return CallBuiltin(c, info.Builtin);
        }

        if (info.TargetType is not null)
        {
            return RvConstruct(info.TargetType, c.Arguments);
        }

        Fail("This call cannot be compiled.", c.Span);
        return Constant.Zero(c.Type ?? HlslType.Void);
    }

    /// <summary>Calls a user function or member function; <paramref name="thisArg"/> is the object of a member call.</summary>
    private IrValue CallUser(SourceSpan span, FunctionSymbol sym, Expression? thisArg, IReadOnlyList<Expression> callArgs)
    {
        var target = sym.Definition ?? sym;
        var all = new List<Expression>(callArgs.Count + 1);
        if (sym.HasThis && thisArg is not null)
        {
            all.Add(thisArg);
        }

        all.AddRange(callArgs);
        bool specialize = target.HasResourceParams;
        IrValue?[] boundResources = [];
        var irf = specialize ? Specialize(target, all, span, out boundResources) : _fns[target];
        if (irf is null)
        {
            return Constant.Zero(sym.ReturnType.IsVoid ? ScalarType.Int : sym.ReturnType);
        }

        var args = new List<IrValue>();
        var copyOut = new List<(IrValue Temp, Expression Arg)>();
        for (int i = 0; i < sym.Params.Count; i++)
        {
            var p = sym.Params[i];
            if (p.Type is ResourceType or SamplerType or ArrayType { Element: ResourceType or SamplerType })
            {
                // Resources are bound into the specialized copy; only run-time indices into resource arrays are passed.
                if (boundResources[i] is { } resourcePtr)
                {
                    foreach (var index in DynamicIndices(resourcePtr))
                    {
                        args.Add(index);
                    }
                }

                continue;
            }

            if (i >= all.Count)
            {
                args.Add(Coerce(Rv(p.Default!), p.Type));
                continue;
            }

            var arg = all[i];
            if (!p.IsOut)
            {
                args.Add(Coerce(Rv(arg), p.Type));
                continue;
            }

            var addr = Address(arg);
            // Only a whole variable or parameter can be passed by pointer: Vulkan rejects pointers into a variable as call arguments.
            if (addr is not null && addr.Type is PointerType { Space: AddressSpace.Function } && arg.Type!.Equals(p.Type)
                && (addr is Argument || addr is Instruction { Op: Opcode.Alloca }))
            {
                args.Add(addr);
                continue;
            }

            var tmp = _b.Alloca(p.Type, "out");
            if ((p.Mods & Modifiers.In) != 0)
            {
                _b.Store(tmp, Coerce(Rv(arg), p.Type));
            }

            args.Add(tmp);
            // The object of a member call is copied back only where it can be written; a temporary or a constant buffer value just gets a copy.
            bool writable = i != 0 || !sym.HasThis || (addr is not null && addr.Type is PointerType { Space: AddressSpace.Function or AddressSpace.StorageBuffer or AddressSpace.Workgroup or AddressSpace.Private });
            if (writable)
            {
                copyOut.Add((tmp, arg));
            }
        }

        var call = _b.Call(irf, [.. args]);
        foreach (var (tmp, arg) in copyOut)
        {
            Store(arg, Coerce(_b.Load(tmp), arg.Type!));
        }

        return call;
    }

    private IrValue Arg(CallExpression c, BuiltinCall bi, int i) => Coerce(Rv(c.Arguments[i]), bi.ParamTypes[i]);

    /// <summary>
    /// Component-wise built-ins on matrices (<c>saturate(M)</c>, <c>lerp(A, B, t)</c>, <c>pow(A, B)</c>): no target has these on matrix types,
    /// so each row goes through the built-in as a vector and the rows are put back together.
    /// </summary>
    private IrValue CallBuiltinPerRow(CallExpression c, BuiltinCall bi, MatrixType result)
    {
        var ops = new IrValue[bi.ParamTypes.Length];
        for (int i = 0; i < ops.Length; i++)
        {
            ops[i] = Arg(c, bi, i);
        }

        var rows = new IrValue[result.Rows];
        var rowResult = HlslType.Vector(result.Element, result.Columns);
        for (int r = 0; r < rows.Length; r++)
        {
            var rowOps = new IrValue[ops.Length];
            for (int i = 0; i < ops.Length; i++)
            {
                rowOps[i] = ops[i].Type is MatrixType mo ? Extract(ops[i], HlslType.Vector(mo.Element, mo.Columns), r) : ops[i];
            }

            switch (bi.Kind)
            {
                case BuiltinKind.Mad when result.Element.IsFloatingPoint:
                    rows[r] = _b.CallIntrinsic(Intrinsic.Fma, rowResult, rowOps[0], rowOps[1], rowOps[2]);
                    break;
                case BuiltinKind.Mad:
                    rows[r] = _b.Binary(Opcode.Add, _b.Binary(Opcode.Mul, rowOps[0], rowOps[1]), rowOps[2]);
                    break;
                case BuiltinKind.Sign when rowOps[0].Type.ScalarElement!.IsFloatingPoint:
                    rows[r] = _b.Convert(_b.CallIntrinsic(Intrinsic.Sign, rowOps[0].Type, rowOps[0]), rowResult);
                    break;
                case BuiltinKind.Sign:
                    rows[r] = _b.CallIntrinsic(Intrinsic.Sign, rowOps[0].Type, rowOps[0]);
                    break;
                default:
                    rows[r] = _b.CallIntrinsic(bi.Ir, rowResult, rowOps);
                    break;
            }
        }

        return _b.Construct(result, rows);
    }

    private IrValue CallBuiltin(CallExpression c, BuiltinCall bi)
    {
        if (bi.Result is MatrixType matrixResult && bi.Kind is BuiltinKind.Intrinsic or BuiltinKind.Mad or BuiltinKind.Sign)
        {
            return CallBuiltinPerRow(c, bi, matrixResult);
        }

        switch (bi.Kind)
        {
            case BuiltinKind.Intrinsic:
            {
                if (bi.Ir is Intrinsic.WaveGetLaneIndex or Intrinsic.WaveGetLaneCount)
                {
                    // Lane index and count are stage inputs that only the entry point can read; helpers see a private copy it fills in.
                    return _b.Load(LaneGlobal(bi.Ir == Intrinsic.WaveGetLaneCount));
                }

                var ops = new IrValue[bi.ParamTypes.Length];
                for (int i = 0; i < ops.Length; i++)
                {
                    ops[i] = Arg(c, bi, i);
                }

                return _b.CallIntrinsic(bi.Ir, bi.Result, ops);
            }

            case BuiltinKind.Op:
                return _b.Binary(bi.Op, Arg(c, bi, 0), Arg(c, bi, 1));
            case BuiltinKind.Mad:
            {
                var a = Arg(c, bi, 0);
                var b = Arg(c, bi, 1);
                var d = Arg(c, bi, 2);
                if (bi.Result.ScalarElement!.IsFloatingPoint)
                {
                    return _b.CallIntrinsic(Intrinsic.Fma, bi.Result, a, b, d);
                }

                return _b.Binary(Opcode.Add, _b.Binary(Opcode.Mul, a, b), d);
            }

            case BuiltinKind.Sign:
            {
                var v = Arg(c, bi, 0);
                if (v.Type.ScalarElement!.IsFloatingPoint)
                {
                    var s = _b.CallIntrinsic(Intrinsic.Sign, v.Type, v);
                    return _b.Convert(s, bi.Result);
                }

                return _b.CallIntrinsic(Intrinsic.Sign, v.Type, v);
            }

            case BuiltinKind.Transpose:
                return _b.Emit(Opcode.Transpose, bi.Result, Arg(c, bi, 0));
            case BuiltinKind.Mul:
                return LowerMul(c, bi);
            case BuiltinKind.AsType:
            {
                var v = Arg(c, bi, 0);
                return v.Type.ScalarElement == bi.Result.ScalarElement ? v : _b.Bitcast(v, bi.Result);
            }

            case BuiltinKind.JoinDouble:
                return LowerJoinDouble(c, bi);
            case BuiltinKind.SplitDouble:
                return LowerSplitDouble(c, bi);
            case BuiltinKind.Select:
                return _b.Select(Arg(c, bi, 0), Arg(c, bi, 1), Arg(c, bi, 2));
            case BuiltinKind.Clip:
                return LowerClip(c, bi);
            case BuiltinKind.BufferLoad:
                return LowerBufferLoad(c, bi);
            case BuiltinKind.GetDimensions:
                return LowerGetDimensions(c, bi);
            case BuiltinKind.Append:
                return LowerAppend(c, bi);
            case BuiltinKind.Consume:
                return LowerConsume(c, bi);
            case BuiltinKind.Barrier:
                _b.Barrier((BarrierFlags)bi.Extra);
                return Constant.Zero(ScalarType.Int);
            case BuiltinKind.Atomic:
                return LowerAtomic(c, bi);
            case BuiltinKind.ByteAddress:
                return LowerByteAddress(c, bi);
            case BuiltinKind.OutIntrinsic:
                return LowerOutIntrinsic(c, bi);
            default:
                return LowerTexture(c, bi);
        }
    }

    private IrValue LowerJoinDouble(CallExpression c, BuiltinCall bi)
    {
        var lo = Arg(c, bi, 0);
        var hi = Arg(c, bi, 1);
        var word2 = HlslType.Vector(ScalarType.UInt, 2);
        int n = bi.Result is VectorType v ? v.Count : 1;
        var lanes = new IrValue[n];
        for (int i = 0; i < n; i++)
        {
            var l = n == 1 ? lo : _b.Extract(lo, ScalarType.UInt, i);
            var h = n == 1 ? hi : _b.Extract(hi, ScalarType.UInt, i);
            lanes[i] = _b.Bitcast(_b.Construct(word2, l, h), ScalarType.Double);
        }

        return n == 1 ? lanes[0] : _b.Construct(bi.Result, lanes);
    }

    private IrValue LowerSplitDouble(CallExpression c, BuiltinCall bi)
    {
        var d = Arg(c, bi, 0);
        var word2 = HlslType.Vector(ScalarType.UInt, 2);
        int n = d.Type is VectorType v ? v.Count : 1;
        var los = new IrValue[n];
        var his = new IrValue[n];
        for (int i = 0; i < n; i++)
        {
            var lane = n == 1 ? d : _b.Extract(d, ScalarType.Double, i);
            var words = _b.Bitcast(lane, word2);
            los[i] = _b.Extract(words, ScalarType.UInt, 0);
            his[i] = _b.Extract(words, ScalarType.UInt, 1);
        }

        var wordType = bi.ParamTypes[1];
        Store(c.Arguments[1], Coerce(n == 1 ? los[0] : _b.Construct(wordType, los), c.Arguments[1].Type!));
        Store(c.Arguments[2], Coerce(n == 1 ? his[0] : _b.Construct(wordType, his), c.Arguments[2].Type!));
        return Constant.Zero(ScalarType.Int);
    }

    private IrValue LowerMul(CallExpression c, BuiltinCall bi)
    {
        var a = Arg(c, bi, 0);
        var b = Arg(c, bi, 1);
        if (a.Type is ScalarType)
        {
            return _b.Binary(Opcode.Mul, b.Type is ScalarType ? a : Splat(a, b.Type), b);
        }

        if (b.Type is ScalarType)
        {
            return _b.Binary(Opcode.Mul, a, Splat(b, a.Type));
        }

        if (a.Type is VectorType && b.Type is VectorType)
        {
            return _b.CallIntrinsic(Intrinsic.Dot, bi.Result, a, b);
        }

        return _b.Emit(Opcode.MatMul, bi.Result, a, b);
    }

    private IrValue LowerClip(CallExpression c, BuiltinCall bi)
    {
        var v = Arg(c, bi, 0);
        var zero = Splat(Constant.Float(0), v.Type);
        IrValue cond = _b.Compare(Opcode.Lt, v, zero);
        if (v.Type is VectorType)
        {
            cond = _b.CallIntrinsic(Intrinsic.Any, ScalarType.Bool, cond);
        }

        var hdr = _b.Block!;
        var kill = _f.AddBlock("clip.kill");
        var cont = _f.AddBlock("clip.end");
        CondBr(cond, kill, cont);
        IrBuilder.MarkSelection(hdr, cont);
        _b.Block = kill;
        _b.Kill();
        _b.Block = cont;
        return Constant.Zero(ScalarType.Int);
    }

    private IrValue LowerBufferLoad(CallExpression c, BuiltinCall bi)
    {
        var ma = (MemberAccessExpression)c.Callee;
        var rt = (ResourceType)ma.Target.Type!;
        var buf = ResourcePointer(ma.Target)!;
        var idx = Arg(c, bi, 0);
        if (rt.ResourceKind is ResourceTypeKind.StructuredBuffer or ResourceTypeKind.RWStructuredBuffer)
        {
            return _b.Load(_b.AccessChain(buf, rt.Element!, idx));
        }

        return _b.Emit(Opcode.TextureLoad, rt.Element!, buf, idx);
    }

    private readonly Dictionary<GlobalVariable, GlobalVariable> _counters = [];

    /// <summary>The hidden counter buffer of an append or consume buffer.</summary>
    private GlobalVariable? CounterOf(IrValue buffer, SourceSpan span)
    {
        if (buffer is GlobalVariable g && _counters.TryGetValue(g, out var counter))
        {
            return counter;
        }

        Fail("This append or consume buffer cannot be resolved to a declared buffer.", span);
        return null;
    }

    /// <summary>Append: reserve a slot by incrementing the counter (the old value is the slot), then store the element there.</summary>
    private IrValue LowerAppend(CallExpression c, BuiltinCall bi)
    {
        var ma = (MemberAccessExpression)c.Callee;
        var rt = (ResourceType)ma.Target.Type!;
        var buf = ResourcePointer(ma.Target)!;
        var value = Arg(c, bi, 0);
        if (CounterOf(buf, c.Span) is { } counter)
        {
            var word = _b.AccessChain(counter, ScalarType.UInt, Constant.UInt(0));
            var slot = _b.Atomic(AtomicOp.Add, word, Constant.UInt(1));
            _b.Store(_b.AccessChain(buf, rt.Element!, slot), value);
        }

        return Constant.Zero(ScalarType.Int);
    }

    /// <summary>Consume: decrement the counter; the element just below the old count is the one taken.</summary>
    private IrValue LowerConsume(CallExpression c, BuiltinCall bi)
    {
        var ma = (MemberAccessExpression)c.Callee;
        var rt = (ResourceType)ma.Target.Type!;
        var buf = ResourcePointer(ma.Target)!;
        if (CounterOf(buf, c.Span) is not { } counter)
        {
            return Constant.Zero(bi.Result);
        }

        var word = _b.AccessChain(counter, ScalarType.UInt, Constant.UInt(0));
        var old = _b.Atomic(AtomicOp.Add, word, Constant.UInt(uint.MaxValue));
        var slot = _b.Binary(Opcode.Sub, old, Constant.UInt(1));
        return _b.Load(_b.AccessChain(buf, rt.Element!, slot));
    }

    private IrValue LowerGetDimensions(CallExpression c, BuiltinCall bi)
    {
        var ma = (MemberAccessExpression)c.Callee;
        var rt = (ResourceType)ma.Target.Type!;
        var res = ResourcePointer(ma.Target)!;
        var values = new List<IrValue>();
        int first = 0;
        if (rt.ResourceKind is ResourceTypeKind.ByteAddressBuffer or ResourceTypeKind.RWByteAddressBuffer)
        {
            // The buffer is addressed in 32-bit words; HLSL reports bytes.
            values.Add(_b.Binary(Opcode.Shl, _b.Emit(Opcode.ArrayLength, ScalarType.UInt, res), Constant.UInt(2)));
        }
        else if (rt.ResourceKind is ResourceTypeKind.StructuredBuffer or ResourceTypeKind.RWStructuredBuffer
            or ResourceTypeKind.AppendStructuredBuffer or ResourceTypeKind.ConsumeStructuredBuffer)
        {
            values.Add(_b.Emit(Opcode.ArrayLength, ScalarType.UInt, res));
            int stride = (res as GlobalVariable)?.Layout?.Size ?? 0;
            values.Add(Constant.UInt((uint)stride));
        }
        else
        {
            int n = bi.Extra;
            var sizeType = n == 1 ? (HlslType)ScalarType.UInt : HlslType.Vector(ScalarType.UInt, n);
            IrValue size;
            if (bi.HasOffset)
            {
                size = _b.Emit(Opcode.TextureQuerySize, sizeType, res, Coerce(Rv(c.Arguments[0]), ScalarType.UInt));
                first = 1;
            }
            else if (rt.IsReadWrite || rt.ResourceKind is ResourceTypeKind.Buffer or ResourceTypeKind.Texture2DMS or ResourceTypeKind.Texture2DMSArray)
            {
                size = _b.Emit(Opcode.TextureQuerySize, sizeType, res);
            }
            else
            {
                size = _b.Emit(Opcode.TextureQuerySize, sizeType, res, Constant.UInt(0));
            }

            if (n == 1)
            {
                values.Add(size);
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    values.Add(Extract(size, ScalarType.UInt, i));
                }
            }

            if (c.Arguments.Count - first > n)
            {
                values.Add(_b.Emit(Opcode.TextureQueryLevels, ScalarType.UInt, res));
            }
        }

        for (int i = 0; i < values.Count && first + i < c.Arguments.Count; i++)
        {
            var arg = c.Arguments[first + i];
            Store(arg, Coerce(values[i], arg.Type!));
        }

        return Constant.Zero(ScalarType.Int);
    }

    private static int[]? ConstOffsets(Expression e)
    {
        e = UnwrapParen(e);
        if (e.Info?.Const is { } cv && !cv.IsFloat)
        {
            return [(int)cv.Int];
        }

        if (e is ConstructExpression ce)
        {
            var list = new List<int>();
            foreach (var a in ce.Arguments)
            {
                var sub = ConstOffsets(a);
                if (sub is null)
                {
                    return null;
                }

                list.AddRange(sub);
            }

            int need = ce.Type is VectorType v ? v.Count : 1;
            if (list.Count == 1 && need > 1)
            {
                var splat = new int[need];
                Array.Fill(splat, list[0]);
                return splat;
            }

            return [.. list];
        }

        return null;
    }

    private void AddOffsets(Instruction i, CallExpression c, int argIndex)
    {
        var offs = ConstOffsets(c.Arguments[argIndex]);
        if (offs is null)
        {
            _diag.Error(SemanticCodes.NotConstant, "A texel offset must be a compile-time constant such as int2(1, 0).", c.Arguments[argIndex].Span);
            return;
        }

        i.Immediates.AddRange(offs);
    }

    private IrValue LowerTexture(CallExpression c, BuiltinCall bi)
    {
        var ma = (MemberAccessExpression)c.Callee;
        var tex = ResourcePointer(ma.Target)!;
        var ops = new List<IrValue> { tex };
        var rt = (ResourceType)ma.Target.Type!;
        int n = bi.ParamTypes.Length;
        int offsetArg = bi.HasOffset ? n - 1 : -1;
        switch (bi.TextureOp)
        {
            case Opcode.TextureLoad:
            {
                int dims = bi.Extra;
                var v = Arg(c, bi, 0);
                if (rt.IsReadWrite)
                {
                    ops.Add(v);
                }
                else if (rt.ResourceKind is ResourceTypeKind.Texture2DMS or ResourceTypeKind.Texture2DMSArray)
                {
                    ops.Add(v);
                    ops.Add(Arg(c, bi, 1));
                    offsetArg = -1;
                }
                else
                {
                    var iv = (VectorType)v.Type;
                    IrValue coord = dims == 1 ? Extract(v, ScalarType.Int, 0) : _b.Shuffle(v, v, HlslType.Vector(ScalarType.Int, dims), SeqInts(dims));
                    ops.Add(coord);
                    ops.Add(Extract(v, iv.Element, dims));
                }

                break;
            }

            case Opcode.TextureQueryLod:
            {
                ops.Add(ResourcePointer(c.Arguments[0])!);
                ops.Add(Arg(c, bi, 1));
                var q = _b.Emit(Opcode.TextureQueryLod, HlslType.Vector(ScalarType.Float, 2), [.. ops]);
                return Extract(q, ScalarType.Float, bi.Extra);
            }

            default:
            {
                int plain = bi.HasOffset ? n - 1 : n;
                for (int i = 0; i < plain; i++)
                {
                    ops.Add(bi.ParamTypes[i] is SamplerType ? ResourcePointer(c.Arguments[i])! : Arg(c, bi, i));
                }

                break;
            }
        }

        var inst = _b.Emit(bi.TextureOp, bi.Result, [.. ops]);
        if (bi.TextureOp == Opcode.TextureGather)
        {
            inst.Immediates.Add(bi.Extra);
        }

        if (offsetArg >= 0)
        {
            AddOffsets(inst, c, offsetArg);
        }

        return inst;
    }

    private static int[] SeqInts(int n)
    {
        var a = new int[n];
        for (int i = 0; i < n; i++)
        {
            a[i] = i;
        }

        return a;
    }

    // ------------------------------------------------------------ statements

    private void LowerBlockBody(IReadOnlyList<Statement> list)
    {
        foreach (var s in list)
        {
            if (_terminated)
            {
                break;
            }

            LowerStatement(s);
        }
    }

    private static ControlHint LoopHint(IReadOnlyList<AttributeSyntax> attrs)
    {
        foreach (var a in attrs)
        {
            switch (a.Name.ToString())
            {
                case "unroll": return ControlHint.Unroll;
                case "loop": return ControlHint.DontUnroll;
                case "flatten": return ControlHint.Flatten;
                case "branch": return ControlHint.Branch;
            }
        }

        return ControlHint.None;
    }

    private IrValue Condition(Expression e) => Coerce(Rv(e), ScalarType.Bool);

    /// <summary>Continues lowering in <paramref name="merge"/>, or closes it when nothing can reach it.</summary>
    private void Continue(BasicBlock merge)
    {
        _b.Block = merge;
        if (_reached.Contains(merge))
        {
            _terminated = false;
        }
        else
        {
            _b.Unreachable();
            _terminated = true;
        }
    }

    private void LowerStatement(Statement s)
    {
        if (++_depth > 1024)
        {
            _depth--;
            Fail("The statements are nested too deeply to compile.", s.Span);
            _terminated = true;
            return;
        }

        _b.Span = s.Span;
        try
        {
            LowerStatementCore(s);
        }
        finally
        {
            _depth--;
        }
    }

    private void LowerStatementCore(Statement s)
    {
        switch (s)
        {
            case BlockStatement b:
                if (_curSource is { } blockOwner && _blockSeen.Add((b.Span.Path, b.Span.Offset)))
                {
                    blockOwner.Blocks.Add(b.Span);
                }

                LowerBlockBody(b.Statements);
                break;
            case ExpressionStatement e:
                Rv(e.Expression);
                break;
            case DeclarationStatement { Declaration: VariableDeclaration v }:
                foreach (var d in v.Declarators)
                {
                    if (d.Symbol is not VariableSymbol sym)
                    {
                        continue;
                    }

                    if (sym.Kind == VarKind.Global)
                    {
                        // A static local: its storage is the private global created for it.
                        continue;
                    }

                    _b.Span = d.Span;
                    if (sym.Type is ResourceType or SamplerType)
                    {
                        _resLocals.Add(sym);
                        sym.Ir = null;
                        if (d.Initializer is not null)
                        {
                            BindResourceLocal(sym, d.Initializer, d.Span);
                        }

                        continue;
                    }

                    var slot = _b.Alloca(sym.Type, sym.Name);
                    slot.DebugVariable = new SourceVariable(sym.Name, sym.Type, d.Span, 0);
                    sym.Ir = slot;
                    if (d.Initializer is not null)
                    {
                        _b.Precise = (sym.Mods & Modifiers.Precise) != 0;
                        var init = LowerInitializer(d.Initializer, sym.Type);
                        _b.Precise = false;
                        _b.Store(slot, init);
                    }
                }

                break;
            case IfStatement i:
                LowerIf(i);
                break;
            case ForStatement f:
                LowerFor(f);
                break;
            case WhileStatement w:
                LowerWhile(w);
                break;
            case DoWhileStatement dw:
                LowerDoWhile(dw);
                break;
            case SwitchStatement sw:
                LowerSwitch(sw);
                break;
            case BreakStatement:
                Br(_jumps[^1].Break);
                _terminated = true;
                break;
            case ContinueStatement:
                for (int k = _jumps.Count - 1; k >= 0; k--)
                {
                    if (_jumps[k].Continue is { } ct)
                    {
                        Br(ct);
                        break;
                    }
                }

                _terminated = true;
                break;
            case DiscardStatement:
                _b.Kill();
                _terminated = true;
                break;
            case ReturnStatement r:
                if (r.Value is null)
                {
                    _b.Return();
                }
                else
                {
                    _b.Precise = _returnsPrecise;
                    var result = Coerce(LowerInitializer(r.Value, _curRet), _curRet);
                    _b.Precise = false;
                    _b.Return(result);
                }

                _terminated = true;
                break;
        }
    }

    private HlslType _curRet => _f.ReturnType;

    /// <summary>True while lowering the body of a function declared <c>precise</c>: its return value is computed exactly as written.</summary>
    private bool _returnsPrecise;

    private void LowerIf(IfStatement i)
    {
        var cond = Condition(i.Condition);
        var hdr = _b.Block!;
        var then = _f.AddBlock("if.then");
        var els = i.Else is null ? null : _f.AddBlock("if.else");
        var merge = _f.AddBlock("if.end");
        CondBr(cond, then, els ?? merge);
        IrBuilder.MarkSelection(hdr, merge, LoopHint(i.Attributes));
        int resCount = _resLocals.Count;
        var resBefore = SnapRes();
        _b.Block = then;
        _terminated = false;
        LowerStatement(i.Then);
        var thenEnd = _terminated ? null : SnapRes();
        if (!_terminated)
        {
            Br(merge);
        }

        ResSnap? elseEnd = resBefore;
        if (els is not null)
        {
            RestoreRes(resBefore);
            _b.Block = els;
            _terminated = false;
            LowerStatement(i.Else!);
            elseEnd = _terminated ? null : SnapRes();
            if (!_terminated)
            {
                Br(merge);
            }
        }

        MergeRes([thenEnd, elseEnd], resCount);
        Continue(merge);
    }

    private void LowerFor(ForStatement f)
    {
        if (f.Initializer is not null)
        {
            LowerStatement(f.Initializer);
        }

        LowerLoop(f.Condition, f.Step, f.Body, f.Attributes, false);
    }

    private void LowerWhile(WhileStatement w) => LowerLoop(w.Condition, null, w.Body, w.Attributes, false);

    private void LowerLoop(Expression? cond, Expression? step, Statement body, IReadOnlyList<AttributeSyntax> attrs, bool unused)
    {
        _ = unused;
        int resCount = _resLocals.Count;
        var resBefore = SnapRes();
        var header = _f.AddBlock("loop.header");
        var condBlock = cond is null ? null : _f.AddBlock("loop.cond");
        var bodyBlock = _f.AddBlock("loop.body");
        var cont = _f.AddBlock("loop.continue");
        var exit = _f.AddBlock("loop.exit");
        Br(header);
        _b.Block = header;
        Br(condBlock ?? bodyBlock);
        IrBuilder.MarkLoop(header, exit, cont, LoopHint(attrs));
        if (condBlock is not null)
        {
            _b.Block = condBlock;
            var c = Condition(cond!);
            CondBr(c, bodyBlock, exit);
        }

        _b.Block = bodyBlock;
        _terminated = false;
        _jumps.Add((exit, cont));
        LowerStatement(body);
        _jumps.RemoveAt(_jumps.Count - 1);
        if (!_terminated)
        {
            Br(cont);
        }

        _b.Block = cont;
        _terminated = false;
        if (!_reached.Contains(cont))
        {
            // Nothing continues: the step is dead code but the block must still branch back.
            Br(header);
        }
        else
        {
            if (step is not null)
            {
                Rv(step);
            }

            Br(header);
        }

        CheckLoopRes(resBefore, resCount);
        Continue(exit);
    }

    private void LowerDoWhile(DoWhileStatement d)
    {
        int resCount = _resLocals.Count;
        var resBefore = SnapRes();
        var header = _f.AddBlock("do.header");
        var bodyBlock = _f.AddBlock("do.body");
        var cont = _f.AddBlock("do.continue");
        var exit = _f.AddBlock("do.exit");
        Br(header);
        _b.Block = header;
        Br(bodyBlock);
        IrBuilder.MarkLoop(header, exit, cont, LoopHint(d.Attributes));
        _b.Block = bodyBlock;
        _terminated = false;
        _jumps.Add((exit, cont));
        LowerStatement(d.Body);
        _jumps.RemoveAt(_jumps.Count - 1);
        if (!_terminated)
        {
            Br(cont);
        }

        _b.Block = cont;
        _terminated = false;
        if (_reached.Contains(cont))
        {
            var c = Condition(d.Condition);
            CondBr(c, header, exit);
        }
        else
        {
            Br(header);
        }

        CheckLoopRes(resBefore, resCount);
        Continue(exit);
    }

    private void LowerSwitch(SwitchStatement sw)
    {
        var sel = Rv(sw.Selector);
        if (sel.Type is not ScalarType { IsInteger: true })
        {
            sel = Coerce(sel, ScalarType.Int);
        }

        var hdr = _b.Block!;
        var merge = _f.AddBlock("switch.end");
        var blocks = new List<BasicBlock>();
        var cases = new List<KeyValuePair<int, BasicBlock>>();
        BasicBlock? def = null;
        foreach (var sec in sw.Sections)
        {
            var bb = _f.AddBlock("switch.case");
            blocks.Add(bb);
            foreach (var l in sec.Labels)
            {
                if (l.Value is null)
                {
                    def = bb;
                }
                else
                {
                    cases.Add(new KeyValuePair<int, BasicBlock>((int)l.Value.Info!.Const!.Value.Int, bb));
                }
            }
        }

        _b.Switch(sel, def ?? merge, cases);
        _reached.Add(def ?? merge);
        foreach (var c in cases)
        {
            _reached.Add(c.Value);
        }

        IrBuilder.MarkSelection(hdr, merge, ControlHint.None);
        _jumps.Add((merge, null));
        int resCount = _resLocals.Count;
        var resBefore = SnapRes();
        var resEnds = new List<ResSnap?>();
        if (def is null)
        {
            resEnds.Add(resBefore);
        }

        for (int i = 0; i < sw.Sections.Count; i++)
        {
            _b.Block = blocks[i];
            _terminated = false;
            RestoreRes(resBefore);
            LowerBlockBody(sw.Sections[i].Statements);
            resEnds.Add(SnapRes());
            if (!_terminated)
            {
                Br(i + 1 < blocks.Count ? blocks[i + 1] : merge);
            }
        }

        _jumps.RemoveAt(_jumps.Count - 1);
        MergeRes(resEnds, resCount);
        // Section blocks nothing jumps to (no label reaches them) still need a valid end.
        foreach (var bb in blocks)
        {
            if (!_reached.Contains(bb) && bb.Terminator is null)
            {
                _b.Block = bb;
                _b.Unreachable();
            }
        }

        Continue(merge);
    }
}
