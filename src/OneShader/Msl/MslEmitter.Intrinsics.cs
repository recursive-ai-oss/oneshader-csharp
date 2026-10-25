using System;
using System.Collections.Generic;
using System.Text;
using OneShader.IR;
using OneShader.Types;

namespace OneShader.Msl;

internal sealed partial class MslEmitter
{
    // ---- math intrinsics ----------------------------------------------------------------------------------

    private string Arg(Instruction i, int n) => V(i.Operands[n]);

    /// <summary>Operand <paramref name="n"/>, widened with a constructor when the result is a vector and the operand a scalar (MSL's two-argument math functions need equal types).</summary>
    private string FitArg(Instruction i, int n) => Fit(V(i.Operands[n]), i.Operands[n].Type, i.Type);

    private static string Splat(string literal, HlslType like, Func<HlslType, string> typeName) =>
        Comp(like) == 1 ? literal : typeName(like) + "(" + literal + ")";

    private void EmitIntrinsic(Instruction i)
    {
        var o = i.Operands;
        var kind = o.Count > 0 ? KindOf(o[0].Type) : NumKind.Other;
        string? simple = i.Intrinsic switch
        {
            Intrinsic.Abs => "abs",
            Intrinsic.Acos => "acos",
            Intrinsic.Asin => "asin",
            Intrinsic.Atan => "atan",
            Intrinsic.Ceil => "ceil",
            Intrinsic.Cos => "cos",
            Intrinsic.Cosh => "cosh",
            Intrinsic.Cross => "cross",
            Intrinsic.Determinant => "determinant",
            Intrinsic.Distance => "distance",
            Intrinsic.Exp => "exp",
            Intrinsic.Exp2 => "exp2",
            Intrinsic.FaceForward => "faceforward",
            Intrinsic.Floor => "floor",
            Intrinsic.Fma => "fma",
            Intrinsic.Frac => "fract",
            Intrinsic.Length => "length",
            Intrinsic.Log => "log",
            Intrinsic.Log2 => "log2",
            Intrinsic.Log10 => "log10",
            Intrinsic.Normalize => "normalize",
            Intrinsic.Reflect => "reflect",
            Intrinsic.Refract => "refract",
            Intrinsic.Round => "rint", // HLSL rounds halves to even
            Intrinsic.Rsqrt => "rsqrt",
            Intrinsic.Saturate => "saturate",
            Intrinsic.Sin => "sin",
            Intrinsic.Sinh => "sinh",
            Intrinsic.Sqrt => "sqrt",
            Intrinsic.Tan => "tan",
            Intrinsic.Tanh => "tanh",
            Intrinsic.Trunc => "trunc",
            Intrinsic.IsNan => "isnan",
            Intrinsic.IsInf => "isinf",
            Intrinsic.IsFinite => "isfinite",
            Intrinsic.CountBits => "popcount",
            Intrinsic.ReverseBits => "reverse_bits",
            Intrinsic.Ddx or Intrinsic.DdxCoarse or Intrinsic.DdxFine => "dfdx",
            Intrinsic.Ddy or Intrinsic.DdyCoarse or Intrinsic.DdyFine => "dfdy",
            Intrinsic.Fwidth => "fwidth",
            _ => null,
        };
        if (simple is not null)
        {
            var args = new List<string>();
            for (int n = 0; n < o.Count; n++)
            {
                args.Add(i.Intrinsic is Intrinsic.Fma or Intrinsic.FaceForward or Intrinsic.Reflect ? Arg(i, n) : Arg(i, n));
            }

            Define(i, simple + "(" + string.Join(", ", args) + ")");
            return;
        }

        switch (i.Intrinsic)
        {
            case Intrinsic.Atan2:
                Define(i, "atan2(" + Arg(i, 0) + ", " + FitArg(i, 1) + ")");
                break;
            case Intrinsic.Pow:
                Define(i, "pow(" + Arg(i, 0) + ", " + FitArg(i, 1) + ")");
                break;
            case Intrinsic.Degrees:
                Define(i, Arg(i, 0) + " * 57.29577951308232f");
                break;
            case Intrinsic.Radians:
                Define(i, Arg(i, 0) + " * 0.017453292519943295f");
                break;
            case Intrinsic.Clamp:
                Define(i, "clamp(" + Arg(i, 0) + ", " + FitArg(i, 1) + ", " + FitArg(i, 2) + ")");
                break;
            case Intrinsic.Min:
                Define(i, "min(" + FitArg(i, 0) + ", " + FitArg(i, 1) + ")");
                break;
            case Intrinsic.Max:
                Define(i, "max(" + FitArg(i, 0) + ", " + FitArg(i, 1) + ")");
                break;
            case Intrinsic.Lerp:
                Define(i, "mix(" + FitArg(i, 0) + ", " + FitArg(i, 1) + ", " + FitArg(i, 2) + ")");
                break;
            case Intrinsic.Step:
                Define(i, "step(" + FitArg(i, 0) + ", " + FitArg(i, 1) + ")");
                break;
            case Intrinsic.SmoothStep:
                Define(i, "smoothstep(" + FitArg(i, 0) + ", " + FitArg(i, 1) + ", " + FitArg(i, 2) + ")");
                break;
            case Intrinsic.Rcp:
                Define(i, ScalarLiteral(i.Type.ScalarElement ?? ScalarType.Float, BitConverter.SingleToUInt32Bits(1f)) + " / " + Arg(i, 0));
                break;
            case Intrinsic.Sign:
                EmitSign(i, kind);
                break;
            case Intrinsic.Dot:
                EmitDot(i, kind);
                break;
            case Intrinsic.All or Intrinsic.Any:
            {
                string fn = i.Intrinsic == Intrinsic.All ? "all" : "any";
                string v = Arg(i, 0);
                int n = Comp(o[0].Type);
                if (kind != NumKind.Bool)
                {
                    v = "bool" + (n == 1 ? string.Empty : Inv(n)) + "(" + v + ")";
                }

                Define(i, n == 1 ? v : fn + "(" + v + ")");
                break;
            }

            case Intrinsic.FirstBitHigh:
                EmitFirstBit(i, kind, true);
                break;
            case Intrinsic.FirstBitLow:
                EmitFirstBit(i, kind, false);
                break;
            case Intrinsic.Ldexp:
            {
                string e = ConvertExpr(Arg(i, 1), o[1].Type, ReshapeTo(o[1].Type, i.Type));
                Define(i, Arg(i, 0) + " * exp2(" + e + ")");
                break;
            }

            case Intrinsic.Frexp:
            {
                var xt = o[0].Type;
                int n = Comp(xt);
                var intType = n == 1 ? (HlslType)ScalarType.Int : HlslType.Vector(ScalarType.Int, n);
                string e = Tmp("fe");
                W(T(intType) + " " + e + ";");
                Define(i, "frexp(" + Arg(i, 0) + ", " + e + ")");
                var target = ((PointerType)o[1].Type).Pointee;
                StoreValue(PlaceOf(o[1]), ConvertExpr(e, intType, target));
                break;
            }

            case Intrinsic.Modf:
            {
                string ip = Tmp("ip");
                W(T(o[0].Type) + " " + ip + ";");
                Define(i, "modf(" + Arg(i, 0) + ", " + ip + ")");
                StoreValue(PlaceOf(o[1]), ip);
                break;
            }

            case Intrinsic.SinCos:
            {
                string x = Atom(Arg(i, 0), o[0].Type);
                StoreValue(PlaceOf(o[1]), "sin(" + x + ")");
                StoreValue(PlaceOf(o[2]), "cos(" + x + ")");
                break;
            }

            case Intrinsic.PackHalf:
            {
                int n = Comp(o[0].Type);
                Define(i, n == 1
                    ? "uint(as_type<ushort>(half(" + Arg(i, 0) + ")))"
                    : "uint" + Inv(n) + "(as_type<ushort" + Inv(n) + ">(half" + Inv(n) + "(" + Arg(i, 0) + ")))");
                break;
            }

            case Intrinsic.UnpackHalf:
            {
                int n = Comp(i.Type);
                Define(i, n == 1
                    ? "float(as_type<half>(ushort(" + Arg(i, 0) + ")))"
                    : "float" + Inv(n) + "(as_type<half" + Inv(n) + ">(ushort" + Inv(n) + "(" + Arg(i, 0) + ")))");
                break;
            }

            default:
                Unsupported("The intrinsic '" + i.Intrinsic + "' is not supported by the MSL backend: wave and quad operations are not part of the portable subset.", i.Span);
                if (!i.IsVoid)
                {
                    Define(i, T(i.Type) + "()");
                }

                break;
        }
    }

