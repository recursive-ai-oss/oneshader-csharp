using System.Collections.Generic;
using OneShader.IR;
using OneShader.Types;

namespace OneShader.SpirV;

internal sealed partial class SpirvEmitter
{
    private uint Arg(Instruction i, int n) => Id(i.Operands[n]);

    /// <summary>Operand <paramref name="n"/>, widened with a splat when the result is a vector and the operand a scalar.</summary>
    private uint Fit(Instruction i, int n)
    {
        var op = i.Operands[n];
        int rc = Count(i.Type);
        if (Count(op.Type) == 1 && rc > 1 && op.Type.ScalarElement is { } s)
        {
            return ConvertValue(Id(op), op.Type, Shape(s, rc));
        }

        return Id(op);
    }

    private void StoreThrough(IrValue pointer, uint value, HlslType valueType)
    {
        uint p = Ptr(pointer, out uint pointee);
        if (TypeId(valueType) != pointee)
        {
            value = PlainToLayout(value, valueType, pointee);
        }

        SpirvModule.Put(_s.Code, Op.Store, p, value);
    }

    private static HlslType PointeeOfPointer(IrValue p) => p.Type is PointerType pt ? pt.Pointee : p.Type;

    private void EmitIntrinsic(Instruction i)
    {
        NK k = i.Operands.Count > 0 ? KindOf(i.Operands[0].Type) : NK.Other;
        uint rt = TypeId(i.Type);
        int rc = Count(i.Type);
        var scalar = i.Type.ScalarElement ?? ScalarType.Float;
        switch (i.Intrinsic)
        {
            case Intrinsic.Abs:
                ExtDirect(i, k == NK.Float ? Glsl.FAbs : Glsl.SAbs, Arg(i, 0));
                break;
            case Intrinsic.Acos: ExtDirect(i, Glsl.Acos, Arg(i, 0)); break;
            case Intrinsic.Asin: ExtDirect(i, Glsl.Asin, Arg(i, 0)); break;
            case Intrinsic.Atan: ExtDirect(i, Glsl.Atan, Arg(i, 0)); break;
            case Intrinsic.Atan2: ExtDirect(i, Glsl.Atan2, Arg(i, 0), Fit(i, 1)); break;
            case Intrinsic.Ceil: ExtDirect(i, Glsl.Ceil, Arg(i, 0)); break;
            case Intrinsic.Cos: ExtDirect(i, Glsl.Cos, Arg(i, 0)); break;
            case Intrinsic.Cosh: ExtDirect(i, Glsl.Cosh, Arg(i, 0)); break;
            case Intrinsic.Cross: ExtDirect(i, Glsl.Cross, Arg(i, 0), Arg(i, 1)); break;
            case Intrinsic.Degrees: ExtDirect(i, Glsl.Degrees, Arg(i, 0)); break;
            case Intrinsic.Determinant: ExtDirect(i, Glsl.Determinant, Arg(i, 0)); break;
            case Intrinsic.Distance: ExtDirect(i, Glsl.Distance, Arg(i, 0), Arg(i, 1)); break;
            case Intrinsic.Exp: ExtDirect(i, Glsl.Exp, Arg(i, 0)); break;
            case Intrinsic.Exp2: ExtDirect(i, Glsl.Exp2, Arg(i, 0)); break;
            case Intrinsic.FaceForward: ExtDirect(i, Glsl.FaceForward, Arg(i, 0), Arg(i, 1), Arg(i, 2)); break;
            case Intrinsic.Floor: ExtDirect(i, Glsl.Floor, Arg(i, 0)); break;
            case Intrinsic.Fma: ExtDirect(i, Glsl.Fma, Arg(i, 0), Arg(i, 1), Arg(i, 2)); break;
            case Intrinsic.Frac: ExtDirect(i, Glsl.Fract, Arg(i, 0)); break;
            case Intrinsic.Length: ExtDirect(i, Glsl.Length, Arg(i, 0)); break;
            case Intrinsic.Log: ExtDirect(i, Glsl.Log, Arg(i, 0)); break;
            case Intrinsic.Log2: ExtDirect(i, Glsl.Log2, Arg(i, 0)); break;
            case Intrinsic.Normalize: ExtDirect(i, Glsl.Normalize, Arg(i, 0)); break;
            case Intrinsic.Pow: ExtDirect(i, Glsl.Pow, Arg(i, 0), Fit(i, 1)); break;
            case Intrinsic.Radians: ExtDirect(i, Glsl.Radians, Arg(i, 0)); break;
            case Intrinsic.Reflect: ExtDirect(i, Glsl.Reflect, Arg(i, 0), Arg(i, 1)); break;
            case Intrinsic.Refract: ExtDirect(i, Glsl.Refract, Arg(i, 0), Arg(i, 1), Arg(i, 2)); break;
            case Intrinsic.Round: ExtDirect(i, Glsl.RoundEven, Arg(i, 0)); break; // HLSL rounds half to even.
            case Intrinsic.Rsqrt: ExtDirect(i, Glsl.InverseSqrt, Arg(i, 0)); break;
            case Intrinsic.Sin: ExtDirect(i, Glsl.Sin, Arg(i, 0)); break;
            case Intrinsic.Sinh: ExtDirect(i, Glsl.Sinh, Arg(i, 0)); break;
            case Intrinsic.SmoothStep: ExtDirect(i, Glsl.SmoothStep, Fit(i, 0), Fit(i, 1), Fit(i, 2)); break;
            case Intrinsic.Sqrt: ExtDirect(i, Glsl.Sqrt, Arg(i, 0)); break;
            case Intrinsic.Step: ExtDirect(i, Glsl.Step, Fit(i, 0), Fit(i, 1)); break;
            case Intrinsic.Tan: ExtDirect(i, Glsl.Tan, Arg(i, 0)); break;
            case Intrinsic.Tanh: ExtDirect(i, Glsl.Tanh, Arg(i, 0)); break;
            case Intrinsic.Trunc: ExtDirect(i, Glsl.Trunc, Arg(i, 0)); break;
            case Intrinsic.Lerp: ExtDirect(i, Glsl.FMix, Fit(i, 0), Fit(i, 1), Fit(i, 2)); break;
            case Intrinsic.Min:
                ExtDirect(i, k == NK.Float ? Glsl.FMin : k == NK.SInt ? Glsl.SMin : Glsl.UMin, Fit(i, 0), Fit(i, 1));
                break;
            case Intrinsic.Max:
                ExtDirect(i, k == NK.Float ? Glsl.FMax : k == NK.SInt ? Glsl.SMax : Glsl.UMax, Fit(i, 0), Fit(i, 1));
                break;
            case Intrinsic.Clamp:
                ExtDirect(i, k == NK.Float ? Glsl.FClamp : k == NK.SInt ? Glsl.SClamp : Glsl.UClamp, Fit(i, 0), Fit(i, 1), Fit(i, 2));
                break;
            case Intrinsic.Saturate:
                ExtDirect(i, Glsl.FClamp, Arg(i, 0), SplatConst(scalar, 0, rc), SplatConst(scalar, 1, rc));
                break;
            case Intrinsic.Rcp:
                Direct(i, Op.FDiv, rt, SplatConst(scalar, 1, rc), Arg(i, 0));
                break;
            case Intrinsic.Sign:
                EmitSign(i, k);
                break;
            case Intrinsic.Dot:
                EmitDot(i, k);
                break;
            case Intrinsic.All or Intrinsic.Any:
            {
                var src = i.Operands[0].Type;
                uint v = Arg(i, 0);
                if (k != NK.Bool)
                {
                    v = ConvertValue(v, src, Shape(ScalarType.Bool, Count(src)));
                }

                if (Count(src) == 1)
                {
                    Finish(i, v);
                }
                else
                {
                    Direct(i, i.Intrinsic == Intrinsic.All ? Op.All : Op.Any, rt, v);
                }

                break;
            }

            case Intrinsic.NonUniformIndex:
                Direct(i, CopyObject, rt, Arg(i, 0));
                MarkNonUniform(i);
                break;
            case Intrinsic.CountBits: Direct(i, Op.BitCount, rt, Arg(i, 0)); break;
            case Intrinsic.ReverseBits: Direct(i, Op.BitReverse, rt, Arg(i, 0)); break;
            case Intrinsic.FirstBitHigh: ExtDirect(i, k == NK.SInt ? Glsl.FindSMsb : Glsl.FindUMsb, Arg(i, 0)); break;
            case Intrinsic.FirstBitLow: ExtDirect(i, Glsl.FindILsb, Arg(i, 0)); break;
            case Intrinsic.IsNan: Direct(i, Op.IsNan, rt, Arg(i, 0)); break;
            case Intrinsic.IsInf: Direct(i, Op.IsInf, rt, Arg(i, 0)); break;
            case Intrinsic.IsFinite:
            {
                // OpIsFinite needs the Kernel capability, so build it from the two shader-legal tests.
                uint nan = Emit(Op.IsNan, rt, Arg(i, 0));
                uint inf = Emit(Op.IsInf, rt, Arg(i, 0));
                uint either = Emit(Op.LogicalOr, rt, nan, inf);
                Direct(i, Op.LogicalNot, rt, either);
                break;
            }

            case Intrinsic.Ldexp:
            {
                // ldexp(x, e) = x * 2^e, with e a float in HLSL.
                uint e = ConvertValue(Arg(i, 1), i.Operands[1].Type, i.Operands[0].Type.ScalarElement is { } es && Count(i.Operands[1].Type) == rc ? Shape(es, rc) : i.Operands[1].Type);
                uint pow = Ext(rt, Glsl.Exp2, e);
                Direct(i, Op.FMul, rt, Arg(i, 0), pow);
                break;
            }

            case Intrinsic.Frexp:
                EmitFrexpModf(i, true);
                break;
            case Intrinsic.Modf:
                EmitFrexpModf(i, false);
                break;
            case Intrinsic.SinCos:
            {
                var xt = i.Operands[0].Type;
                uint xt32 = TypeId(xt);
                StoreThrough(i.Operands[1], Ext(xt32, Glsl.Sin, Arg(i, 0)), xt);
                StoreThrough(i.Operands[2], Ext(xt32, Glsl.Cos, Arg(i, 0)), xt);
                break;
            }

            case Intrinsic.PackHalf:
                EmitHalf(i, true);
                break;
            case Intrinsic.UnpackHalf:
                EmitHalf(i, false);
                break;
            case Intrinsic.Ddx: Direct(i, Op.DPdx, rt, Arg(i, 0)); break;
            case Intrinsic.Ddy: Direct(i, Op.DPdy, rt, Arg(i, 0)); break;
            case Intrinsic.Fwidth: Direct(i, Op.Fwidth, rt, Arg(i, 0)); break;
            case Intrinsic.DdxCoarse:
                _s.Capabilities.Add(Cap.DerivativeControl);
                Direct(i, Op.DPdxCoarse, rt, Arg(i, 0));
                break;
            case Intrinsic.DdyCoarse:
                _s.Capabilities.Add(Cap.DerivativeControl);
                Direct(i, Op.DPdyCoarse, rt, Arg(i, 0));
                break;
            case Intrinsic.DdxFine:
                _s.Capabilities.Add(Cap.DerivativeControl);
                Direct(i, Op.DPdxFine, rt, Arg(i, 0));
                break;
            case Intrinsic.DdyFine:
                _s.Capabilities.Add(Cap.DerivativeControl);
                Direct(i, Op.DPdyFine, rt, Arg(i, 0));
                break;
            default:
                Unsupported("The intrinsic '" + i.Intrinsic + "' is not supported by the SPIR-V backend yet.", i.Span);
                if (!i.IsVoid)
                {
                    Finish(i, Zero(i.Type));
                }

                break;
        }
    }

