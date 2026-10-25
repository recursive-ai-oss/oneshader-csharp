using System;
using System.Collections.Generic;
using OneShader.Diagnostics;
using OneShader.IR;
using OneShader.Syntax;
using OneShader.Types;

namespace OneShader.Semantics;

internal sealed partial class Checker
{
    private static int CoordDims(ResourceTypeKind k) => k switch
    {
        ResourceTypeKind.Texture1D or ResourceTypeKind.RWTexture1D => 1,
        ResourceTypeKind.Texture1DArray or ResourceTypeKind.RWTexture1DArray or ResourceTypeKind.Texture2D or ResourceTypeKind.RWTexture2D
            or ResourceTypeKind.Texture2DMS => 2,
        ResourceTypeKind.Texture2DArray or ResourceTypeKind.RWTexture2DArray or ResourceTypeKind.Texture3D or ResourceTypeKind.RWTexture3D
            or ResourceTypeKind.TextureCube or ResourceTypeKind.Texture2DMSArray => 3,
        ResourceTypeKind.TextureCubeArray => 4,
        _ => 0,
    };

    /// <summary>Components of a texture's size query (width, height, depth or layers).</summary>
    private static int SizeDims(ResourceTypeKind k) => k switch
    {
        ResourceTypeKind.Texture1D or ResourceTypeKind.RWTexture1D => 1,
        ResourceTypeKind.Texture1DArray or ResourceTypeKind.RWTexture1DArray or ResourceTypeKind.Texture2D or ResourceTypeKind.RWTexture2D
            or ResourceTypeKind.TextureCube or ResourceTypeKind.Texture2DMS => 2,
        ResourceTypeKind.Texture2DArray or ResourceTypeKind.RWTexture2DArray or ResourceTypeKind.Texture3D or ResourceTypeKind.RWTexture3D
            or ResourceTypeKind.TextureCubeArray or ResourceTypeKind.Texture2DMSArray => 3,
        _ => 0,
    };

    /// <summary>Components of a texel offset (the layer is not offset).</summary>
    private static int OffsetDims(ResourceTypeKind k) => k switch
    {
        ResourceTypeKind.Texture1D or ResourceTypeKind.Texture1DArray => 1,
        ResourceTypeKind.Texture2D or ResourceTypeKind.Texture2DArray => 2,
        ResourceTypeKind.Texture3D => 3,
        _ => 0,
    };

    private static HlslType FloatVec(int n) => n == 1 ? ScalarType.Float : HlslType.Vector(ScalarType.Float, n);

    private static HlslType IntVec(int n) => n == 1 ? ScalarType.Int : HlslType.Vector(ScalarType.Int, n);

    private HlslType CheckMethodCall(CallExpression c, MemberAccessExpression ma, ExprInfo info)
    {
        var tt = CheckExpr(ma.Target);
        ma.Type = HlslType.Error;
        var at = CheckArgs(c);
        string name = ma.Member.Text;
        if (tt.IsError || AnyError(at))
        {
            return HlslType.Error;
        }

        if (tt is ArrayType { Element: ResourceType })
        {
            Err(SemanticCodes.BadOperator, "Index the resource array first, for example " + name + "[0]." + name + "(...).", ma.Span);
            return HlslType.Error;
        }

        if (tt is StructType structTarget)
        {
            return CheckStructMethodCall(c, ma, structTarget, at, info);
        }

        if (tt is not ResourceType rt)
        {
            Err(SemanticCodes.BadMember, "The type '" + tt + "' has no method '" + name + "'.", ma.Member.Span);
            return HlslType.Error;
        }

        if (rt.IsTexture)
        {
            return CheckTextureMethod(c, ma, rt, name, at, info);
        }

        if (rt.ResourceKind is ResourceTypeKind.StructuredBuffer or ResourceTypeKind.RWStructuredBuffer or ResourceTypeKind.Buffer or ResourceTypeKind.RWBuffer)
        {
            if (name == "Load" && ArityOk(c, name, 1))
            {
                CheckConvertible(c.Arguments[0], ScalarType.Int);
                info.Builtin = new BuiltinCall { Kind = BuiltinKind.BufferLoad, Name = name, ParamTypes = [ScalarType.Int], Result = rt.Element ?? HlslType.Error };
                return info.Builtin.Result;
            }

            if (name == "GetDimensions")
            {
                return CheckGetDimensions(c, rt, at, info, rt.ResourceKind is ResourceTypeKind.StructuredBuffer or ResourceTypeKind.RWStructuredBuffer ? 2 : 1, false);
            }
        }

        if (rt.ResourceKind is ResourceTypeKind.ByteAddressBuffer or ResourceTypeKind.RWByteAddressBuffer)
        {
            return CheckByteAddressMethod(c, ma, rt, name, at, info);
        }

        Err(SemanticCodes.Unsupported, "The method '" + name + "' of '" + rt + "' is not supported.", ma.Member.Span);
        return HlslType.Error;
    }

