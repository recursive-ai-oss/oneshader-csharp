// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: 64-bit integers in a constant buffer and structured buffers (wrapping arithmetic, shifts,
// division, comparisons, conversions) and 64-bit atomics on a structured buffer and on group-shared memory.

struct Item
{
    int64_t a;
    uint64_t b;
    int64_t2 pair;
    uint tag;
    uint64_t3 trio;
};

[[vk::binding(0, 0)]] cbuffer Params
{
    int64_t Offset;
    uint64_t Mask;
    uint Shift;
    int64_t2 Step;
};

[[vk::binding(1, 0)]] StructuredBuffer<Item> Items;
[[vk::binding(2, 0)]] RWStructuredBuffer<Item> Result;
[[vk::binding(3, 0)]] RWStructuredBuffer<uint64_t> Unsigned;
[[vk::binding(4, 0)]] RWStructuredBuffer<int64_t> Signed;
[[vk::binding(5, 0)]] RWStructuredBuffer<float4> Floats;

groupshared uint64_t SharedSum;
groupshared uint64_t SharedBits;
groupshared int64_t SharedMin;
groupshared int64_t SharedMax;

[shader("compute")]
[numthreads(16, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID, uint gi : SV_GroupIndex)
{
    Item it = Items[id.x];

    // Wrapping arithmetic at 64 bits.
    int64_t sq = it.a * it.a;
    int64_t sum = it.a + Offset;
    uint64_t prod = it.b * Mask;
    uint64_t diff = it.b - Mask;
    int64_t2 stepped = it.pair + Step * 3;
    uint64_t3 mixed = it.trio * Mask + it.b;

    // Shifts and bit operations.
    uint amount = Shift & 63u;
    int64_t sar = it.a >> amount;
    uint64_t shr = it.b >> amount;
    uint64_t shl = it.b << amount;
    uint64_t bits = (it.b & Mask) | (shl ^ 0x00FF00FF00FF00FFull);
    int64_t inv = ~it.a;

    // Division and remainder by non-zero divisors.
    int64_t dsor = (int64_t)((uint64_t)it.pair.x % 1000003ull) + 5;
    int64_t quot = it.a / dsor;
    int64_t rem = it.a % dsor;
    uint64_t uq = it.b / (Mask | 1);
    uint64_t ur = it.b % ((Mask >> 3) | 1);

    // Comparisons, selection, clamping.
    int64_t pick = it.a < Offset ? it.a : Offset;
    int64_t clamped = clamp(it.a, -1000000000000ll, 1000000000000ll);
    int64_t ab = abs(it.a >> 1);
    uint64_t hi = max(it.b, Mask);
    uint64_t lo = min(it.b, Mask);

    // Conversions. (int64 to float goes through double: DXC narrows a direct int64-to-float cast to 32 bits first.)
    int narrowed = (int)it.a;
    uint unarrowed = (uint)it.b;
    int64_t widened = (int64_t)(int)it.tag;
    uint64_t uwidened = (uint64_t)it.tag;
    float asFloat = (float)(double)it.pair.y;
    double asDouble = (double)it.b;
    int64_t fromFloat = (int64_t)(asFloat * 0.25f);

    Item o;
    o.a = sq + sum + sar + quot - rem;
    o.b = prod + diff + shr + bits + uq + ur;
    o.pair = stepped + int64_t2(inv, pick);
    o.tag = (uint)narrowed ^ unarrowed;
    o.trio = mixed + uint64_t3(hi, lo, uwidened);
    Result[id.x] = o;
    Floats[id.x] = float4(asFloat, (float)asDouble, (float)(double)(clamped + ab + widened), (float)(double)fromFloat);

    // Atomics on a structured buffer: every thread adds, the others combine commutatively.
    InterlockedAdd(Unsigned[0], it.b);
    InterlockedMin(Unsigned[1], it.b);
    InterlockedMax(Unsigned[2], it.b);
    InterlockedAnd(Unsigned[3], it.b | 0xF0F0F0F0F0F0F0F0ull);
    InterlockedOr(Unsigned[4], it.b & 0x0000FFFF0000FFFFull);
    InterlockedXor(Unsigned[5], it.b);
    InterlockedAdd(Signed[0], it.a);
    InterlockedMin(Signed[1], it.a);
    InterlockedMax(Signed[2], it.a);

    // Atomics on group-shared memory, combined into the buffer by the first thread of each group.
    if (gi == 0)
    {
        SharedSum = 0;
        SharedBits = 0;
        SharedMin = 0x7FFFFFFFFFFFFFFFll;
        SharedMax = -0x7FFFFFFFFFFFFFFFll;
    }
    GroupMemoryBarrierWithGroupSync();
    InterlockedAdd(SharedSum, it.b >> 1);
    InterlockedOr(SharedBits, it.b & 0xFF00000000000000ull);
    InterlockedMin(SharedMin, it.a);
    InterlockedMax(SharedMax, it.a);
    GroupMemoryBarrierWithGroupSync();
    if (gi == 0)
    {
        InterlockedAdd(Unsigned[6], SharedSum);
        InterlockedOr(Unsigned[7], SharedBits);
        InterlockedMin(Signed[3], SharedMin);
        InterlockedMax(Signed[4], SharedMax);

        // Exchange and compare-exchange with results that do not depend on thread order: one thread, a known slot.
        uint64_t old;
        if (id.x == 0)
        {
            InterlockedExchange(Unsigned[8], 0x1122334455667788ull, old);
            Unsigned[9] = old;
            uint64_t seen;
            InterlockedCompareExchange(Unsigned[10], 77ull, 0xAABBCCDDEEFF0011ull, seen);
            Unsigned[11] = seen;
            InterlockedCompareExchange(Unsigned[10], 77ull, 5ull, seen);
            Unsigned[12] = seen;
        }
    }
}
