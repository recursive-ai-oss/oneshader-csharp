using System;
using System.Collections.Generic;
using OneShader.IR;
using OneShader.Syntax;
using OneShader.Types;

namespace OneShader.Semantics;

/// <summary>Checking of the compute-oriented built-ins: barriers, atomics, byte-address buffers and the bit/out-parameter math functions.</summary>
internal sealed partial class Checker
{
    private enum AtomicDest
    {
        Invalid,
        Memory,
        Texel,
    }

    private static bool IsInt32(HlslType t) => t is ScalarType s && (s == ScalarType.Int || s == ScalarType.UInt);

    /// <summary>Atomics work on 32-bit integers everywhere and on 64-bit integers in buffers and group-shared memory.</summary>
    private static bool IsAtomicInt(HlslType t) => IsInt32(t) || t is ScalarType { ScalarKind: ScalarKind.Int64 or ScalarKind.UInt64 };

    private static bool TryAtomicOp(string suffix, out AtomicOp op, out int form)
    {
        form = 0;
        op = AtomicOp.Add;
        switch (suffix)
        {
            case "Add": op = AtomicOp.Add; return true;
            case "And": op = AtomicOp.And; return true;
            case "Or": op = AtomicOp.Or; return true;
            case "Xor": op = AtomicOp.Xor; return true;
            case "Min": op = AtomicOp.Min; return true;
            case "Max": op = AtomicOp.Max; return true;
            case "Exchange": op = AtomicOp.Exchange; return true;
            case "CompareExchange": form = 1; return true;
            case "CompareStore": form = 2; return true;
            default: return false;
        }
    }

