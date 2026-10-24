// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: exclusive prefix sum of every 256-element block in group-shared
// memory (Hillis-Steele scan), plus one sum per block.

static const uint BlockSize = 256;

[[vk::binding(0, 0)]] StructuredBuffer<uint> Input;
[[vk::binding(1, 0)]] RWStructuredBuffer<uint> Output;
[[vk::binding(2, 0)]] RWStructuredBuffer<uint> BlockSums;

groupshared uint Scratch[BlockSize];

[shader("compute")]
[numthreads(BlockSize, 1, 1)]
void CSMain(uint3 groupId : SV_GroupID, uint groupIndex : SV_GroupIndex, uint3 dispatchId : SV_DispatchThreadID)
{
    uint value = Input[dispatchId.x];
    Scratch[groupIndex] = value;
    GroupMemoryBarrierWithGroupSync();

    for (uint offset = 1; offset < BlockSize; offset <<= 1)
    {
        // Read before anyone writes, then write: two barriers per step.
        uint left = (groupIndex >= offset) ? Scratch[groupIndex - offset] : 0;
        GroupMemoryBarrierWithGroupSync();
        Scratch[groupIndex] += left;
        GroupMemoryBarrierWithGroupSync();
    }

    Output[dispatchId.x] = Scratch[groupIndex] - value;
    if (groupIndex == BlockSize - 1)
    {
        BlockSums[groupId.x] = Scratch[groupIndex];
    }
}