    /// <summary>The type with <paramref name="shape"/>'s scalar type and <paramref name="like"/>'s component count.</summary>
    private static HlslType ReshapeTo(HlslType shape, HlslType like)
    {
        int n = Comp(like);
        var s = shape.ScalarElement ?? ScalarType.Float;
        return n == 1 ? s : HlslType.Vector(s, n);
    }

    private void EmitSign(Instruction i, NumKind k)
    {
        var src = i.Operands[0].Type;
        string x = Atom(Arg(i, 0), src);
        string natural;
        var naturalType = src;
        if (k == NumKind.Float)
        {
            natural = "sign(" + x + ")";
        }
        else if (k == NumKind.SInt)
        {
            string zero = Splat("0", src, T);
            if (Comp(src) == 1)
            {
                natural = "(" + x + " > 0 ? 1 : (" + x + " < 0 ? -1 : 0))";
            }
            else
            {
                natural = "select(select(" + zero + ", " + Splat("-1", src, T) + ", " + x + " < " + zero + "), " + Splat("1", src, T) + ", " + x + " > " + zero + ")";
            }
        }
        else
        {
            natural = Comp(src) == 1 ? "(" + x + " != 0u ? 1u : 0u)" : T(src) + "(" + x + " != " + T(src) + "(0u))";
        }

        Define(i, ConvertExpr(natural, naturalType, i.Type));
    }

