using System.Collections.Generic;
using OneShader.Reflection;

namespace OneShader.IR;

/// <summary>Finds which subgroup (wave) operations a function and everything it calls use.</summary>
internal static class SubgroupUse
{
    /// <summary>Name of the private global that holds the lane index; the entry point copies the built-in into it.</summary>
    public const string LaneIndexName = "wave.laneIndex";

    /// <summary>Name of the private global that holds the subgroup size.</summary>
    public const string LaneCountName = "wave.laneCount";

    /// <summary>The operations used by <paramref name="root"/> and its callees (walked with an explicit stack).</summary>
    public static SubgroupOperations Scan(IrFunction root, out bool usesLaneIndex, out bool usesLaneCount)
    {
        var ops = SubgroupOperations.None;
        usesLaneIndex = false;
        usesLaneCount = false;
        var seen = new HashSet<IrFunction>();
        var pending = new Stack<IrFunction>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var f = pending.Pop();
            if (!seen.Add(f))
            {
                continue;
            }

            foreach (var b in f.Blocks)
            {
                foreach (var i in b.Instructions)
                {
                    if (i.Op == Opcode.CallIntrinsic)
                    {
                        ops |= Of(i.Intrinsic);
                    }
                    else if (i.Op == Opcode.Load && i.Operands[0] is GlobalVariable { GlobalKind: GlobalKind.Private } g)
                    {
                        if (g.Name == LaneIndexName)
                        {
                            usesLaneIndex = true;
                        }
                        else if (g.Name == LaneCountName)
                        {
                            usesLaneCount = true;
                        }
                    }

                    if (i.Callee is { } callee)
                    {
                        pending.Push(callee);
                    }
                }
            }
        }

        if (usesLaneIndex || usesLaneCount)
        {
            ops |= SubgroupOperations.Basic;
        }

        return ops;
    }

    /// <summary>The Vulkan feature class an intrinsic needs.</summary>
    public static SubgroupOperations Of(Intrinsic i) => i switch
    {
        Intrinsic.WaveGetLaneCount or Intrinsic.WaveGetLaneIndex or Intrinsic.WaveIsFirstLane => SubgroupOperations.Basic,
        Intrinsic.WaveActiveAnyTrue or Intrinsic.WaveActiveAllTrue or Intrinsic.WaveActiveAllEqual => SubgroupOperations.Vote,
        Intrinsic.WaveActiveBallot or Intrinsic.WaveActiveCountBits or Intrinsic.WavePrefixCountBits or Intrinsic.WaveReadLaneFirst => SubgroupOperations.Ballot,
        Intrinsic.WaveActiveSum or Intrinsic.WaveActiveProduct or Intrinsic.WaveActiveMin or Intrinsic.WaveActiveMax
            or Intrinsic.WaveActiveBitAnd or Intrinsic.WaveActiveBitOr or Intrinsic.WaveActiveBitXor
            or Intrinsic.WavePrefixSum or Intrinsic.WavePrefixProduct => SubgroupOperations.Arithmetic,
        Intrinsic.WaveReadLaneAt => SubgroupOperations.Shuffle,
        Intrinsic.QuadReadAcrossX or Intrinsic.QuadReadAcrossY or Intrinsic.QuadReadAcrossDiagonal or Intrinsic.QuadReadLaneAt => SubgroupOperations.Quad,
        _ => SubgroupOperations.None,
    };
}
