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
    private static readonly Dictionary<Intrinsic, string> Unary = new()
    {
        [Intrinsic.Abs] = "abs",
        [Intrinsic.Acos] = "acos",
        [Intrinsic.Asin] = "asin",
        [Intrinsic.Atan] = "atan",
        [Intrinsic.Ceil] = "ceil",
        [Intrinsic.Cos] = "cos",
        [Intrinsic.Cosh] = "cosh",
        [Intrinsic.Degrees] = "degrees",
        [Intrinsic.Determinant] = "determinant",
        [Intrinsic.Exp] = "exp",
        [Intrinsic.Exp2] = "exp2",
        [Intrinsic.Floor] = "floor",
        [Intrinsic.Frac] = "fract",
        [Intrinsic.Length] = "length",
        [Intrinsic.Log] = "log",
        [Intrinsic.Log2] = "log2",
        [Intrinsic.Normalize] = "normalize",
        [Intrinsic.Radians] = "radians",
        [Intrinsic.Round] = "round", // WGSL rounds half to even, as HLSL does.
        [Intrinsic.Rsqrt] = "inverseSqrt",
        [Intrinsic.Saturate] = "saturate",
        [Intrinsic.Sin] = "sin",
        [Intrinsic.Sinh] = "sinh",
        [Intrinsic.Sqrt] = "sqrt",
        [Intrinsic.Tan] = "tan",
        [Intrinsic.Tanh] = "tanh",
        [Intrinsic.Trunc] = "trunc",
        [Intrinsic.Ddx] = "dpdx",
        [Intrinsic.DdxCoarse] = "dpdxCoarse",
        [Intrinsic.DdxFine] = "dpdxFine",
        [Intrinsic.Ddy] = "dpdy",
        [Intrinsic.DdyCoarse] = "dpdyCoarse",
        [Intrinsic.DdyFine] = "dpdyFine",
        [Intrinsic.Fwidth] = "fwidth",
    };

    /// <summary>Bit operations whose WGSL result has the operand's type, converted afterwards to the HLSL result type.</summary>
    private static readonly Dictionary<Intrinsic, string> BitOps = new()
    {
        [Intrinsic.CountBits] = "countOneBits",
        [Intrinsic.ReverseBits] = "reverseBits",
        [Intrinsic.FirstBitHigh] = "firstLeadingBit",
        [Intrinsic.FirstBitLow] = "firstTrailingBit",
    };

    private string Fit(Instruction i, int n)
    {
        var op = i.Operands[n];
        string e = Val(op);
        int rc = Count(i.Type);
        if (Count(op.Type) == 1 && rc > 1 && op.Type.ScalarElement is { } s)
        {
            return TypeName(Shape(s, rc)) + "(" + e + ")";
        }

        return e;
    }

    private string Arg(Instruction i, int n) => Val(i.Operands[n]);

    private void EmitIntrinsic(Instruction i)
    {
        var rt = i.Type;
        int rc = Count(rt);
        var scalar = rt.ScalarElement ?? ScalarType.Float;
        NK k = i.Operands.Count > 0 ? KindOf(i.Operands[0].Type) : NK.Other;
        if (i.Intrinsic is Intrinsic.Normalize or Intrinsic.Reflect or Intrinsic.Refract or Intrinsic.FaceForward && Count(i.Operands[0].Type) == 1)
        {
            // WGSL defines these for vectors only; HLSL accepts scalars. Widen to a vec2 with a zero second lane and keep lane x.
            string wide(int n) => "vec2(" + Arg(i, n) + ", 0.0)";
            string call = i.Intrinsic switch
            {
                Intrinsic.Normalize => "normalize(" + wide(0) + ")",
                Intrinsic.Reflect => "reflect(" + wide(0) + ", " + wide(1) + ")",
                Intrinsic.Refract => "refract(" + wide(0) + ", " + wide(1) + ", " + Arg(i, 2) + ")",
                _ => "faceForward(" + wide(0) + ", " + wide(1) + ", " + wide(2) + ")",
            };
            Define(i, call + ".x");
            return;
        }

        if (Unary.TryGetValue(i.Intrinsic, out var unary))
        {
            Define(i, unary + "(" + Arg(i, 0) + ")");
            return;
        }

        if (BitOps.TryGetValue(i.Intrinsic, out var bitOp))
        {
            string call = bitOp + "(" + Arg(i, 0) + ")";
            Define(i, ConvertExpr(call, i.Operands[0].Type, rt));
            return;
        }

        switch (i.Intrinsic)
        {
            case Intrinsic.Atan2:
                Define(i, "atan2(" + Arg(i, 0) + ", " + Fit(i, 1) + ")");
                break;
            case Intrinsic.Cross:
            case Intrinsic.Distance:
            case Intrinsic.Reflect:
                Define(i, IntrinsicName(i.Intrinsic) + "(" + Arg(i, 0) + ", " + Arg(i, 1) + ")");
                break;
            case Intrinsic.Pow:
                Define(i, "pow(" + Arg(i, 0) + ", " + Fit(i, 1) + ")");
                break;
            case Intrinsic.Fma:
                Define(i, "fma(" + Arg(i, 0) + ", " + Arg(i, 1) + ", " + Arg(i, 2) + ")");
                break;
            case Intrinsic.FaceForward:
                Define(i, "faceForward(" + Arg(i, 0) + ", " + Arg(i, 1) + ", " + Arg(i, 2) + ")");
                break;
            case Intrinsic.Refract:
                Define(i, "refract(" + Arg(i, 0) + ", " + Arg(i, 1) + ", " + Arg(i, 2) + ")");
                break;
            case Intrinsic.Clamp:
                Define(i, "clamp(" + Fit(i, 0) + ", " + Fit(i, 1) + ", " + Fit(i, 2) + ")");
                break;
            case Intrinsic.Min:
                Define(i, "min(" + Fit(i, 0) + ", " + Fit(i, 1) + ")");
                break;
            case Intrinsic.Max:
                Define(i, "max(" + Fit(i, 0) + ", " + Fit(i, 1) + ")");
                break;
            case Intrinsic.Lerp:
                Define(i, "mix(" + Fit(i, 0) + ", " + Fit(i, 1) + ", " + Fit(i, 2) + ")");
                break;
            case Intrinsic.SmoothStep:
                Define(i, "smoothstep(" + Fit(i, 0) + ", " + Fit(i, 1) + ", " + Fit(i, 2) + ")");
                break;
            case Intrinsic.NonUniformIndex:
                // Only a hint for backends with descriptor indexing; the value itself is the index.
                Define(i, Arg(i, 0));
                break;
            case Intrinsic.Step:
                Define(i, "step(" + Fit(i, 0) + ", " + Fit(i, 1) + ")");
                break;
            case Intrinsic.Rcp:
                Define(i, "1.0f / " + Arg(i, 0));
                break;
            case Intrinsic.Log10:
                // WGSL has no log10: log2(x) * log10(2).
                Define(i, "log2(" + Arg(i, 0) + ") * " + FloatLiteral(BitConverter.SingleToUInt32Bits((float)0.30102999566398114)));
                break;
            case Intrinsic.Sign:
                EmitSign(i, k);
                break;
            case Intrinsic.Dot:
                Define(i, Count(i.Operands[0].Type) == 1 ? Arg(i, 0) + " * " + Arg(i, 1) : "dot(" + Arg(i, 0) + ", " + Arg(i, 1) + ")");
                break;
            case Intrinsic.All or Intrinsic.Any:
            {
                var src = i.Operands[0].Type;
                string v = Arg(i, 0);
                if (k != NK.Bool)
                {
                    v = ConvertExpr(v, src, Shape(ScalarType.Bool, Count(src)));
                }

                if (Count(src) == 1)
                {
                    Define(i, v);
                }
                else
                {
                    Define(i, (i.Intrinsic == Intrinsic.All ? "all(" : "any(") + v + ")");
                }

                break;
            }

            case Intrinsic.IsNan or Intrinsic.IsInf or Intrinsic.IsFinite:
                EmitFloatClass(i);
                break;
            case Intrinsic.Ldexp:
            {
                // ldexp(x, e) = x * 2^e with e a float in HLSL.
                var x = i.Operands[0].Type;
                string e = ConvertExpr(Arg(i, 1), i.Operands[1].Type, x.ScalarElement is { } es && Count(i.Operands[1].Type) == rc ? Shape(es, rc) : i.Operands[1].Type);
                Define(i, Arg(i, 0) + " * exp2(" + e + ")");
                break;
            }

            case Intrinsic.Frexp or Intrinsic.Modf:
                EmitFrexpModf(i);
                break;
            case Intrinsic.SinCos:
            {
                var xt = i.Operands[0].Type;
                string x = Simple(Arg(i, 0));
                StoreThrough(i.Operands[1], "sin(" + x + ")", xt);
                StoreThrough(i.Operands[2], "cos(" + x + ")", xt);
                break;
            }

            case Intrinsic.PackHalf:
                EmitHalf(i, true);
                break;
            case Intrinsic.UnpackHalf:
                EmitHalf(i, false);
                break;
            default:
                Unsupported(i.Intrinsic >= Intrinsic.WaveGetLaneCount
                    ? "Wave and quad intrinsics (" + i.Intrinsic + ") are not available in WGSL; WebGPU's subgroup feature is optional and not used by this backend."
                    : "The intrinsic '" + i.Intrinsic + "' is not supported by the WGSL backend.", i.Span);
                break;
        }

        _ = scalar;
    }

    private static string IntrinsicName(Intrinsic i) => i switch
    {
        Intrinsic.Cross => "cross",
        Intrinsic.Distance => "distance",
        _ => "reflect",
    };

    private void StoreThrough(IrValue pointer, string value, HlslType valueType)
    {
        if (MemOf(pointer) is not { } m)
        {
            Fail(WgslCodes.MalformedIr, "An out parameter does not address a variable.", _span);
            return;
        }

        var target = pointer.Type is PointerType pt ? pt.Pointee : valueType;
        WriteMem(m, ConvertExpr(value, valueType, target));
    }

    private void EmitSign(Instruction i, NK k)
    {
        var src = i.Operands[0].Type;
        string v = Arg(i, 0);
        int n = Count(src);
        string natural;
        if (k == NK.UInt)
        {
            string zero = n == 1 ? "0u" : "vec" + Inv(n) + "<u32>(0u)";
            string one = n == 1 ? "1u" : "vec" + Inv(n) + "<u32>(1u)";
            natural = "select(" + zero + ", " + one + ", " + v + " != " + zero + ")";
        }
        else
        {
            natural = "sign(" + v + ")";
        }

        Define(i, ConvertExpr(natural, src, i.Type));
    }

    /// <summary>
    /// WGSL has no isnan, isinf or isfinite, so test the exponent bits. Implementations may assume no NaN or infinity
    /// ever occurs (WGSL 15.7.5), in which case these tests can be optimized away; HLSL has no such licence.
    /// </summary>
    private void EmitFloatClass(Instruction i)
    {
        var src = i.Operands[0].Type;
        int n = Count(src);
        string u = n == 1 ? "u32" : "vec" + Inv(n) + "<u32>";
        string Mask(string hex) => u + "(" + hex + "u)";
        string bits = Temp("bitcast<" + u + ">(" + Arg(i, 0) + ")");
        string expr = i.Intrinsic switch
        {
            Intrinsic.IsNan => "(" + bits + " & " + Mask("0x7fffffff") + ") > " + Mask("0x7f800000"),
            Intrinsic.IsInf => "(" + bits + " & " + Mask("0x7fffffff") + ") == " + Mask("0x7f800000"),
            _ => "(" + bits + " & " + Mask("0x7f800000") + ") != " + Mask("0x7f800000"),
        };
        Define(i, expr);
    }

    private void EmitFrexpModf(Instruction i)
    {
        bool frexp = i.Intrinsic == Intrinsic.Frexp;
        var xt = i.Operands[0].Type;
        int n = Count(xt);
        string r = Temp((frexp ? "frexp(" : "modf(") + Arg(i, 0) + ")");
        if (frexp)
        {
            StoreThrough(i.Operands[1], r + ".exp", Shape(ScalarType.Int, n));
            Define(i, r + ".fract");
        }
        else
        {
            StoreThrough(i.Operands[1], r + ".whole", xt);
            Define(i, r + ".fract");
        }
    }

    private void EmitHalf(Instruction i, bool pack)
    {
        int n = Count(i.Type);
        var src = i.Operands[0].Type;
        string x = Simple(Arg(i, 0));
        var parts = new string[n];
        for (int c = 0; c < n; c++)
        {
            string comp = n == 1 ? x : x + "." + "xyzw"[c];
            if (pack)
            {
                parts[c] = "pack2x16float(vec2<f32>(" + comp + ", 0.0f))";
            }
            else
            {
                string bits = ConvertExpr(comp, src.ScalarElement!, ScalarType.UInt);
                parts[c] = "unpack2x16float(" + bits + ").x";
            }
        }

        Define(i, n == 1 ? parts[0] : TypeName(i.Type) + "(" + string.Join(", ", parts) + ")");
    }

    // ---- textures and byte-address buffers ---------------------------------------------------------------------

    private readonly HashSet<GlobalVariable> _depthTextures = [];
    private readonly Dictionary<GlobalVariable, (bool Load, bool Store)> _storageUse = [];

    private static ResourceType? ResourceOf(IrValue pointer) =>
        pointer.Type is PointerType { Pointee: ResourceType rt } ? rt : null;

    private static ScalarType SampledScalar(ResourceType rt)
    {
        var e = rt.Element?.ScalarElement ?? ScalarType.Float;
        return e.IsFloatingPoint || e.ScalarKind == ScalarKind.Bool ? ScalarType.Float : e.IsSignedInteger ? ScalarType.Int : ScalarType.UInt;
    }

    private static bool IsArrayed(ResourceType rt) => rt.ResourceKind is ResourceTypeKind.Texture1DArray or ResourceTypeKind.Texture2DArray
        or ResourceTypeKind.Texture2DMSArray or ResourceTypeKind.TextureCubeArray or ResourceTypeKind.RWTexture1DArray or ResourceTypeKind.RWTexture2DArray;

    private static int SpatialDims(ResourceType rt) => rt.ResourceKind switch
    {
        ResourceTypeKind.Texture1D or ResourceTypeKind.Texture1DArray or ResourceTypeKind.RWTexture1D or ResourceTypeKind.RWTexture1DArray => 1,
        ResourceTypeKind.Texture3D or ResourceTypeKind.RWTexture3D or ResourceTypeKind.TextureCube or ResourceTypeKind.TextureCubeArray => 3,
        _ => 2,
    };

    private static string Call(string fn, params string?[] args)
    {
        var sb = new StringBuilder(fn).Append('(');
        bool first = true;
        foreach (var a in args)
        {
            if (a is null)
            {
                continue;
            }

            if (!first)
            {
                sb.Append(", ");
            }

            sb.Append(a);
            first = false;
        }

        return sb.Append(')').ToString();
    }

    private static (string Coords, string? Layer) SplitCoords(string coord, HlslType coordType, ResourceType rt, bool isFloat)
    {
        if (!IsArrayed(rt))
        {
            return (coord, null);
        }

        int n = Count(coordType);
        string p = Paren(coord);
        string layer = p + "." + "xyzw"[n - 1];
        string spatial = n - 1 == 1 ? p + ".x" : p + "." + Swizzle(n - 1);
        return (spatial, isFloat ? "i32(round(" + layer + "))" : layer);
    }

    private string? OffsetArg(Instruction i, int start, ResourceType rt)
    {
        int n = i.Immediates.Count - start;
        if (n <= 0)
        {
            return null;
        }

        if (rt.ResourceKind is ResourceTypeKind.TextureCube or ResourceTypeKind.TextureCubeArray or ResourceTypeKind.Texture1D or ResourceTypeKind.Texture1DArray)
        {
            Unsupported("A texel offset on a " + rt.ResourceKind + " is not available in WGSL.", i.Span);
            return null;
        }

        var parts = new string[n];
        for (int k = 0; k < n; k++)
        {
            parts[k] = Inv(i.Immediates[start + k]) + "i";
        }

        return n == 1 ? parts[0] : "vec" + Inv(n) + "<i32>(" + string.Join(", ", parts) + ")";
    }

    /// <summary>Cuts a four-component texel (or a depth texture's single value) down to the type the shader asked for.</summary>
    private string FitTexel(string raw, ScalarType sampled, HlslType want, bool single)
    {
        string v4 = single ? "vec4<f32>(" + raw + ", 0.0f, 0.0f, 1.0f)" : raw;
        int n = Count(want);
        string cut = n == 4 ? v4 : n == 1 ? Paren(v4) + ".x" : Paren(v4) + "." + Swizzle(n);
        var ws = want.ScalarElement ?? sampled;
        return ScalarName(ws) == ScalarName(sampled) ? cut : ConvertExpr(cut, Shape(sampled, n), Shape(ws, n));
    }

    private void EmitImageOrBuffer(Instruction i)
    {
        if (i.Op is Opcode.ByteAddressLoad or Opcode.ByteAddressStore)
        {
            EmitByteAddress(i);
            return;
        }

        var ops = i.Operands;
        var rt = ResourceOf(ops[0]);
        if (rt is null || ops[0] is not GlobalVariable)
        {
            Fail(WgslCodes.MalformedIr, "A texture operation does not address a texture resource.", i.Span);
            return;
        }

        string tex = Val(ops[0]);
        bool depth = IsDepth(ops[0]);
        var sampled = SampledScalar(rt);
        switch (i.Op)
        {
            case Opcode.TextureSample or Opcode.TextureSampleBias or Opcode.TextureSampleLevel or Opcode.TextureSampleGrad:
            {
                var (coords, layer) = SplitCoords(Val(ops[2]), ops[2].Type, rt, true);
                string? offset = OffsetArg(i, 0, rt);
                string raw;
                switch (i.Op)
                {
                    case Opcode.TextureSample:
                        raw = Call("textureSample", tex, Val(ops[1]), coords, layer, offset);
                        break;
                    case Opcode.TextureSampleBias:
                        raw = Call("textureSampleBias", tex, Val(ops[1]), coords, layer, Val(ops[3]), offset);
                        break;
                    case Opcode.TextureSampleLevel:
                        // Depth textures take an integer level.
                        raw = Call("textureSampleLevel", tex, Val(ops[1]), coords, layer, depth ? "i32(" + Val(ops[3]) + ")" : Val(ops[3]), offset);
                        break;
                    default:
                        raw = Call("textureSampleGrad", tex, Val(ops[1]), coords, layer, Val(ops[3]), Val(ops[4]), offset);
                        break;
                }

                Define(i, FitTexel(raw, sampled, i.Type, depth));
                break;
            }

            case Opcode.TextureSampleCompare or Opcode.TextureSampleCompareLevelZero:
            {
                var (coords, layer) = SplitCoords(Val(ops[2]), ops[2].Type, rt, true);
                string fn = i.Op == Opcode.TextureSampleCompare ? "textureSampleCompare" : "textureSampleCompareLevel";
                Define(i, Call(fn, tex, Val(ops[1]), coords, layer, Val(ops[3]), OffsetArg(i, 0, rt)));
                break;
            }

            case Opcode.TextureGather:
            {
                var (coords, layer) = SplitCoords(Val(ops[2]), ops[2].Type, rt, true);
                string? component = depth ? null : Inv(i.Immediates.Count > 0 ? i.Immediates[0] : 0) + "i";
                string raw = Call("textureGather", component, tex, Val(ops[1]), coords, layer, OffsetArg(i, 1, rt));
                Define(i, FitTexel(raw, depth ? ScalarType.Float : sampled, i.Type, false));
                break;
            }

            case Opcode.TextureGatherCompare:
            {
                var (coords, layer) = SplitCoords(Val(ops[2]), ops[2].Type, rt, true);
                Define(i, Call("textureGatherCompare", tex, Val(ops[1]), coords, layer, Val(ops[3]), OffsetArg(i, 0, rt)));
                break;
            }

            case Opcode.TextureLoad:
                EmitTextureLoad(i, rt, tex, depth, sampled);
                break;
            case Opcode.TextureStore:
            {
                var (coords, layer) = SplitCoords(Val(ops[1]), ops[1].Type, rt, false);
                int n = Count(ops[2].Type);
                string texel = Val(ops[2]);
                var texelScalar = ops[2].Type.ScalarElement ?? sampled;
                if (ScalarName(texelScalar) != ScalarName(sampled))
                {
                    texel = ConvertExpr(texel, ops[2].Type, Shape(sampled, n));
                }

                if (n < 4)
                {
                    var parts = new List<string> { texel };
                    for (int c = n; c < 4; c++)
                    {
                        parts.Add(ScalarConstant(Constant.FromBits(sampled, 0)));
                    }

                    texel = "vec4<" + ScalarName(sampled) + ">(" + string.Join(", ", parts) + ")";
                }

                Put(Call("textureStore", tex, coords, layer, texel) + ";");
                break;
            }

            case Opcode.TextureQuerySize:
            {
                bool leveled = !rt.IsReadWrite && rt.ResourceKind is not (ResourceTypeKind.Texture2DMS or ResourceTypeKind.Texture2DMSArray);
                string dims = Call("textureDimensions", tex, leveled && ops.Count > 1 ? Val(ops[1]) : null);
                int n = SpatialDims(rt) == 3 && rt.ResourceKind is ResourceTypeKind.TextureCube or ResourceTypeKind.TextureCubeArray ? 2 : SpatialDims(rt);
                if (IsArrayed(rt))
                {
                    dims = "vec" + Inv(n + 1) + "<u32>(" + dims + ", textureNumLayers(" + tex + "))";
                    n++;
                }

                Define(i, ConvertExpr(dims, Shape(ScalarType.UInt, n), i.Type));
                break;
            }

            case Opcode.TextureQueryLevels:
                Define(i, ConvertExpr("textureNumLevels(" + tex + ")", ScalarType.UInt, i.Type));
                break;
            case Opcode.TextureQuerySamples:
                Define(i, ConvertExpr("textureNumSamples(" + tex + ")", ScalarType.UInt, i.Type));
                break;
            default:
                Unsupported("CalculateLevelOfDetail has no WGSL equivalent; compute the level from derivatives or pass it in.", i.Span);
                break;
        }
    }

    private void EmitTextureLoad(Instruction i, ResourceType rt, string tex, bool depth, ScalarType sampled)
    {
        var ops = i.Operands;
        var (coords, layer) = SplitCoords(Val(ops[1]), ops[1].Type, rt, false);
        if (i.Immediates.Count > 0)
        {
            // textureLoad has no offset parameter, so the offset is added to the coordinate.
            int n = Math.Min(i.Immediates.Count, SpatialDims(rt));
            var parts = new string[n];
            for (int k = 0; k < n; k++)
            {
                parts[k] = Inv(i.Immediates[k]) + "i";
            }

            string offset = n == 1 ? parts[0] : "vec" + Inv(n) + "<i32>(" + string.Join(", ", parts) + ")";
            var coordScalar = ops[1].Type.ScalarElement ?? ScalarType.Int;
            string asInt = ScalarName(coordScalar) == "i32" ? coords : TypeName(Shape(ScalarType.Int, n)) + "(" + coords + ")";
            coords = asInt + " + " + offset;
        }

        string? level = null;
        bool multisampled = rt.ResourceKind is ResourceTypeKind.Texture2DMS or ResourceTypeKind.Texture2DMSArray;
        if (!rt.IsReadWrite)
        {
            level = ops.Count > 2 ? Val(ops[2]) : multisampled ? "0i" : "0i";
        }

        string raw = Call("textureLoad", tex, coords, layer, level);
        Define(i, FitTexel(raw, sampled, i.Type, depth));
    }

    private bool IsDepth(IrValue tex) => tex is GlobalVariable g && _depthTextures.Contains(g);

    private string ByteAddressSlot(BufInfo info, IrValue offset, int component)
    {
        if (offset is Constant { ConstantKind: ConstantKind.Scalar } c)
        {
            return info.Name + "[" + Inv((uint)(c.Bits >> 2) + (uint)component) + "u]";
        }

        string word = "(" + U32(offset) + " >> 2u)";
        return info.Name + "[" + (component == 0 ? word : word + " + " + Inv(component) + "u") + "]";
    }

    private void EmitByteAddress(Instruction i)
    {
        if (i.Operands[0] is not GlobalVariable g || !_bufs.TryGetValue(g, out var info))
        {
            Fail(WgslCodes.MalformedIr, "A byte-address access does not address a byte-address buffer.", i.Span);
            return;
        }

        bool load = i.Op == Opcode.ByteAddressLoad;
        var valueType = load ? i.Type : i.Operands[2].Type;
        int n = Count(valueType);
        if (load)
        {
            var comps = new string[n];
            for (int c = 0; c < n; c++)
            {
                comps[c] = LoadWord(info, ByteAddressSlot(info, i.Operands[1], c));
            }

            string all = n == 1 ? comps[0] : "vec" + Inv(n) + "<u32>(" + string.Join(", ", comps) + ")";
            Define(i, ConvertExpr(all, Shape(ScalarType.UInt, n), i.Type));
            return;
        }

        info.Written = true;
        string stored = Simple(ConvertExpr(Val(i.Operands[2]), valueType, Shape(ScalarType.UInt, n)));
        for (int c = 0; c < n; c++)
        {
            string comp = n == 1 ? stored : stored + "." + "xyzw"[c];
            string slot = ByteAddressSlot(info, i.Operands[1], c);
            Put(info.Atomic ? "atomicStore(&" + slot + ", " + comp + ");" : slot + " = " + comp + ";");
        }
    }
}