    private void EmitDot(Instruction i, NumKind k)
    {
        var a = i.Operands[0].Type;
        if (Comp(a) == 1)
        {
            Define(i, Arg(i, 0) + " * " + Arg(i, 1));
        }
        else if (k == NumKind.Float)
        {
            Define(i, "dot(" + Arg(i, 0) + ", " + Arg(i, 1) + ")");
        }
        else
        {
            // MSL's dot is float-only: sum the component products.
            string p = Atom(Arg(i, 0) + " * " + Arg(i, 1), a);
            var terms = new List<string>();
            for (int c = 0; c < Comp(a); c++)
            {
                terms.Add(p + "." + Swz(c));
            }

            Define(i, string.Join(" + ", terms));
        }
    }

    private void EmitFirstBit(Instruction i, NumKind k, bool high)
    {
        var src = i.Operands[0].Type;
        string x = Atom(Arg(i, 0), src);
        string expr;
        if (high)
        {
            // Index of the highest set bit (of the highest clear bit for negative numbers); clz(0) is 32, so 31 - 32 wraps to -1 as HLSL wants.
            string basis = k == NumKind.SInt ? (Comp(src) == 1 ? "(" + x + " < 0 ? ~" + x + " : " + x + ")" : "select(" + x + ", ~" + x + ", " + x + " < " + T(src) + "(0))") : x;
            expr = Splat(k == NumKind.SInt ? "31" : "31u", src, T) + " - clz(" + basis + ")";
        }
        else
        {
            string none = Splat(k == NumKind.SInt ? "-1" : "0xFFFFFFFFu", src, T);
            expr = Comp(src) == 1
                ? "(" + x + " == 0 ? " + none + " : ctz(" + x + "))"
                : "select(ctz(" + x + "), " + none + ", " + x + " == " + T(src) + "(0))";
        }

        Define(i, ConvertExpr(expr, src, i.Type));
    }

    // ---- textures -----------------------------------------------------------------------------------------

    private readonly record struct TexShape(int Dims, bool Arrayed, bool Cube, bool Multisampled, bool Buffer);

