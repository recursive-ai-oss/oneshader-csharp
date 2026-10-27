using OneShader.IR;
using OneShader.Types;

namespace OneShader.Msl;

internal sealed partial class MslEmitter
{
    /// <summary>
    /// Wave and quad intrinsics as Metal SIMD-group and quad-group functions (MSL 5.x "SIMD-group functions" and "Quad-group functions").
    /// A SIMD-group is Metal's subgroup; its lanes are the invocations that execute together, as in the Vulkan model.
    /// </summary>
    private void EmitWave(Instruction i)
    {
        string a = i.Operands.Count > 0 ? Arg(i, 0) : "";
        switch (i.Intrinsic)
        {
            case Intrinsic.WaveIsFirstLane:
                Define(i, "simd_is_first()");
                break;
            case Intrinsic.WaveActiveAnyTrue:
                Define(i, "simd_any(" + a + ")");
                break;
            case Intrinsic.WaveActiveAllTrue:
                Define(i, "simd_all(" + a + ")");
                break;
            case Intrinsic.WaveActiveAllEqual:
            {
                int n = Comp(i.Operands[0].Type);
                string first = Tmp("first");
                W(T(i.Operands[0].Type) + " " + first + " = simd_broadcast_first(" + a + ");");
                if (n == 1)
                {
                    Define(i, "simd_all(" + a + " == " + first + ")");
                    break;
                }

                // HLSL compares each component on its own; simd_all takes one bool.
                string v = Atom(a, i.Operands[0].Type);
                string[] lanes = ["x", "y", "z", "w"];
                var parts = new string[n];
                for (int c = 0; c < n; c++)
                {
                    parts[c] = "simd_all(" + v + "." + lanes[c] + " == " + first + "." + lanes[c] + ")";
                }

                Define(i, T(i.Type) + "(" + string.Join(", ", parts) + ")");
                break;
            }

            case Intrinsic.WaveActiveBallot:
            {
                // simd_ballot gives one bit per lane in a 64-bit vote; HLSL wants four 32-bit words.
                string bits = Tmp("ballot");
                W("ulong " + bits + " = static_cast<ulong>(simd_ballot(" + a + "));");
                Define(i, "uint4(uint(" + bits + "), uint(" + bits + " >> 32), 0u, 0u)");
                break;
            }

            case Intrinsic.WaveActiveCountBits:
                Define(i, "simd_sum(uint(" + a + "))");
                break;
            case Intrinsic.WavePrefixCountBits:
                Define(i, "simd_prefix_exclusive_sum(uint(" + a + "))");
                break;
            case Intrinsic.WaveActiveSum: Define(i, "simd_sum(" + a + ")"); break;
            case Intrinsic.WaveActiveProduct: Define(i, "simd_product(" + a + ")"); break;
            case Intrinsic.WaveActiveMin: Define(i, "simd_min(" + a + ")"); break;
            case Intrinsic.WaveActiveMax: Define(i, "simd_max(" + a + ")"); break;
            case Intrinsic.WaveActiveBitAnd: Define(i, "simd_and(" + a + ")"); break;
            case Intrinsic.WaveActiveBitOr: Define(i, "simd_or(" + a + ")"); break;
            case Intrinsic.WaveActiveBitXor: Define(i, "simd_xor(" + a + ")"); break;
            case Intrinsic.WavePrefixSum: Define(i, "simd_prefix_exclusive_sum(" + a + ")"); break;
            case Intrinsic.WavePrefixProduct: Define(i, "simd_prefix_exclusive_product(" + a + ")"); break;
            case Intrinsic.WaveReadLaneFirst: Define(i, "simd_broadcast_first(" + a + ")"); break;
            case Intrinsic.WaveReadLaneAt:
                // simd_shuffle takes any lane; simd_broadcast wants the same lane in every thread.
                Define(i, "simd_shuffle(" + a + ", ushort(" + Arg(i, 1) + "))");
                break;
            case Intrinsic.QuadReadAcrossX: Define(i, "quad_shuffle_xor(" + a + ", ushort(1))"); break;
            case Intrinsic.QuadReadAcrossY: Define(i, "quad_shuffle_xor(" + a + ", ushort(2))"); break;
            case Intrinsic.QuadReadAcrossDiagonal: Define(i, "quad_shuffle_xor(" + a + ", ushort(3))"); break;
            case Intrinsic.QuadReadLaneAt: Define(i, "quad_shuffle(" + a + ", ushort(" + Arg(i, 1) + "))"); break;
            default:
                Unsupported("The intrinsic '" + i.Intrinsic + "' is not supported by the MSL backend.", i.Span);
                Define(i, T(i.Type) + "()");
                break;
        }
    }
}