    private void EmitSign(Instruction i, NK k)
    {
        // HLSL sign() returns int; GLSL.std.450 returns the operand's type.
        var src = i.Operands[0].Type;
        uint v = Arg(i, 0);
        int n = Count(src);
        var s = src.ScalarElement!;
        uint natural;
        HlslType naturalType = src;
        if (k == NK.Float)
        {
            natural = Ext(TypeId(src), Glsl.FSign, v);
        }
        else if (k == NK.SInt)
        {
            natural = Ext(TypeId(src), Glsl.SSign, v);
        }
        else
        {
            uint nonZero = Emit(Op.INotEqual, TypeId(Shape(ScalarType.Bool, n)), v, SplatConst(s, 0, n));
            natural = CastScalars(nonZero, ScalarType.Bool, s, n);
        }

        Finish(i, ConvertValue(natural, naturalType, i.Type));
    }

    private void EmitDot(Instruction i, NK k)
    {
        var a = i.Operands[0].Type;
        uint rt = TypeId(i.Type);
        if (Count(a) == 1)
        {
            Direct(i, k == NK.Float ? Op.FMul : Op.IMul, rt, Arg(i, 0), Arg(i, 1));
            return;
        }

        if (k == NK.Float)
        {
            Direct(i, Op.Dot, rt, Arg(i, 0), Arg(i, 1));
            return;
        }

        // OpDot is float-only; integer dot products are a sum of component products.
        int n = Count(a);
        uint prod = Emit(Op.IMul, TypeId(a), Arg(i, 0), Arg(i, 1));
        uint sum = Emit(Op.CompositeExtract, rt, prod, 0);
        for (int c = 1; c < n; c++)
        {
            uint x = Emit(Op.CompositeExtract, rt, prod, (uint)c);
            sum = Emit(Op.IAdd, rt, sum, x);
        }

        Finish(i, sum);
    }