    private static TexShape ShapeOf(ResourceType rt) => rt.ResourceKind switch
    {
        ResourceTypeKind.Texture1D or ResourceTypeKind.RWTexture1D => new TexShape(1, false, false, false, false),
        ResourceTypeKind.Texture1DArray or ResourceTypeKind.RWTexture1DArray => new TexShape(1, true, false, false, false),
        ResourceTypeKind.Texture2D or ResourceTypeKind.RWTexture2D => new TexShape(2, false, false, false, false),
        ResourceTypeKind.Texture2DArray or ResourceTypeKind.RWTexture2DArray => new TexShape(2, true, false, false, false),
        ResourceTypeKind.Texture2DMS => new TexShape(2, false, false, true, false),
        ResourceTypeKind.Texture2DMSArray => new TexShape(2, true, false, true, false),
        ResourceTypeKind.Texture3D or ResourceTypeKind.RWTexture3D => new TexShape(3, false, false, false, false),
        ResourceTypeKind.TextureCube => new TexShape(3, false, true, false, false),
        ResourceTypeKind.TextureCubeArray => new TexShape(3, true, true, false, false),
        _ => new TexShape(1, false, false, false, true),
    };

    private static string Cmp(string e, int n, int count) => count == 1 ? e : e + "." + Swz(n);

    private static string Head(string e, int n, int count)
    {
        if (n == count)
        {
            return e;
        }

        if (n == 1)
        {
            return Cmp(e, 0, count);
        }

        var sb = new StringBuilder(e).Append('.');
        for (int c = 0; c < n; c++)
        {
            sb.Append(Swz(c));
        }

        return sb.ToString();
    }

    /// <summary>Splits a coordinate into the texture-space part and the array layer; the layer rounds to nearest for float coordinates, as HLSL specifies.</summary>
    private string SplitCoordinate(string e, HlslType type, TexShape s, bool floatCoord, out string layer)
    {
        int count = Comp(type);
        e = count > 1 ? Atom(e, type) : e;
        layer = "";
        if (!s.Arrayed)
        {
            string c = Head(e, s.Dims, count);
            return floatCoord ? c : (s.Dims == 1 ? "uint(" + c + ")" : "uint" + Inv(s.Dims) + "(" + c + ")");
        }

        string raw = Cmp(e, s.Dims, count);
        layer = floatCoord ? "uint(max(rint(" + raw + "), 0.0f))" : "uint(" + raw + ")";
        string head = Head(e, s.Dims, count);
        return floatCoord ? head : (s.Dims == 1 ? "uint(" + head + ")" : "uint" + Inv(s.Dims) + "(" + head + ")");
    }

    private string? OffsetArg(Instruction i, int first, TexShape s)
    {
        int n = i.Immediates.Count - first;
        bool any = false;
        for (int k = 0; k < n; k++)
        {
            any |= i.Immediates[first + k] != 0;
        }

        if (!any)
        {
            return null;
        }

        if (s.Dims == 1 || s.Cube)
        {
            Unsupported("MSL cannot apply a texel offset to a 1D or cube texture.", i.Span);
            return null;
        }

        var parts = new List<string>();
        for (int k = 0; k < s.Dims; k++)
        {
            parts.Add(Inv(k < n ? i.Immediates[first + k] : 0));
        }

        return "int" + Inv(s.Dims) + "(" + string.Join(", ", parts) + ")";
    }

    private static ResourceType? ResourceOf(IrValue pointer) => pointer.Type is PointerType { Pointee: ResourceType rt } ? rt : null;

    private string HandleExpr(IrValue pointer)
    {
        var p = PlaceOf(pointer);
        if (p.Kind != PlaceKind.Handle)
        {
            Malformed("A texture operation does not address a texture resource.");
            return "0";
        }

        return p.Expr;
    }

    private bool IsDepth(IrValue pointer) => RootGlobal(pointer) is { } g && _res.TryGetValue(g, out var r) && r.Depth;

    /// <summary>Cuts or pads a texel result to the type the shader asked for.</summary>
    private string FitTexel(string call, ResourceType rt, bool scalarResult, Instruction i)
    {
        var sampled = SampledScalarOf(rt);
        int n = Comp(i.Type);
        string v4 = scalarResult ? ScalarName(sampled) + "4(" + call + ", 0, 0, 1)" : call;
        string cut = scalarResult && n == 1 ? call : n == 4 ? v4 : Head(Atom(v4, HlslType.Vector(sampled, 4)), n, 4);
        var have = n == 1 ? (HlslType)sampled : HlslType.Vector(sampled, n);
        return ConvertExpr(cut, have, i.Type);
    }

