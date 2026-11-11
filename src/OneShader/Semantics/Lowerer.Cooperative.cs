using System.Collections.Generic;
using OneShader.IR;
using OneShader.Syntax;
using OneShader.Types;

namespace OneShader.Semantics;

/// <summary>Lowering of cooperative-matrix members.</summary>
internal sealed partial class Lowerer
{
    /// <summary>A pointer to element <paramref name="index"/> of a structured buffer or a groupshared array.</summary>
    private IrValue? CooperativeElementPointer(Expression memory, HlslType element, IrValue index)
    {
        var basePtr = memory.Type is ResourceType ? ResourcePointer(memory) : Address(memory);
        if (basePtr is null)
        {
            Fail("This memory cannot be addressed; pass a structured buffer or a groupshared array variable.", memory.Span);
            return null;
        }

        return _b.AccessChain(basePtr, element, index);
    }

    private IrValue LowerCooperative(CallExpression c, BuiltinCall bi)
    {
        if (c.Callee is not MemberAccessExpression)
        {
            return LowerCooperativeStatic(c.Arguments, bi);
        }

        var ma = (MemberAccessExpression)c.Callee;
        var cm = (CooperativeMatrixType)ma.Target.Type!;
        switch (bi.Name)
        {
            case "Load":
            {
                var ptr = CooperativeElementPointer(c.Arguments[0], cm.Element, Coerce(Rv(c.Arguments[1]), ScalarType.Int));
                var stride = Arg(c, bi, 2);
                if (ptr is not null)
                {
                    var load = _b.CallIntrinsic(Intrinsic.CoopMatrixLoad, cm, ptr, stride);
                    load.Immediates.Add(bi.Extra);
                    Store(ma.Target, load);
                }

                return Constant.Zero(ScalarType.Int);
            }

            case "Store":
            {
                var ptr = CooperativeElementPointer(c.Arguments[0], cm.Element, Coerce(Rv(c.Arguments[1]), ScalarType.Int));
                var matrix = Rv(ma.Target);
                var stride = Arg(c, bi, 2);
                if (ptr is not null)
                {
                    var store = _b.CallIntrinsic(Intrinsic.CoopMatrixStore, HlslType.Void, ptr, matrix, stride);
                    store.Immediates.Add(bi.Extra);
                }

                return Constant.Zero(ScalarType.Int);
            }

            default:
                return _b.CallIntrinsic(Intrinsic.CoopMatrixLength, ScalarType.UInt, Rv(ma.Target));
        }
    }

    private IrValue LowerCooperativeStatic(IReadOnlyList<Expression> args, BuiltinCall bi)
    {
        if (bi.Name == "Zero")
        {
            return _b.CallIntrinsic(Intrinsic.CoopMatrixZero, bi.Result);
        }

        var a = Coerce(Rv(args[0]), bi.ParamTypes[0]);
        var b = Coerce(Rv(args[1]), bi.ParamTypes[1]);
        var acc = Coerce(Rv(args[2]), bi.ParamTypes[2]);
        return _b.CallIntrinsic(Intrinsic.CoopMatrixMulAdd, bi.Result, a, b, acc);
    }
}