    /// <summary>Handles barrier and Interlocked* free functions and the bit/out-parameter math intrinsics; false when <paramref name="name"/> is none of them.</summary>
    private bool TryComputeBuiltin(CallExpression c, string name, HlslType[] at, ExprInfo info, out HlslType result)
    {
        result = HlslType.Error;
        var flags = name switch
        {
            "GroupMemoryBarrier" => BarrierFlags.GroupMemory,
            "GroupMemoryBarrierWithGroupSync" => BarrierFlags.GroupMemory | BarrierFlags.GroupSync,
            "DeviceMemoryBarrier" => BarrierFlags.DeviceMemory,
            "DeviceMemoryBarrierWithGroupSync" => BarrierFlags.DeviceMemory | BarrierFlags.GroupSync,
            "AllMemoryBarrier" => BarrierFlags.GroupMemory | BarrierFlags.DeviceMemory,
            "AllMemoryBarrierWithGroupSync" => BarrierFlags.GroupMemory | BarrierFlags.DeviceMemory | BarrierFlags.GroupSync,
            _ => BarrierFlags.None,
        };
        if (flags != BarrierFlags.None)
        {
            if ((flags & BarrierFlags.GroupSync) != 0 && _fn is not null && _fn.GroupSyncSpan is null)
            {
                _fn.GroupSyncSpan = c.Span;
                _fn.GroupSyncName = name;
            }

            if (ArityOk(c, name, 0))
            {
                info.Builtin = new BuiltinCall { Kind = BuiltinKind.Barrier, Name = name, Extra = (int)flags, Result = HlslType.Void };
                result = HlslType.Void;
            }

            return true;
        }

        if (name.StartsWith("Interlocked", StringComparison.Ordinal) && TryAtomicOp(name.Substring("Interlocked".Length), out var aop, out int form))
        {
            result = CheckAtomic(c, name, at, info, aop, form, null);
            return true;
        }

        switch (name)
        {
            case "NonUniformResourceIndex":
            {
                if (!ArityOk(c, name, 1) || AnyError(at))
                {
                    return true;
                }

                if (at[0] is not ScalarType { IsInteger: true } index || TypeOps.BitWidth(index) != 32)
                {
                    Err(SemanticCodes.TypeMismatch, "'NonUniformResourceIndex' takes a 32-bit integer index (int or uint), not '" + at[0] + "'.", c.Arguments[0].Span);
                    return true;
                }

                info.Builtin = new BuiltinCall { Kind = BuiltinKind.Intrinsic, Ir = Intrinsic.NonUniformIndex, Name = name, ParamTypes = [index], Result = index };
                result = index;
                return true;
            }

            case "countbits" or "firstbithigh" or "firstbitlow" or "reversebits":
            {
                if (!ArityOk(c, name, 1) || AnyError(at))
                {
                    return true;
                }

                var s = at[0].ScalarElement;
                if (at[0] is MatrixType || s is null || !(s.IsInteger || s.ScalarKind == ScalarKind.Bool) || TypeOps.BitWidth(s) != 32)
                {
                    Err(SemanticCodes.TypeMismatch, "'" + name + "' works on 32-bit integers (int, uint and their vectors), not '" + at[0] + "'. Convert the argument with asuint() or a cast.", c.Arguments[0].Span);
                    return true;
                }

                // reversebits keeps the argument's type; the others count and always return uint (what DXC does).
                var pt = s.ScalarKind == ScalarKind.Bool ? TypeOps.Reshape(at[0], ScalarType.UInt) : at[0];
                var rt = name == "reversebits" ? pt : TypeOps.Reshape(pt, ScalarType.UInt);
                var ir = name switch
                {
                    "countbits" => Intrinsic.CountBits,
                    "firstbithigh" => Intrinsic.FirstBitHigh,
                    "firstbitlow" => Intrinsic.FirstBitLow,
                    _ => Intrinsic.ReverseBits,
                };
                info.Builtin = new BuiltinCall { Kind = BuiltinKind.Intrinsic, Ir = ir, Name = name, ParamTypes = [pt], Result = rt };
                result = rt;
                return true;
            }

            case "sincos" or "modf" or "frexp":
            {
                int expected = name == "sincos" ? 3 : 2;
                if (!ArityOk(c, name, expected) || AnyError(at))
                {
                    return true;
                }

                var u = Unify(c, name, at, true, [0]);
                if (u is null)
                {
                    return true;
                }

                if (u is MatrixType)
                {
                    Err(SemanticCodes.TypeMismatch, "'" + name + "' works on scalars and vectors, not matrices.", c.Arguments[0].Span);
                    return true;
                }

                var ptypes = new HlslType[expected];
                Array.Fill(ptypes, u);
                for (int i = 1; i < expected; i++)
                {
                    string why = WriteBlocker(c.Arguments[i]);
                    if (why.Length > 0)
                    {
                        Err(SemanticCodes.NotAssignable, "Argument " + Num(i + 1) + " of '" + name + "' receives a result and must be a variable: " + why, c.Arguments[i].Span);
                        return true;
                    }

                    if (at[i].ScalarElement is null || at[i] is MatrixType)
                    {
                        Err(SemanticCodes.TypeMismatch, "Argument " + Num(i + 1) + " of '" + name + "' must be a float variable, not '" + at[i] + "'.", c.Arguments[i].Span);
                        return true;
                    }
                }

                var ir = name == "sincos" ? Intrinsic.SinCos : name == "modf" ? Intrinsic.Modf : Intrinsic.Frexp;
                var res = name == "sincos" ? HlslType.Void : u;
                info.Builtin = new BuiltinCall { Kind = BuiltinKind.OutIntrinsic, Ir = ir, Name = name, ParamTypes = ptypes, Result = res, Extra = 1 };
                result = res;
                return true;
            }

            default:
                return false;
        }
    }

    private static string NameOfRoot(Expression e)
    {
        while (true)
        {
            switch (e)
            {
                case ParenthesizedExpression p:
                    e = p.Inner;
                    continue;
                case IndexExpression ix:
                    e = ix.Target;
                    continue;
                case MemberAccessExpression m:
                    e = m.Target;
                    continue;
                case NameExpression n:
                    return n.Name.ToString();
                default:
                    return "this expression";
            }
        }
    }