    private void EmitImageOrBuffer(Instruction i)
    {
        if (i.Op is Opcode.ByteAddressLoad or Opcode.ByteAddressStore)
        {
            EmitByteAddress(i);
            return;
        }

        var o = i.Operands;
        var rt = ResourceOf(o[0]);
        if (rt is null)
        {
            Malformed("A texture operation does not address a texture resource.", i.Span);
            return;
        }

        var s = ShapeOf(rt);
        string h = HandleExpr(o[0]);
        bool depth = IsDepth(o[0]);
        switch (i.Op)
        {
            case Opcode.TextureSample or Opcode.TextureSampleBias or Opcode.TextureSampleLevel or Opcode.TextureSampleGrad:
            {
                var args = new List<string> { HandleExpr(o[1]), SplitCoordinate(V(o[2]), o[2].Type, s, true, out string layer) };
                if (layer.Length > 0)
                {
                    args.Add(layer);
                }

                switch (i.Op)
                {
                    case Opcode.TextureSampleBias:
                        args.Add("bias(" + V(o[3]) + ")");
                        break;
                    case Opcode.TextureSampleLevel:
                        args.Add("level(" + V(o[3]) + ")");
                        break;
                    case Opcode.TextureSampleGrad:
                        if (s.Dims == 1)
                        {
                            Unsupported("MSL has no gradient sampling for 1D textures.", i.Span);
                        }

                        args.Add((s.Cube ? "gradientcube(" : s.Dims == 3 ? "gradient3d(" : "gradient2d(") + V(o[3]) + ", " + V(o[4]) + ")");
                        break;
                    default:
                        break;
                }

                if (OffsetArg(i, 0, s) is { } off)
                {
                    args.Add(off);
                }

                Define(i, FitTexel(h + ".sample(" + string.Join(", ", args) + ")", rt, depth, i));
                break;
            }

            case Opcode.TextureSampleCompare or Opcode.TextureSampleCompareLevelZero:
            {
                var args = new List<string> { HandleExpr(o[1]), SplitCoordinate(V(o[2]), o[2].Type, s, true, out string layer) };
                if (layer.Length > 0)
                {
                    args.Add(layer);
                }

                args.Add(V(o[3]));
                if (i.Op == Opcode.TextureSampleCompareLevelZero)
                {
                    args.Add("level(0)");
                }

                if (OffsetArg(i, 0, s) is { } off)
                {
                    args.Add(off);
                }

                Define(i, h + ".sample_compare(" + string.Join(", ", args) + ")");
                break;
            }

            case Opcode.TextureGather:
            {
                var args = new List<string> { HandleExpr(o[1]), SplitCoordinate(V(o[2]), o[2].Type, s, true, out string layer) };
                if (layer.Length > 0)
                {
                    args.Add(layer);
                }

                if (!s.Cube)
                {
                    args.Add(OffsetArg(i, 1, s) ?? "int2(0)");
                }

                if (!depth)
                {
                    args.Add("component::" + Swz(i.Immediates.Count > 0 ? i.Immediates[0] : 0));
                }

                Define(i, FitTexel(h + ".gather(" + string.Join(", ", args) + ")", rt, false, i));
                break;
            }

            case Opcode.TextureGatherCompare:
            {
                var args = new List<string> { HandleExpr(o[1]), SplitCoordinate(V(o[2]), o[2].Type, s, true, out string layer) };
                if (layer.Length > 0)
                {
                    args.Add(layer);
                }

                args.Add(V(o[3]));
                if (OffsetArg(i, 0, s) is { } off)
                {
                    args.Add(off);
                }

                Define(i, h + ".gather_compare(" + string.Join(", ", args) + ")");
                break;
            }

            case Opcode.TextureLoad:
            {
                var args = new List<string>();
                if (s.Buffer)
                {
                    args.Add("uint(" + V(o[1]) + ")");
                }
                else
                {
                    args.Add(SplitCoordinate(V(o[1]), o[1].Type, s, false, out string layer));
                    if (layer.Length > 0)
                    {
                        args.Add(layer);
                    }

                    if (o.Count > 2 && !rt.IsReadWrite)
                    {
                        args.Add("uint(" + V(o[2]) + ")");
                    }
                }

                Define(i, FitTexel(h + ".read(" + string.Join(", ", args) + ")", rt, depth, i));
                break;
            }

            case Opcode.TextureStore:
            {
                var sampled = SampledScalarOf(rt);
                var vt = o[2].Type;
                int n = Comp(vt);
                string v = ConvertExpr(V(o[2]), vt, n == 1 ? sampled : HlslType.Vector(sampled, n));
                string sn = ScalarName(sampled);
                string value = n switch
                {
                    1 => sn + "4(" + v + ", 0, 0, 0)",
                    2 => sn + "4(" + v + ", 0, 0)",
                    3 => sn + "4(" + v + ", 0)",
                    _ => v,
                };
                var args = new List<string> { value };
                string layer = string.Empty;
                args.Add(s.Buffer ? "uint(" + V(o[1]) + ")" : SplitCoordinate(V(o[1]), o[1].Type, s, false, out layer));
                if (!s.Buffer && layer.Length > 0)
                {
                    args.Add(layer);
                }

                W(h + ".write(" + string.Join(", ", args) + ");");
                break;
            }

            case Opcode.TextureQuerySize:
            {
                string lod = o.Count > 1 && !rt.IsReadWrite && !s.Multisampled && !s.Buffer ? "uint(" + V(o[1]) + ")" : string.Empty;
                var parts = new List<string> { h + ".get_width(" + lod + ")" };
                if (s.Dims >= 2)
                {
                    parts.Add(h + ".get_height(" + lod + ")");
                }

                if (s.Dims == 3 && !s.Cube)
                {
                    parts.Add(h + ".get_depth(" + lod + ")");
                }

                if (s.Arrayed)
                {
                    parts.Add(h + ".get_array_size()");
                }

                var qt = parts.Count == 1 ? (HlslType)ScalarType.UInt : HlslType.Vector(ScalarType.UInt, parts.Count);
                string size = parts.Count == 1 ? parts[0] : "uint" + Inv(parts.Count) + "(" + string.Join(", ", parts) + ")";
                Define(i, ConvertExpr(size, qt, i.Type));
                break;
            }

            case Opcode.TextureQueryLevels:
                Define(i, ConvertExpr(h + ".get_num_mip_levels()", ScalarType.UInt, i.Type));
                break;
            case Opcode.TextureQuerySamples:
                Define(i, ConvertExpr(h + ".get_num_samples()", ScalarType.UInt, i.Type));
                break;
            default: // TextureQueryLod
            {
                string sampler = HandleExpr(o[1]);
                string coord = SplitCoordinate(V(o[2]), o[2].Type, s, true, out _);
                Define(i, "float2(" + h + ".calculate_clamped_lod(" + sampler + ", " + coord + "), " + h + ".calculate_unclamped_lod(" + sampler + ", " + coord + "))");
                break;
            }
        }
    }

