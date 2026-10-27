// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: group reductions from wave reductions (sum, product, min, max, bit operations,
// signed and float forms, vectors). Each wave reduces its lanes, one lane per wave combines the result into
// group-shared state, and the first thread writes 16 words per group. The values are chosen so that the
// result does not depend on the order or size of the waves (integers wrap, floats stay exact).

[[vk::binding(0, 0)]] StructuredBuffer<uint> Values;
[[vk::binding(1, 0)]] RWStructuredBuffer<uint> Results;

groupshared uint Acc[16];
groupshared uint ProductSlot[256];
groupshared float FloatMinSlot[256];
groupshared float FloatMaxSlot[256];
groupshared float FloatSumSlot[256];
groupshared float Vec3MaxSlot[256];

[shader("compute")]
[numthreads(256, 1, 1)]
void CSMain(uint3 dispatchId : SV_DispatchThreadID, uint groupIndex : SV_GroupIndex, uint3 groupId : SV_GroupID)
{
    if (groupIndex < 16)
    {
        // Identity of each accumulator: min, and and the biased signed min start with all bits set, the others at zero.
        uint init = 0u;
        if (groupIndex == 1 || groupIndex == 3 || groupIndex == 10)
        {
            init = 0xFFFFFFFFu;
        }
        Acc[groupIndex] = init;
    }
    ProductSlot[groupIndex] = 1u;
    FloatMinSlot[groupIndex] = 1.0e30;
    FloatMaxSlot[groupIndex] = -1.0e30;
    FloatSumSlot[groupIndex] = 0.0;
    Vec3MaxSlot[groupIndex] = -1.0e30;
    GroupMemoryBarrierWithGroupSync();

    uint v = Values[dispatchId.x];
    int s = (int)v - 500;
    float f = (float)s;
    uint masked = (v << 20) | 0x0F0F0000u;
    uint2 pair = uint2(v, v & 7u);
    float3 triple = float3((float)(v & 255u), -(float)v, 2.0 * (float)v);

    uint sum = WaveActiveSum(v);
    uint lo = WaveActiveMin(v);
    uint hi = WaveActiveMax(v);
    uint bitsAnd = WaveActiveBitAnd(masked);
    uint bitsOr = WaveActiveBitOr(v);
    uint parity = WaveActiveBitXor(v);
    uint product = WaveActiveProduct(v | 1u);
    float fmin = WaveActiveMin(f);
    float fmax = WaveActiveMax(f);
    float fsum = WaveActiveSum((float)(v & 255u));
    int smin = WaveActiveMin(s);
    int smax = WaveActiveMax(s);
    uint2 pairSum = WaveActiveSum(pair);
    float3 tripleMax = WaveActiveMax(triple);
    uint odd = WaveActiveCountBits((v & 1u) != 0u);

    uint waveId = WaveReadLaneFirst(groupIndex);
    if (WaveIsFirstLane())
    {
        uint ignored;
        InterlockedAdd(Acc[0], sum, ignored);
        InterlockedMin(Acc[1], lo, ignored);
        InterlockedMax(Acc[2], hi, ignored);
        InterlockedAnd(Acc[3], bitsAnd, ignored);
        InterlockedOr(Acc[4], bitsOr, ignored);
        InterlockedXor(Acc[5], parity, ignored);
        InterlockedMin(Acc[10], asuint(smin) ^ 0x80000000u, ignored);
        InterlockedMax(Acc[11], asuint(smax) ^ 0x80000000u, ignored);
        InterlockedAdd(Acc[12], pairSum.x, ignored);
        InterlockedAdd(Acc[13], pairSum.y, ignored);
        InterlockedAdd(Acc[15], odd, ignored);
        ProductSlot[waveId] = product;
        FloatMinSlot[waveId] = fmin;
        FloatMaxSlot[waveId] = fmax;
        FloatSumSlot[waveId] = fsum;
        Vec3MaxSlot[waveId] = tripleMax.z;
    }
    GroupMemoryBarrierWithGroupSync();

    if (groupIndex == 0)
    {
        uint prod = 1;
        float fmn = 1.0e30;
        float fmx = -1.0e30;
        float fs = 0.0;
        float vm = -1.0e30;
        for (uint i = 0; i < 256; i++)
        {
            // Slots of lanes that are not the first of their wave were never written; they still hold the identity.
            prod *= ProductSlot[i];
            fmn = min(fmn, FloatMinSlot[i]);
            fmx = max(fmx, FloatMaxSlot[i]);
            fs += FloatSumSlot[i];
            vm = max(vm, Vec3MaxSlot[i]);
        }
        uint o = groupId.x * 16;
        Results[o + 0] = Acc[0];
        Results[o + 1] = Acc[1];
        Results[o + 2] = Acc[2];
        Results[o + 3] = Acc[3];
        Results[o + 4] = Acc[4];
        Results[o + 5] = Acc[5];
        Results[o + 6] = prod;
        Results[o + 7] = asuint(fmn);
        Results[o + 8] = asuint(fmx);
        Results[o + 9] = asuint(fs);
        Results[o + 10] = Acc[10] ^ 0x80000000u;
        Results[o + 11] = Acc[11] ^ 0x80000000u;
        Results[o + 12] = Acc[12];
        Results[o + 13] = Acc[13];
        Results[o + 14] = asuint(vm);
        Results[o + 15] = Acc[15];
    }
}
