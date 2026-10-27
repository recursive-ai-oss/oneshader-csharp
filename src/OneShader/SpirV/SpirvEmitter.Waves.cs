using OneShader.IR;
using OneShader.Types;

namespace OneShader.SpirV;

internal sealed partial class SpirvEmitter
{
    private const uint SubgroupScope = 3;
    private const uint Reduce = 0;
    private const uint ExclusiveScan = 2;

    /// <summary>
    /// Wave and quad intrinsics as GroupNonUniform instructions, picked the way DXC's SPIR-V output does:
    /// <c>WaveReadLaneAt</c> is a shuffle, the count-bits family is a ballot bit count, <c>QuadReadAcross*</c> a quad swap.
    /// </summary>
    private void EmitWave(Instruction i)
    {
        uint rt = TypeId(i.Type);
        uint scope = UIntConst(SubgroupScope);
        _s.Capabilities.Add(Cap.GroupNonUniform);
        NK k = i.Operands.Count > 0 ? KindOf(i.Operands[0].Type) : NK.Other;
        switch (i.Intrinsic)
        {
            case Intrinsic.WaveIsFirstLane:
                Direct(i, Op.GroupNonUniformElect, rt, scope);
                break;
            case Intrinsic.WaveActiveAnyTrue:
                _s.Capabilities.Add(Cap.GroupNonUniformVote);
                Direct(i, Op.GroupNonUniformAny, rt, scope, Arg(i, 0));
                break;
            case Intrinsic.WaveActiveAllTrue:
                _s.Capabilities.Add(Cap.GroupNonUniformVote);
                Direct(i, Op.GroupNonUniformAll, rt, scope, Arg(i, 0));
                break;
            case Intrinsic.WaveActiveAllEqual:
                _s.Capabilities.Add(Cap.GroupNonUniformVote);
                EmitAllEqual(i, scope);
                break;
            case Intrinsic.WaveActiveBallot:
                _s.Capabilities.Add(Cap.GroupNonUniformBallot);
                Direct(i, Op.GroupNonUniformBallot, rt, scope, Arg(i, 0));
                break;
            case Intrinsic.WaveActiveCountBits or Intrinsic.WavePrefixCountBits:
            {
                _s.Capabilities.Add(Cap.GroupNonUniformBallot);
                uint ballot = Emit(Op.GroupNonUniformBallot, TypeId(HlslType.Vector(ScalarType.UInt, 4)), scope, Arg(i, 0));
                uint groupOp = i.Intrinsic == Intrinsic.WaveActiveCountBits ? Reduce : ExclusiveScan; // a literal enumerant, not an id
                Direct(i, Op.GroupNonUniformBallotBitCount, rt, scope, groupOp, ballot);
                break;
            }

            case Intrinsic.WaveReadLaneFirst:
                _s.Capabilities.Add(Cap.GroupNonUniformBallot);
                Direct(i, Op.GroupNonUniformBroadcastFirst, rt, scope, Arg(i, 0));
                break;
            case Intrinsic.WaveReadLaneAt:
                _s.Capabilities.Add(Cap.GroupNonUniformShuffle);
                Direct(i, Op.GroupNonUniformShuffle, rt, scope, Arg(i, 0), Arg(i, 1));
                break;
            case Intrinsic.QuadReadAcrossX or Intrinsic.QuadReadAcrossY or Intrinsic.QuadReadAcrossDiagonal:
            {
                _s.Capabilities.Add(Cap.GroupNonUniformQuad);
                uint direction = i.Intrinsic == Intrinsic.QuadReadAcrossX ? 0u : i.Intrinsic == Intrinsic.QuadReadAcrossY ? 1u : 2u;
                Direct(i, Op.GroupNonUniformQuadSwap, rt, scope, Arg(i, 0), UIntConst(direction));
                break;
            }

            case Intrinsic.QuadReadLaneAt:
                _s.Capabilities.Add(Cap.GroupNonUniformQuad);
                Direct(i, Op.GroupNonUniformQuadBroadcast, rt, scope, Arg(i, 0), Arg(i, 1));
                break;
            default:
            {
                // The arithmetic family: reductions, and exclusive scans for the prefix forms.
                _s.Capabilities.Add(Cap.GroupNonUniformArithmetic);
                bool prefix = i.Intrinsic is Intrinsic.WavePrefixSum or Intrinsic.WavePrefixProduct;
                bool product = i.Intrinsic is Intrinsic.WaveActiveProduct or Intrinsic.WavePrefixProduct;
                uint op;
                switch (i.Intrinsic)
                {
                    case Intrinsic.WaveActiveMin:
                        op = k == NK.Float ? Op.GroupNonUniformFMin : k == NK.SInt ? Op.GroupNonUniformSMin : Op.GroupNonUniformUMin;
                        break;
                    case Intrinsic.WaveActiveMax:
                        op = k == NK.Float ? Op.GroupNonUniformFMax : k == NK.SInt ? Op.GroupNonUniformSMax : Op.GroupNonUniformUMax;
                        break;
                    case Intrinsic.WaveActiveBitAnd:
                        op = Op.GroupNonUniformBitwiseAnd;
                        break;
                    case Intrinsic.WaveActiveBitOr:
                        op = Op.GroupNonUniformBitwiseOr;
                        break;
                    case Intrinsic.WaveActiveBitXor:
                        op = Op.GroupNonUniformBitwiseXor;
                        break;
                    default:
                        op = product
                            ? (k == NK.Float ? Op.GroupNonUniformFMul : Op.GroupNonUniformIMul)
                            : (k == NK.Float ? Op.GroupNonUniformFAdd : Op.GroupNonUniformIAdd);
                        break;
                }

                Direct(i, op, rt, scope, prefix ? ExclusiveScan : Reduce, Arg(i, 0));
                break;
            }
        }
    }

    /// <summary>OpGroupNonUniformAllEqual compares a whole vector; HLSL compares per component, so vectors are taken apart.</summary>
    private void EmitAllEqual(Instruction i, uint scope)
    {
        var src = i.Operands[0].Type;
        int n = Count(src);
        uint boolType = TypeId(ScalarType.Bool);
        if (n == 1)
        {
            Direct(i, Op.GroupNonUniformAllEqual, boolType, scope, Arg(i, 0));
            return;
        }

        uint element = TypeId(src.ScalarElement!);
        var parts = new uint[n];
        for (int c = 0; c < n; c++)
        {
            uint lane = Emit(Op.CompositeExtract, element, Arg(i, 0), (uint)c);
            parts[c] = Emit(Op.GroupNonUniformAllEqual, boolType, scope, lane);
        }

        Direct(i, Op.CompositeConstruct, TypeId(i.Type), parts);
    }
}