    private void EmitFrexpModf(Instruction i, bool frexp)
    {
        var xt = i.Operands[0].Type;
        int n = Count(xt);
        var s = xt.ScalarElement ?? ScalarType.Float;
        uint xT = TypeId(xt);
        var second = frexp ? Shape(ScalarType.Int, n) : xt;
        uint secondT = TypeId(second);
        uint st = _s.DeclType("st:" + (frexp ? "frexp:" : "modf:") + Inv(xT) + ":" + Inv(secondT), Op.TypeStruct, xT, secondT);
        uint res = Ext(st, frexp ? Glsl.FrexpStruct : Glsl.ModfStruct, Arg(i, 0));
        uint first = Emit(Op.CompositeExtract, xT, res, 0);
        uint other = Emit(Op.CompositeExtract, secondT, res, 1);
        var target = PointeeOfPointer(i.Operands[1]);
        // modf returns (fractional, whole) while HLSL returns the fraction and writes the whole part.
        uint stored = frexp ? ConvertValue(other, second, target) : other;
        StoreThrough(i.Operands[1], stored, frexp ? target : xt);
        _ = s;
        Finish(i, first);
    }

    private void EmitHalf(Instruction i, bool pack)
    {
        int n = Count(i.Type);
        uint f32 = TypeId(ScalarType.Float);
        uint u32 = TypeId(ScalarType.UInt);
        uint v2 = TypeId(HlslType.Vector(ScalarType.Float, 2));
        var parts = new uint[n];
        var src = i.Operands[0].Type;
        for (int c = 0; c < n; c++)
        {
            uint comp = n == 1 ? Arg(i, 0) : Emit(Op.CompositeExtract, TypeId(src.ScalarElement!), Arg(i, 0), (uint)c);
            if (pack)
            {
                uint vec = Emit(Op.CompositeConstruct, v2, comp, Zero(ScalarType.Float));
                parts[c] = Ext(u32, Glsl.PackHalf2x16, vec);
            }
            else
            {
                uint bits = CastScalars(comp, src.ScalarElement!, ScalarType.UInt, 1);
                uint vec = Ext(v2, Glsl.UnpackHalf2x16, bits);
                parts[c] = Emit(Op.CompositeExtract, f32, vec, 0);
            }
        }

        if (n == 1)
        {
            Finish(i, parts[0]);
        }
        else
        {
            Direct(i, Op.CompositeConstruct, TypeId(i.Type), parts);
        }
    }