    private HlslType CheckGetDimensions(CallExpression c, ResourceType rt, HlslType[] at, ExprInfo info, int sizeComponents, bool texture)
    {
        int n = at.Length;
        bool mip = false;
        int expectedMin = sizeComponents;
        if (texture && n == sizeComponents + 2)
        {
            mip = true;
        }
        else if (texture && n != sizeComponents && n != sizeComponents + 1)
        {
            Err(SemanticCodes.ArgumentCount, "GetDimensions on '" + rt + "' takes " + Num(sizeComponents) + " to " + Num(sizeComponents + 2) + " arguments but the call passes " + Num(n) + ".", c.Span);
            return HlslType.Error;
        }
        else if (!texture && n != expectedMin)
        {
            Err(SemanticCodes.ArgumentCount, "GetDimensions on '" + rt + "' takes " + Num(expectedMin) + " argument" + (expectedMin == 1 ? "" : "s") + " but the call passes " + Num(n) + ".", c.Span);
            return HlslType.Error;
        }

        var ptypes = new HlslType[n];
        for (int i = 0; i < n; i++)
        {
            if (mip && i == 0)
            {
                CheckConvertible(c.Arguments[0], ScalarType.UInt);
                ptypes[0] = ScalarType.UInt;
                continue;
            }

            string why = WriteBlocker(c.Arguments[i]);
            if (why.Length > 0)
            {
                Err(SemanticCodes.NotAssignable, "Argument " + Num(i + 1) + " of GetDimensions receives a result and must be a variable: " + why, c.Arguments[i].Span);
            }

            if (at[i].ScalarElement is not { } s || at[i] is not ScalarType)
            {
                Err(SemanticCodes.TypeMismatch, "Argument " + Num(i + 1) + " of GetDimensions must be a uint or float variable, not '" + at[i] + "'.", c.Arguments[i].Span);
                return HlslType.Error;
            }

            ptypes[i] = at[i];
        }

        info.Builtin = new BuiltinCall { Kind = BuiltinKind.GetDimensions, Name = "GetDimensions", ParamTypes = ptypes, Result = HlslType.Void, Extra = sizeComponents, HasOffset = mip };
        return HlslType.Void;
    }