    private static AtomicDest ClassifyAtomicDest(Expression e, out string why, out Expression? textureExpr)
    {
        why = "";
        textureExpr = null;
        for (int guard = 0; guard < 4096; guard++)
        {
            switch (e)
            {
                case ParenthesizedExpression p:
                    e = p.Inner;
                    continue;
                case NameExpression n:
                    if (n.Info?.Symbol is VariableSymbol { Kind: VarKind.GroupShared })
                    {
                        return AtomicDest.Memory;
                    }

                    why = "'" + n.Name + "' is not groupshared memory or an element of a read-write buffer or texture.";
                    return AtomicDest.Invalid;
                case MemberAccessExpression m:
                    if (m.Target.Type is ResourceType)
                    {
                        why = "this member is not an element of a read-write buffer or texture.";
                        return AtomicDest.Invalid;
                    }

                    if (m.SwizzleComponents is { Count: not 1 })
                    {
                        why = "a swizzle of several components is not a single 32-bit integer.";
                        return AtomicDest.Invalid;
                    }

                    e = m.Target;
                    continue;
                case IndexExpression ix:
                {
                    if (ix.Target.Type is ResourceType rt)
                    {
                        if (rt.ResourceKind == ResourceTypeKind.RWStructuredBuffer)
                        {
                            return AtomicDest.Memory;
                        }

                        if (rt.IsTexture && rt.IsReadWrite)
                        {
                            textureExpr = ix.Target;
                            return AtomicDest.Texel;
                        }

                        why = rt.ResourceKind == ResourceTypeKind.StructuredBuffer
                            ? "a StructuredBuffer is read-only; declare it RWStructuredBuffer."
                            : "atomics need a groupshared variable or an element of a RWStructuredBuffer, RWTexture or RWByteAddressBuffer, not '" + rt + "'.";
                        return AtomicDest.Invalid;
                    }

                    e = ix.Target;
                    continue;
                }

                default:
                    why = "the target is not a variable.";
                    return AtomicDest.Invalid;
            }
        }

        why = "the expression is nested too deeply.";
        return AtomicDest.Invalid;
    }

    private static Expression? ResourceNameOf(Expression? e)
    {
        for (int guard = 0; guard < 4096 && e is not null; guard++)
        {
            switch (e)
            {
                case ParenthesizedExpression p:
                    e = p.Inner;
                    break;
                case IndexExpression ix:
                    e = ix.Target;
                    break;
                default:
                    return e;
            }
        }

        return null;
    }