    // ---- textures and buffers -------------------------------------------------------------------

    private static ResourceType? ResourceOf(IrValue pointer) =>
        pointer.Type is PointerType { Pointee: ResourceType rt } ? rt : null;

    private static ScalarType SampledScalar(ResourceType rt)
    {
        var e = rt.Element?.ScalarElement ?? ScalarType.Float;
        return e.IsFloatingPoint || e.ScalarKind == ScalarKind.Bool ? ScalarType.Float : e.IsSignedInteger ? ScalarType.Int : ScalarType.UInt;
    }

    private uint LoadImage(IrValue texture, out ResourceType? rt, out uint imageType)
    {
        rt = ResourceOf(texture);
        if (rt is null)
        {
            Fail(SpirvCodes.MalformedIr, "A texture operation does not address a texture resource.");
            imageType = TypeId(ScalarType.UInt);
            return Zero(ScalarType.UInt);
        }

        // The variable's own type carries the storage format when [[vk::image_format]] gave one.
        uint p = Ptr(texture, out uint pointee);
        imageType = _formattedImages.Contains(pointee) ? pointee : ImageTypeId(rt);
        return MarkLoaded(texture, Emit(Op.Load, imageType, p));
    }

    private uint SampledImageOf(IrValue texture, IrValue sampler, out ResourceType? rt)
    {
        uint img = LoadImage(texture, out rt, out uint imageType);
        uint sp = Ptr(sampler, out _);
        uint smp = MarkLoaded(sampler, Emit(Op.Load, TypeId(SamplerType.State), sp));
        uint combined = Emit(Op.SampledImage, SampledImageTypeId(imageType), img, smp);
        return _nonUniform.Contains(texture) ? MarkLoaded(texture, combined) : MarkLoaded(sampler, combined);
    }