    private HlslType CheckTextureMethod(CallExpression c, MemberAccessExpression ma, ResourceType rt, string name, HlslType[] at, ExprInfo info)
    {
        var rk = rt.ResourceKind;
        var elem = rt.Element ?? HlslType.Error;
        int dims = CoordDims(rk);
        bool rw = rt.IsReadWrite;
        bool ms = rk is ResourceTypeKind.Texture2DMS or ResourceTypeKind.Texture2DMSArray;
        int offDims = OffsetDims(rk);
        var coord = FloatVec(dims);
        HlslType? offsetType = offDims > 0 ? IntVec(offDims) : null;
        int n = at.Length;

        BuiltinCall Make(Opcode op, HlslType result, HlslType[] ptypes, bool hasOffset = false, int extra = 0) =>
            new() { Kind = BuiltinKind.Texture, TextureOp = op, Name = name, ParamTypes = ptypes, Result = result, HasOffset = hasOffset, Extra = extra };

        bool ArgRange(int min, int max)
        {
            if (n >= min && n <= max)
            {
                return true;
            }

            Err(SemanticCodes.ArgumentCount, "The method '" + name + "' of '" + rt + "' takes " + (min == max ? Num(min) : Num(min) + " to " + Num(max)) + " arguments but the call passes " + Num(n) + ".", c.Span);
            return false;
        }

        void Convertibles(HlslType[] pts)
        {
            for (int i = 0; i < pts.Length && i < n; i++)
            {
                if (pts[i] is SamplerType or ResourceType)
                {
                    if (!at[i].Equals(pts[i]))
                    {
                        Err(SemanticCodes.TypeMismatch, "Argument " + Num(i + 1) + " of '" + name + "' must be a " + pts[i] + ", not '" + at[i] + "'.", c.Arguments[i].Span);
                    }
                }
                else
                {
                    CheckConvertible(c.Arguments[i], pts[i]);
                }
            }
        }

        if (rw || ms)
        {
            if (name == "Load")
            {
                if (ArgRange(1, ms ? 2 : 1))
                {
                    var pts = ms ? new HlslType[] { IntVec(dims), ScalarType.Int } : [IntVec(dims)];
                    Convertibles(pts);
                    info.Builtin = Make(Opcode.TextureLoad, elem, pts, false, dims);
                    return elem;
                }

                return HlslType.Error;
            }

            if (name == "GetDimensions")
            {
                return CheckGetDimensions(c, rt, at, info, SizeDims(rk), !rw);
            }

            Err(SemanticCodes.Unsupported, "The method '" + name + "' of '" + rt + "' is not supported.", ma.Member.Span);
            return HlslType.Error;
        }

        switch (name)
        {
            case "Sample":
            {
                if (!ArgRange(2, 3) || (n == 3 && offsetType is null))
                {
                    return HlslType.Error;
                }

                var pts = n == 3 ? new HlslType[] { SamplerType.State, coord, offsetType! } : [SamplerType.State, coord];
                Convertibles(pts);
                info.Builtin = Make(Opcode.TextureSample, elem, pts, n == 3);
                return elem;
            }

            case "SampleBias" or "SampleLevel":
            {
                if (!ArgRange(3, 4) || (n == 4 && offsetType is null))
                {
                    return HlslType.Error;
                }

                var pts = n == 4 ? new HlslType[] { SamplerType.State, coord, ScalarType.Float, offsetType! } : [SamplerType.State, coord, ScalarType.Float];
                Convertibles(pts);
                info.Builtin = Make(name == "SampleBias" ? Opcode.TextureSampleBias : Opcode.TextureSampleLevel, elem, pts, n == 4);
                return elem;
            }

            case "SampleGrad":
            {
                if (!ArgRange(4, 5) || (n == 5 && offsetType is null))
                {
                    return HlslType.Error;
                }

                int gd = rk is ResourceTypeKind.TextureCube or ResourceTypeKind.TextureCubeArray ? 3 : rk is ResourceTypeKind.Texture1DArray ? 1 : rk is ResourceTypeKind.Texture2DArray ? 2 : dims;
                var pts = n == 5 ? new HlslType[] { SamplerType.State, coord, FloatVec(gd), FloatVec(gd), offsetType! } : [SamplerType.State, coord, FloatVec(gd), FloatVec(gd)];
                Convertibles(pts);
                info.Builtin = Make(Opcode.TextureSampleGrad, elem, pts, n == 5);
                return elem;
            }

            case "SampleCmp" or "SampleCmpLevelZero":
            {
                if (!ArgRange(3, 4) || (n == 4 && offsetType is null))
                {
                    return HlslType.Error;
                }

                var pts = n == 4 ? new HlslType[] { SamplerType.Comparison, coord, ScalarType.Float, offsetType! } : [SamplerType.Comparison, coord, ScalarType.Float];
                Convertibles(pts);
                info.Builtin = Make(name == "SampleCmp" ? Opcode.TextureSampleCompare : Opcode.TextureSampleCompareLevelZero, ScalarType.Float, pts, n == 4);
                return ScalarType.Float;
            }

            case "Gather" or "GatherRed" or "GatherGreen" or "GatherBlue" or "GatherAlpha":
            {
                if (!ArgRange(2, 3) || (n == 3 && offsetType is null))
                {
                    return HlslType.Error;
                }

                var pts = n == 3 ? new HlslType[] { SamplerType.State, coord, offsetType! } : [SamplerType.State, coord];
                Convertibles(pts);
                int comp = name switch { "GatherGreen" => 1, "GatherBlue" => 2, "GatherAlpha" => 3, _ => 0 };
                var res = HlslType.Vector(elem.ScalarElement ?? ScalarType.Float, 4);
                info.Builtin = Make(Opcode.TextureGather, res, pts, n == 3, comp);
                return res;
            }

            case "Load":
            {
                if (!ArgRange(1, 2) || (n == 2 && offsetType is null))
                {
                    return HlslType.Error;
                }

                var pts = n == 2 ? new HlslType[] { IntVec(dims + 1), offsetType! } : [IntVec(dims + 1)];
                Convertibles(pts);
                info.Builtin = Make(Opcode.TextureLoad, elem, pts, n == 2, dims);
                return elem;
            }

            case "GetDimensions":
                return CheckGetDimensions(c, rt, at, info, SizeDims(rk), true);
            case "CalculateLevelOfDetail" or "CalculateLevelOfDetailUnclamped":
            {
                if (!ArgRange(2, 2))
                {
                    return HlslType.Error;
                }

                var pts = new HlslType[] { SamplerType.State, coord };
                Convertibles(pts);
                info.Builtin = Make(Opcode.TextureQueryLod, ScalarType.Float, pts, false, name == "CalculateLevelOfDetail" ? 0 : 1);
                return ScalarType.Float;
            }

            default:
                Err(SemanticCodes.BadMember, "The type '" + rt + "' has no method '" + name + "'.", ma.Member.Span);
                return HlslType.Error;
        }
    }
}
