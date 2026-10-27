// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: votes, ballots, lane queries and lane reads. Eight per-group totals are the same for
// every subgroup size (waves must be at most 128 lanes); the per-thread record in the second buffer shows what each
// lane saw and is compared with DXC's build of the same shader on the same device.

[[vk::binding(0, 0)]] StructuredBuffer<uint> Values;
[[vk::binding(1, 0)]] RWStructuredBuffer<uint> Totals;    // 8 words per group
[[vk::binding(2, 0)]] RWStructuredBuffer<uint4> PerThread;

[shader("compute")]
[numthreads(256, 1, 1)]
void CSMain(uint3 dispatchId : SV_DispatchThreadID, uint groupIndex : SV_GroupIndex, uint3 groupId : SV_GroupID)
{
    uint v = Values[dispatchId.x];
    bool p = (v % 5u) == 0u;
    // A vote that is uniform over aligned blocks of 128 threads, and one that mixes within waves.
    bool lowHalf = groupIndex < 128u * (groupId.x % 3u);
    bool mixed = (v % 7u) != 0u;

    uint4 ballot = WaveActiveBallot(p);
    uint ballotBits = countbits(ballot.x) + countbits(ballot.y) + countbits(ballot.z) + countbits(ballot.w);
    uint laneIndex = WaveGetLaneIndex();
    uint laneCount = WaveGetLaneCount();
    bool laneOk = laneIndex < laneCount && (laneCount & (laneCount - 1)) == 0;

    uint o = groupId.x * 8;
    uint ballotCount = WaveActiveCountBits(p);
    if (WaveIsFirstLane())
    {
        // Each wave's first lane reports for the whole wave, so the sums do not depend on how many waves there are.
        uint ignored;
        InterlockedAdd(Totals[o + 0], ballotCount, ignored);
        InterlockedAdd(Totals[o + 5], laneCount, ignored);
        InterlockedAdd(Totals[o + 6], ballotBits, ignored);
    }
    // The votes are evaluated by every lane, then each lane adds one if it saw "true".
    uint ignored2;
    if (WaveActiveAllTrue(lowHalf)) { InterlockedAdd(Totals[o + 1], 1u, ignored2); }
    if (WaveActiveAnyTrue(lowHalf)) { InterlockedAdd(Totals[o + 2], 1u, ignored2); }
    if (WaveActiveAllEqual(groupIndex / 128u)) { InterlockedAdd(Totals[o + 3], 1u, ignored2); }
    bool2 equalPair = WaveActiveAllEqual(uint2(groupIndex / 128u, groupIndex & 1u));
    if (equalPair.x && !equalPair.y) { InterlockedAdd(Totals[o + 4], 1u, ignored2); }
    if (laneOk) { InterlockedAdd(Totals[o + 7], 1u, ignored2); }

    uint flags = (WaveActiveAnyTrue(mixed) ? 1u : 0u) | (WaveActiveAllTrue(mixed) ? 2u : 0u)
        | (WaveActiveAllEqual(v & 1u) ? 4u : 0u) | (WaveIsFirstLane() ? 8u : 0u)
        | (WaveActiveAllEqual(float2(1.0, (float)(v & 1u))).y ? 16u : 0u);
    uint first = WaveReadLaneFirst(v);
    uint neighbour = WaveReadLaneAt(v, (laneIndex + 1u) % laneCount);
    PerThread[dispatchId.x] = uint4(WavePrefixCountBits(p) | (flags << 16), laneIndex | (laneCount << 16), first, neighbour);
}