    private void EmitByteAddress(Instruction i)
    {
        var p = PlaceOf(i.Operands[0]);
        if (p.Kind != PlaceKind.BufferRoot)
        {
            Malformed("A byte-address operation does not address a byte-address buffer.", i.Span);
            return;
        }

        string word = Tmp("w");
        W("uint " + word + " = " + V(i.Operands[1]) + " >> 2;");
        bool load = i.Op == Opcode.ByteAddressLoad;
        var valueType = load ? i.Type : i.Operands[2].Type;
        int n = Comp(valueType);
        var uintType = n == 1 ? (HlslType)ScalarType.UInt : HlslType.Vector(ScalarType.UInt, n);
        if (load)
        {
            var parts = new List<string>();
            for (int c = 0; c < n; c++)
            {
                parts.Add(p.Expr + "[" + (c == 0 ? word : word + " + " + Inv(c) + "u") + "]");
            }

            string all = n == 1 ? parts[0] : "uint" + Inv(n) + "(" + string.Join(", ", parts) + ")";
            Define(i, ConvertExpr(all, uintType, i.Type));
            return;
        }

        string stored = Atom(ConvertExpr(V(i.Operands[2]), valueType, uintType), uintType);
        for (int c = 0; c < n; c++)
        {
            W(p.Expr + "[" + (c == 0 ? word : word + " + " + Inv(c) + "u") + "] = " + Cmp(stored, c, n) + ";");
        }
    }
}