    private uint OffsetConst(List<int> values, int start)
    {
        int n = values.Count - start;
        if (n <= 0)
        {
            return 0;
        }

        if (n == 1)
        {
            return ConstId(Constant.Int(values[start]));
        }

        var elems = new Constant[n];
        for (int k = 0; k < n; k++)
        {
            elems[k] = Constant.Int(values[start + k]);
        }

        return ConstId(Constant.Composite(HlslType.Vector(ScalarType.Int, n), elems));
    }

    /// <summary>Cuts a four-component texel result down to the type the shader asked for.</summary>
    private uint FitTexel(uint v4, ScalarType sampled, HlslType want)
    {
        int n = Count(want);
        uint wantSampled = v4;
        if (n < 4)
        {
            if (n == 1)
            {
                wantSampled = Emit(Op.CompositeExtract, TypeId(sampled), v4, 0);
            }
            else
            {
                var ops = new uint[n + 2];
                ops[0] = v4;
                ops[1] = v4;
                for (int k = 0; k < n; k++)
                {
                    ops[k + 2] = (uint)k;
                }

                wantSampled = Emit(Op.VectorShuffle, TypeId(Shape(sampled, n)), ops);
            }
        }

        var ws = want.ScalarElement ?? sampled;
        return ws == sampled ? wantSampled : CastScalars(wantSampled, sampled, ws, n);
    }

