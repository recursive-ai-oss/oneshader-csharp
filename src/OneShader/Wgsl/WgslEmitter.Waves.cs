using OneShader.IR;
using OneShader.Reflection;
using OneShader.Types;

namespace OneShader.Wgsl;

internal sealed partial class WgslEmitter
{
    /// <summary>
    /// Wave and quad intrinsics as WebGPU subgroup functions (the optional <c>subgroups</c> feature; the backend writes
    /// <c>enable subgroups;</c>). Operations WGSL has no single function for are built from the ones it has.
    /// </summary>
    private void EmitWave(Instruction i)
    {
        if (_ep.Stage == ShaderStage.Vertex)
        {
            Unsupported("The intrinsic '" + i.Intrinsic + "' is not available in a vertex shader in WGSL: WebGPU's subgroup functions exist only in compute and fragment shaders.", i.Span);
            Define(i, Zero(i.Type));
            return;
        }

        string a = i.Operands.Count > 0 ? Arg(i, 0) : "";
        switch (i.Intrinsic)
        {
            case Intrinsic.WaveIsFirstLane: Define(i, "subgroupElect()"); break;
            case Intrinsic.WaveActiveAnyTrue: Define(i, "subgroupAny(" + a + ")"); break;
            case Intrinsic.WaveActiveAllTrue: Define(i, "subgroupAll(" + a + ")"); break;
            case Intrinsic.WaveActiveBallot: Define(i, "subgroupBallot(" + a + ")"); break;
            case Intrinsic.WaveActiveCountBits: Define(i, "subgroupAdd(u32(" + a + "))"); break;
            case Intrinsic.WavePrefixCountBits: Define(i, "subgroupExclusiveAdd(u32(" + a + "))"); break;
            case Intrinsic.WaveActiveSum: Define(i, "subgroupAdd(" + a + ")"); break;
            case Intrinsic.WaveActiveProduct: Define(i, "subgroupMul(" + a + ")"); break;
            case Intrinsic.WaveActiveMin: Define(i, "subgroupMin(" + a + ")"); break;
            case Intrinsic.WaveActiveMax: Define(i, "subgroupMax(" + a + ")"); break;
            case Intrinsic.WaveActiveBitAnd: Define(i, "subgroupAnd(" + a + ")"); break;
            case Intrinsic.WaveActiveBitOr: Define(i, "subgroupOr(" + a + ")"); break;
            case Intrinsic.WaveActiveBitXor: Define(i, "subgroupXor(" + a + ")"); break;
            case Intrinsic.WavePrefixSum: Define(i, "subgroupExclusiveAdd(" + a + ")"); break;
            case Intrinsic.WavePrefixProduct: Define(i, "subgroupExclusiveMul(" + a + ")"); break;
            case Intrinsic.WaveReadLaneFirst: Define(i, "subgroupBroadcastFirst(" + a + ")"); break;
            case Intrinsic.WaveReadLaneAt: Define(i, "subgroupShuffle(" + a + ", " + Arg(i, 1) + ")"); break;
            case Intrinsic.QuadReadAcrossX: Define(i, "quadSwapX(" + a + ")"); break;
            case Intrinsic.QuadReadAcrossY: Define(i, "quadSwapY(" + a + ")"); break;
            case Intrinsic.QuadReadAcrossDiagonal: Define(i, "quadSwapDiagonal(" + a + ")"); break;
            case Intrinsic.QuadReadLaneAt: Define(i, "quadBroadcast(" + a + ", " + Arg(i, 1) + ")"); break;
            case Intrinsic.WaveActiveAllEqual:
            {
                // No single function: compare with the first active lane's value; HLSL does this per component.
                int n = i.Operands[0].Type is VectorType v ? v.Count : 1;
                string value = Simple(a);
                string first = Temp("subgroupBroadcastFirst(" + value + ")");
                if (n == 1)
                {
                    Define(i, "subgroupAll(" + value + " == " + first + ")");
                    break;
                }

                string[] lanes = ["x", "y", "z", "w"];
                var parts = new string[n];
                for (int c = 0; c < n; c++)
                {
                    parts[c] = "subgroupAll(" + value + "." + lanes[c] + " == " + first + "." + lanes[c] + ")";
                }

                Define(i, TypeName(i.Type) + "(" + string.Join(", ", parts) + ")");
                break;
            }

            default:
                Unsupported("The intrinsic '" + i.Intrinsic + "' is not supported by the WGSL backend.", i.Span);
                Define(i, Zero(i.Type));
                break;
        }
    }
}