    /// <summary>Checks <c>InterlockedXxx</c>; <paramref name="byteAddress"/> is set for the RWByteAddressBuffer methods, whose first argument is a byte offset.</summary>
    private HlslType CheckAtomic(CallExpression c, string name, HlslType[] at, ExprInfo info, AtomicOp op, int form, ResourceType? byteAddress)
    {
        int valueArgs = form == 0 ? 1 : 2;
        int baseCount = 1 + valueArgs;
        int min = form == 1 ? baseCount + 1 : baseCount;
        int max = form == 2 ? baseCount : baseCount + 1;
        int n = at.Length;
        if (n < min || n > max)
        {
            Err(SemanticCodes.ArgumentCount, "'" + name + "' takes " + (min == max ? Num(min) : Num(min) + " or " + Num(max)) + " arguments but the call passes " + Num(n) + "."
                + (form == 1 ? " The last one receives the original value." : ""), c.Span);
            return HlslType.Error;
        }

        if (AnyError(at))
        {
            return HlslType.Error;
        }

        HlslType target;
        var ptypes = new HlslType[n];
        if (byteAddress is not null)
        {
            if (!CheckConvertible(c.Arguments[0], ScalarType.UInt))
            {
                return HlslType.Error;
            }

            target = ScalarType.UInt;
            ptypes[0] = ScalarType.UInt;
        }
        else
        {
            var kind = ClassifyAtomicDest(c.Arguments[0], out string why, out var texExpr);
            if (kind == AtomicDest.Invalid)
            {
                Err(SemanticCodes.BadAtomic, "The first argument of '" + name + "' must be groupshared memory or an element of a read-write resource: " + why, c.Arguments[0].Span);
                return HlslType.Error;
            }

            if (!IsAtomicInt(at[0]))
            {
                string hint = at[0].ScalarElement is { IsFloatingPoint: true } ? " Float atomics are not supported; reinterpret the data as uint with asuint()/asfloat()." : "";
                Err(SemanticCodes.BadAtomic, "'" + name + "' works on int, uint, int64_t and uint64_t, but the first argument is '" + at[0] + "'." + hint, c.Arguments[0].Span);
                return HlslType.Error;
            }

            if (kind == AtomicDest.Texel && at[0] is ScalarType { ScalarKind: ScalarKind.Int64 or ScalarKind.UInt64 })
            {
                Err(SemanticCodes.BadAtomic, "64-bit atomics on a texture are not available; use a structured buffer of uint64_t.", c.Arguments[0].Span);
                return HlslType.Error;
            }

            if (kind == AtomicDest.Texel && ResourceNameOf(texExpr) is NameExpression { Info.Symbol: VariableSymbol { Resource: { } rd } } && rd.ImageFormat is not ("r32ui" or "r32i"))
            {
                Err(SemanticCodes.BadAtomic, "Atomics on the texture '" + NameOfRoot(c.Arguments[0]) + "' need a single-channel 32-bit integer format. Declare it with [[vk::image_format(\"r32ui\")]] (or \"r32i\" for int texels).", c.Arguments[0].Span);
                return HlslType.Error;
            }

            target = at[0];
            ptypes[0] = target;
        }

        for (int i = 1; i <= valueArgs; i++)
        {
            if (!CheckConvertible(c.Arguments[i], target))
            {
                return HlslType.Error;
            }

            ptypes[i] = target;
        }

        if (n > baseCount)
        {
            var outArg = c.Arguments[n - 1];
            string why = WriteBlocker(outArg);
            if (why.Length > 0)
            {
                Err(SemanticCodes.NotAssignable, "The last argument of '" + name + "' receives the original value and must be a variable: " + why, outArg.Span);
                return HlslType.Error;
            }

            if (!IsAtomicInt(at[n - 1]))
            {
                Err(SemanticCodes.TypeMismatch, "The original-value argument of '" + name + "' must be an integer variable, not '" + at[n - 1] + "'.", outArg.Span);
                return HlslType.Error;
            }

            ptypes[n - 1] = at[n - 1];
        }

        info.Builtin = new BuiltinCall { Kind = BuiltinKind.Atomic, Atomic = op, Name = name, Extra = form, HasOffset = byteAddress is not null, ParamTypes = ptypes, Result = HlslType.Void };
        return HlslType.Void;
    }

    // ------------------------------------------------------------ byte-address buffers

    private static bool ByteAddressable(HlslType t, out string why)
    {
        why = "";
        switch (t)
        {
            case ScalarType s:
                if (s.ScalarKind == ScalarKind.Bool || TypeOps.BitWidth(s) != 32)
                {
                    why = "only 32-bit int, uint and float values can be read from a byte-address buffer";
                    return false;
                }

                return true;
            case VectorType v:
                return ByteAddressable(v.Element, out why);
            case StructType st:
                foreach (var f in st.Fields)
                {
                    if (!ByteAddressable(f.Type, out why))
                    {
                        return false;
                    }
                }

                return true;
            default:
                why = "matrices, arrays and resources are not supported; load the components with Load2/Load3/Load4 or a struct of scalars and vectors";
                return false;
        }
    }