    private void EmitImageOrBuffer(Instruction i)
    {
        if (i.Op is Opcode.ByteAddressLoad or Opcode.ByteAddressStore)
        {
            EmitByteAddress(i);
            return;
        }

        var ops = i.Operands;
        switch (i.Op)
        {
            case Opcode.TextureSample or Opcode.TextureSampleBias or Opcode.TextureSampleLevel or Opcode.TextureSampleGrad:
            {
                uint si = SampledImageOf(ops[0], ops[1], out var rt);
                if (rt is null)
                {
                    Finish(i, Zero(i.Type));
                    return;
                }

                var sampled = SampledScalar(rt);
                uint mask = 0;
                var ids = new List<uint>();
                uint opcode = Op.ImageSampleImplicitLod;
                if (i.Op == Opcode.TextureSampleBias)
                {
                    mask |= 0x1;
                    ids.Add(Id(ops[3]));
                }
                else if (i.Op == Opcode.TextureSampleLevel)
                {
                    mask |= 0x2;
                    ids.Add(Id(ops[3]));
                    opcode = Op.ImageSampleExplicitLod;
                }
                else if (i.Op == Opcode.TextureSampleGrad)
                {
                    mask |= 0x4;
                    ids.Add(Id(ops[3]));
                    ids.Add(Id(ops[4]));
                    opcode = Op.ImageSampleExplicitLod;
                }

                AddOffset(i, 0, ref mask, ids);
                uint v4 = Emit(opcode, TypeId(Shape(sampled, 4)), Operands(si, Id(ops[2]), mask, ids));
                Finish(i, FitTexel(v4, sampled, i.Type));
                break;
            }

            case Opcode.TextureSampleCompare or Opcode.TextureSampleCompareLevelZero:
            {
                uint si = SampledImageOf(ops[0], ops[1], out _);
                uint mask = 0;
                var ids = new List<uint>();
                uint opcode = Op.ImageSampleDrefImplicitLod;
                if (i.Op == Opcode.TextureSampleCompareLevelZero)
                {
                    mask |= 0x2;
                    ids.Add(Zero(ScalarType.Float));
                    opcode = Op.ImageSampleDrefExplicitLod;
                }

                AddOffset(i, 0, ref mask, ids);
                var all = new List<uint> { si, Id(ops[2]), Id(ops[3]) };
                if (mask != 0)
                {
                    all.Add(mask);
                    all.AddRange(ids);
                }

                Direct(i, opcode, TypeId(i.Type), all.ToArray());
                break;
            }

            case Opcode.TextureGather:
            {
                uint si = SampledImageOf(ops[0], ops[1], out var rt);
                var sampled = rt is null ? ScalarType.Float : SampledScalar(rt);
                uint mask = 0;
                var ids = new List<uint>();
                AddOffset(i, 1, ref mask, ids);
                var all = new List<uint> { si, Id(ops[2]), ConstId(Constant.Int(i.Immediates.Count > 0 ? i.Immediates[0] : 0)) };
                if (mask != 0)
                {
                    all.Add(mask);
                    all.AddRange(ids);
                }

                uint v4 = Emit(Op.ImageGather, TypeId(Shape(sampled, 4)), all.ToArray());
                Finish(i, FitTexel(v4, sampled, i.Type));
                break;
            }

            case Opcode.TextureLoad:
                EmitTextureLoad(i);
                break;
            case Opcode.TextureStore:
            {
                uint img = LoadImage(ops[0], out var rt, out uint storeImageType);
                if (!_formattedImages.Contains(storeImageType))
                {
                    _s.Capabilities.Add(Cap.StorageImageWriteWithoutFormat);
                }

                var sampled = rt is null ? ScalarType.Float : SampledScalar(rt);
                int n = Count(ops[2].Type);
                uint texel = Id(ops[2]);
                var texelScalar = ops[2].Type.ScalarElement ?? sampled;
                if (texelScalar != sampled)
                {
                    texel = ConvertValue(texel, ops[2].Type, Shape(sampled, n));
                }

                if (n < 4)
                {
                    var parts = new List<uint> { texel };
                    for (int c = n; c < 4; c++)
                    {
                        parts.Add(ConstId(ScalarOf(sampled, 0)));
                    }

                    texel = Emit(Op.CompositeConstruct, TypeId(Shape(sampled, 4)), parts.ToArray());
                }

                SpirvModule.Put(_s.Code, Op.ImageWrite, img, Id(ops[1]), texel);
                break;
            }

            case Opcode.TextureQuerySize:
            {
                uint img = LoadImage(ops[0], out var rt, out _);
                _s.Capabilities.Add(Cap.ImageQuery);
                int n = rt is null ? 2 : QuerySizeComponents(rt);
                uint sizeT = TypeId(Shape(ScalarType.UInt, n));
                bool useLod = rt is not null && !rt.IsReadWrite && rt.ResourceKind is not (ResourceTypeKind.Buffer or ResourceTypeKind.Texture2DMS or ResourceTypeKind.Texture2DMSArray);
                uint size = useLod
                    ? Emit(Op.ImageQuerySizeLod, sizeT, img, ops.Count > 1 ? Id(ops[1]) : UIntConst(0))
                    : Emit(Op.ImageQuerySize, sizeT, img);
                Finish(i, ConvertValue(size, Shape(ScalarType.UInt, n), i.Type));
                break;
            }

            case Opcode.TextureQueryLevels or Opcode.TextureQuerySamples:
            {
                uint img = LoadImage(ops[0], out _, out _);
                _s.Capabilities.Add(Cap.ImageQuery);
                uint r = Emit(i.Op == Opcode.TextureQueryLevels ? Op.ImageQueryLevels : Op.ImageQuerySamples, TypeId(ScalarType.UInt), img);
                Finish(i, ConvertValue(r, ScalarType.UInt, i.Type));
                break;
            }

            default: // TextureQueryLod
            {
                uint si = SampledImageOf(ops[0], ops[1], out _);
                _s.Capabilities.Add(Cap.ImageQuery);
                Direct(i, Op.ImageQueryLod, TypeId(i.Type), si, Id(ops[2]));
                break;
            }
        }
    }

    private static uint[] Operands(uint first, uint second, uint mask, List<uint> ids)
    {
        var all = new List<uint> { first, second };
        if (mask != 0)
        {
            all.Add(mask);
            all.AddRange(ids);
        }

        return all.ToArray();
    }

