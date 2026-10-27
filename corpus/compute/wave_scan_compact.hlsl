// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: block-wide exclusive scan and stream compaction built from wave operations.
// Every result is the same for any subgroup size: waves publish their totals in group-shared memory under the
// group index of their first lane, and each wave adds up the totals of the waves before it.

[[vk::binding(0, 0)]] StructuredBuffer<uint> Values;
[[vk::binding(1, 0)]] RWStructuredBuffer<uint> Scan;       // exclusive scan inside each group of 256
[[vk::binding(2, 0)]] RWStructuredBuffer<uint> BlockSums;  // sum of each group
[[vk::binding(3, 0)]] RWStructuredBuffer<uint> Compacted;  // the values divisible by 3, packed at the start of the group's 256 slots
[[vk::binding(4, 0)]] RWStructuredBuffer<uint> KeptCounts; // how many values each group kept

groupshared uint WaveSum[256];
groupshared uint WaveKept[256];

[shader("compute")]
[numthreads(256, 1, 1)]
void CSMain(uint3 dispatchId : SV_DispatchThreadID, uint groupIndex : SV_GroupIndex, uint3 groupId : SV_GroupID)
{
    WaveSum[groupIndex] = 0;
    WaveKept[groupIndex] = 0;
    GroupMemoryBarrierWithGroupSync();

    uint value = Values[dispatchId.x];
    bool keep = (value % 3u) == 0u;

    // Scans inside this wave, and the wave's totals.
    uint scanInWave = WavePrefixSum(value);
    uint keptInWave = WavePrefixCountBits(keep);
    uint waveSum = WaveActiveSum(value);
    uint waveKept = WaveActiveCountBits(keep);

    // The first lane's group index names the wave; no assumption about the wave size.
    uint waveId = WaveReadLaneFirst(groupIndex);
    if (WaveIsFirstLane())
    {
        WaveSum[waveId] = waveSum;
        WaveKept[waveId] = waveKept;
    }
    GroupMemoryBarrierWithGroupSync();

    uint sumBefore = 0;
    uint keptBefore = 0;
    for (uint i = 0; i < waveId; i++)
    {
        sumBefore += WaveSum[i];
        keptBefore += WaveKept[i];
    }

    Scan[dispatchId.x] = sumBefore + scanInWave;
    if (keep)
    {
        Compacted[groupId.x * 256 + keptBefore + keptInWave] = value;
    }

    if (groupIndex == 0)
    {
        uint sum = 0;
        uint kept = 0;
        for (uint j = 0; j < 256; j++)
        {
            sum += WaveSum[j];
            kept += WaveKept[j];
        }
        BlockSums[groupId.x] = sum;
        KeptCounts[groupId.x] = kept;
    }
}
