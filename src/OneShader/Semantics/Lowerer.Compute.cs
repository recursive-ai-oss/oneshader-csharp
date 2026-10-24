using System.Collections.Generic;
using OneShader.IR;
using OneShader.Syntax;
using OneShader.Types;

namespace OneShader.Semantics;

/// <summary>Lowering of atomics, byte-address buffer access and the out-parameter math intrinsics.</summary>
internal sealed partial class Lowerer
{
    private IrValue LowerAtomic(CallExpression c, BuiltinCall bi)
    {
        int form = bi.Extra;
        int valueArgs = form == 0 ? 1 : 2;
        var target = bi.ParamTypes[0];
        IrValue? pointer = null;
        IrValue? texture = null;
        IrValue? coord = null;
        int firstValue = 1;
        if (bi.HasOffset)
        {
            // RWByteAddressBuffer method: a one-index access chain addresses one 32-bit word.
            var ma = (MemberAccessExpression)c.Callee;
            var buf = ResourcePointer(ma.Target);
            var word = _b.Binary(Opcode.Shr, Coerce(Rv(c.Arguments[0]), ScalarType.UInt), Constant.UInt(2));
            pointer = buf is null ? null : _b.AccessChain(buf, ScalarType.UInt, word);
        }
        else if (StripScalarSwizzle(c.Arguments[0]) is IndexExpression { Target.Type: ResourceType { IsTexture: true } } tix)
        {
            texture = ResourcePointer(tix.Target);
            int dims = tix.Index.Type is VectorType iv ? iv.Count : 1;
            coord = Coerce(Rv(tix.Index), dims == 1 ? ScalarType.UInt : HlslType.Vector(ScalarType.UInt, dims));
        }
        else
        {
            pointer = Address(StripScalarSwizzle(c.Arguments[0]));
        }

        if (pointer is null && texture is null)
        {
            Fail("This atomic target cannot be addressed.", c.Arguments[0].Span);
            return Constant.Zero(ScalarType.Int);
        }

        var values = new IrValue[valueArgs];
        for (int i = 0; i < valueArgs; i++)
        {
            values[i] = Coerce(Rv(c.Arguments[firstValue + i]), target);
        }

        Instruction inst;
        if (form == 0)
        {
            inst = texture is null
                ? _b.Emit(Opcode.AtomicRmw, target, pointer!, values[0])
                : _b.Emit(Opcode.AtomicRmw, target, texture, coord!, values[0]);
            inst.Immediates.Add((int)bi.Atomic);
        }
        else
        {
            // Operands: target, comparand, new value (HLSL order: compare then value).
            inst = texture is null
                ? _b.Emit(Opcode.AtomicCompareExchange, target, pointer!, values[0], values[1])
                : _b.Emit(Opcode.AtomicCompareExchange, target, texture, coord!, values[0], values[1]);
        }

        if (c.Arguments.Count > firstValue + valueArgs)
        {
            var outArg = c.Arguments[^1];
            Store(outArg, Coerce(inst, outArg.Type!));
        }

        return Constant.Zero(ScalarType.Int);
    }

    /// <summary>Drops parentheses and a <c>.x</c> on a scalar, which names the same storage.</summary>
    private static Expression StripScalarSwizzle(Expression e)
    {
        while (true)
        {
            e = UnwrapParen(e);
            if (e is MemberAccessExpression { SwizzleComponents: not null, Target.Type: ScalarType } m)
            {
                e = m.Target;
                continue;
            }

            return e;
        }
    }

    private static int ByteSize(HlslType t)
    {
        switch (t)
        {
            case VectorType v:
                return 4 * v.Count;
            case StructType st:
            {
                int sum = 0;
                foreach (var f in st.Fields)
                {
                    sum += ByteSize(f.Type);
                }

                return sum;
            }

            default:
                return 4;
        }
    }