    private void AddOffset(Instruction i, int firstOffsetImmediate, ref uint mask, List<uint> ids)
    {
        uint off = OffsetConst(i.Immediates, firstOffsetImmediate);
        if (off != 0)
        {
            mask |= 0x8; // ConstOffset
            ids.Add(off);
        }
    }

    private static int QuerySizeComponents(ResourceType rt)
    {
        int dims = rt.ResourceKind switch
        {
            ResourceTypeKind.Texture1D or ResourceTypeKind.Texture1DArray or ResourceTypeKind.RWTexture1D or ResourceTypeKind.RWTexture1DArray
                or ResourceTypeKind.Buffer or ResourceTypeKind.RWBuffer => 1,
            ResourceTypeKind.Texture3D or ResourceTypeKind.RWTexture3D => 3,
            _ => 2,
        };
        bool arrayed = rt.ResourceKind is ResourceTypeKind.Texture1DArray or ResourceTypeKind.Texture2DArray or ResourceTypeKind.Texture2DMSArray
            or ResourceTypeKind.TextureCubeArray or ResourceTypeKind.RWTexture1DArray or ResourceTypeKind.RWTexture2DArray;
        return dims + (arrayed ? 1 : 0);
    }

    private void EmitTextureLoad(Instruction i)
    {
        var ops = i.Operands;
        uint img = LoadImage(ops[0], out var rt, out uint loadImageType);
        if (rt is null)
        {
            Finish(i, Zero(i.Type));
            return;
        }

        var sampled = SampledScalar(rt);
        uint v4T = TypeId(Shape(sampled, 4));
        uint v4;
        if (rt.IsReadWrite)
        {
            if (!_formattedImages.Contains(loadImageType))
            {
                _s.Capabilities.Add(Cap.StorageImageReadWithoutFormat);
            }

            v4 = Emit(Op.ImageRead, v4T, img, Id(ops[1]));
        }
        else
        {
            uint mask = 0;
            var ids = new List<uint>();
            bool ms = rt.ResourceKind is ResourceTypeKind.Texture2DMS or ResourceTypeKind.Texture2DMSArray;
            bool buffer = rt.ResourceKind == ResourceTypeKind.Buffer;
            if (ops.Count > 2 && !buffer)
            {
                if (!ms)
                {
                    mask |= 0x2;
                    ids.Add(Id(ops[2]));
                }
            }

            AddOffset(i, 0, ref mask, ids);
            if (ops.Count > 2 && ms)
            {
                mask |= 0x40;
                ids.Add(Id(ops[2]));
            }

            v4 = Emit(Op.ImageFetch, v4T, Operands(img, Id(ops[1]), mask, ids));
        }

        Finish(i, FitTexel(v4, sampled, i.Type));
    }

    private void EmitByteAddress(Instruction i)
    {
        uint buf = Ptr(i.Operands[0], out _);
        uint u32 = TypeId(ScalarType.UInt);
        uint word = Emit(Op.ShiftRightLogical, u32, Id(i.Operands[1]), UIntConst(2));
        uint zero = UIntConst(0);
        uint ptrT = PointerTo(Sc.StorageBuffer, u32);
        bool load = i.Op == Opcode.ByteAddressLoad;
        var valueType = load ? i.Type : i.Operands[2].Type;
        int n = Count(valueType);
        uint stored = 0;
        if (!load)
        {
            stored = ConvertValue(Id(i.Operands[2]), valueType, Shape(ScalarType.UInt, n));
        }

        var parts = new uint[n];
        for (int c = 0; c < n; c++)
        {
            uint idx = c == 0 ? word : Emit(Op.IAdd, u32, word, UIntConst((uint)c));
            uint p = Emit(Op.AccessChain, ptrT, buf, zero, idx);
            if (load)
            {
                parts[c] = Emit(Op.Load, u32, p);
            }
            else
            {
                uint comp = n == 1 ? stored : Emit(Op.CompositeExtract, u32, stored, (uint)c);
                SpirvModule.Put(_s.Code, Op.Store, p, comp);
            }
        }

        if (load)
        {
            uint all = n == 1 ? parts[0] : Emit(Op.CompositeConstruct, TypeId(Shape(ScalarType.UInt, n)), parts);
            Finish(i, ConvertValue(all, Shape(ScalarType.UInt, n), i.Type));
        }
    }
}