    private HlslType CheckByteAddressMethod(CallExpression c, MemberAccessExpression ma, ResourceType rt, string name, HlslType[] at, ExprInfo info)
    {
        bool rw = rt.IsReadWrite;
        if (name == "GetDimensions")
        {
            return CheckGetDimensions(c, rt, at, info, 1, false);
        }

        if (name.StartsWith("Interlocked", StringComparison.Ordinal) && TryAtomicOp(name.Substring("Interlocked".Length), out var aop, out int form))
        {
            if (!rw)
            {
                Err(SemanticCodes.BadAtomic, "'" + name + "' writes the buffer, but '" + rt + "' is read-only. Declare it RWByteAddressBuffer.", ma.Member.Span);
                return HlslType.Error;
            }

            if (c.TemplateArguments.Count > 0)
            {
                Err(SemanticCodes.BadAtomic, "'" + name + "' takes no template arguments.", c.Span);
                return HlslType.Error;
            }

            return CheckAtomic(c, name, at, info, aop, form, rt);
        }

        bool isLoad = name.StartsWith("Load", StringComparison.Ordinal);
        bool isStore = name.StartsWith("Store", StringComparison.Ordinal);
        string tail = isLoad ? name.Substring(4) : isStore ? name.Substring(5) : "x";
        int comps = tail.Length == 0 ? 1 : tail is "2" or "3" or "4" ? tail[0] - '0' : 0;
        if (!(isLoad || isStore) || comps == 0)
        {
            Err(SemanticCodes.BadMember, "The type '" + rt + "' has no method '" + name + "'. Byte-address buffers have Load, Load2, Load3, Load4, Store, Store2, Store3, Store4, Load<T>, Store<T> and GetDimensions"
                + (rw ? ", and the Interlocked* methods." : "."), ma.Member.Span);
            return HlslType.Error;
        }

        if (isStore && !rw)
        {
            Err(SemanticCodes.NotAssignable, "'" + name + "' writes the buffer, but '" + rt + "' is read-only. Declare it RWByteAddressBuffer.", ma.Member.Span);
            return HlslType.Error;
        }

        HlslType valueType;
        if (c.TemplateArguments.Count > 0)
        {
            if (comps != 1 || c.TemplateArguments.Count != 1 || c.TemplateArguments[0].Type is not { } ts)
            {
                Err(SemanticCodes.BadType, "'" + name + "' takes one type argument, for example " + (isLoad ? "Load<float4>(offset)" : "Store<float4>(offset, value)") + ".", c.Span);
                return HlslType.Error;
            }

            valueType = ResolveType(ts);
            if (valueType.IsError)
            {
                return HlslType.Error;
            }

            if (!ByteAddressable(valueType, out string why))
            {
                Err(SemanticCodes.Unsupported, "'" + name + "<" + valueType + ">' is not supported: " + why + ".", c.Span);
                return HlslType.Error;
            }
        }
        else
        {
            valueType = comps == 1 ? ScalarType.UInt : HlslType.Vector(ScalarType.UInt, comps);
        }

        int args = isLoad ? 1 : 2;
        if (at.Length != args)
        {
            Err(SemanticCodes.ArgumentCount, "'" + name + "' takes " + Num(args) + " argument" + (args == 1 ? "" : "s") + " (" + (isLoad ? "the byte offset" : "the byte offset and the value") + ") but the call passes " + Num(at.Length) + ".", c.Span);
            return HlslType.Error;
        }

        if (!CheckConvertible(c.Arguments[0], ScalarType.UInt))
        {
            return HlslType.Error;
        }

        if (isLoad)
        {
            info.Builtin = new BuiltinCall { Kind = BuiltinKind.ByteAddress, Name = name, ParamTypes = [ScalarType.UInt], Result = valueType, Extra = 0 };
            return valueType;
        }

        if (!CheckConvertible(c.Arguments[1], valueType))
        {
            return HlslType.Error;
        }

        info.Builtin = new BuiltinCall { Kind = BuiltinKind.ByteAddress, Name = name, ParamTypes = [ScalarType.UInt, valueType], Result = HlslType.Void, Extra = 1 };
        return HlslType.Void;
    }
}