    private IrValue AddBytes(IrValue offset, int bytes) =>
        bytes == 0 ? offset : _b.Binary(Opcode.Add, offset, Constant.UInt((uint)bytes));

    private IrValue LoadBytes(IrValue buf, IrValue offset, HlslType t, int depth = 0)
    {
        switch (t)
        {
            case StructType st when depth < 32:
            {
                var parts = new IrValue[st.Fields.Count];
                int at = 0;
                for (int i = 0; i < parts.Length; i++)
                {
                    parts[i] = LoadBytes(buf, AddBytes(offset, at), st.Fields[i].Type, depth + 1);
                    at += ByteSize(st.Fields[i].Type);
                }

                return _b.Construct(st, parts);
            }

            case ScalarType or VectorType:
            {
                var bits = t is VectorType vt ? (HlslType)HlslType.Vector(ScalarType.UInt, vt.Count) : ScalarType.UInt;
                IrValue v = _b.Emit(Opcode.ByteAddressLoad, bits, buf, offset);
                return t.ScalarElement == ScalarType.UInt ? v : _b.Bitcast(v, t);
            }

            default:
                Fail("This type cannot be loaded from a byte-address buffer.", default);
                return Constant.Zero(t);
        }
    }

    private void StoreBytes(IrValue buf, IrValue offset, IrValue value, int depth = 0)
    {
        var t = value.Type;
        switch (t)
        {
            case StructType st when depth < 32:
            {
                int at = 0;
                for (int i = 0; i < st.Fields.Count; i++)
                {
                    var f = st.Fields[i].Type;
                    StoreBytes(buf, AddBytes(offset, at), _b.Extract(value, f, i), depth + 1);
                    at += ByteSize(f);
                }

                break;
            }

            case ScalarType or VectorType:
            {
                var bits = t is VectorType vt ? (HlslType)HlslType.Vector(ScalarType.UInt, vt.Count) : ScalarType.UInt;
                var v = t.ScalarElement == ScalarType.UInt ? value : _b.Bitcast(value, bits);
                _b.Emit(Opcode.ByteAddressStore, HlslType.Void, buf, offset, v);
                break;
            }

            default:
                Fail("This type cannot be stored to a byte-address buffer.", default);
                break;
        }
    }

    private IrValue LowerByteAddress(CallExpression c, BuiltinCall bi)
    {
        var ma = (MemberAccessExpression)c.Callee;
        var buf = ResourcePointer(ma.Target);
        if (buf is null)
        {
            Fail("The byte-address buffer must be a resource variable.", ma.Span);
            return Constant.Zero(bi.Result);
        }

        var offset = Arg(c, bi, 0);
        if (bi.Extra == 0)
        {
            return LoadBytes(buf, offset, bi.Result);
        }

        StoreBytes(buf, offset, Arg(c, bi, 1));
        return Constant.Zero(ScalarType.Int);
    }

    private IrValue LowerOutIntrinsic(CallExpression c, BuiltinCall bi)
    {
        var ops = new List<IrValue> { Arg(c, bi, 0) };
        var copyOut = new List<(IrValue Temp, Expression Arg)>();
        for (int i = 1; i < bi.ParamTypes.Length; i++)
        {
            var arg = c.Arguments[i];
            var pt = bi.ParamTypes[i];
            var addr = Address(arg);
            if (addr is not null && addr.Type is PointerType { Space: AddressSpace.Function } && arg.Type!.Equals(pt))
            {
                ops.Add(addr);
                continue;
            }

            var tmp = _b.Alloca(pt, "out");
            ops.Add(tmp);
            copyOut.Add((tmp, arg));
        }

        var r = _b.CallIntrinsic(bi.Ir, bi.Result, [.. ops]);
        foreach (var (tmp, arg) in copyOut)
        {
            Store(arg, Coerce(_b.Load(tmp), arg.Type!));
        }

        return bi.Result.IsVoid ? Constant.Zero(ScalarType.Int) : r;
    }
}
