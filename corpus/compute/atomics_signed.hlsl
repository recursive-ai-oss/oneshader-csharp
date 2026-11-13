// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: signed and unsigned InterlockedMin/Max and InterlockedCompareExchange/CompareStore on
// structured buffers, a byte-address buffer and group-shared memory, with and without the original-value argument.
// 256 invocations hit the same words, so contention is real; every result is independent of the order the atomics run in.

[[vk::binding(0, 0)]] StructuredBuffer<uint> Input;      // random words; read as int they are about half negative
[[vk::binding(1, 0)]] RWStructuredBuffer<int> Signed;
[[vk::binding(2, 0)]] RWStructuredBuffer<uint> Unsigned;
[[vk::binding(3, 0)]] RWByteAddressBuffer Raw;
[[vk::binding(4, 0)]] RWStructuredBuffer<int> Groups;    // 16 words per group: the group-shared results

groupshared int SharedSigned[8];
groupshared uint SharedUnsigned[8];

[shader("compute")]
[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID, uint index : SV_GroupIndex, uint3 group : SV_GroupID)
{
    uint word = Input[id.x];
    int v = (int)word;

    // ---- group-shared words (reset by the first eight threads, which also covers the initial values below)
    if (index < 8)
    {
        SharedSigned[index] = 0;
        SharedUnsigned[index] = 0;
    }
    if (index == 0)
    {
        SharedSigned[0] = 2147483647;           // min
        SharedSigned[1] = -2147483647 - 1;      // max
        SharedSigned[3] = -1;                   // compare-exchange target that matches a negative comparand
        SharedSigned[4] = 2147483647;           // min of the original values the max returned
        SharedSigned[5] = 3;                    // compare-store target
        SharedUnsigned[0] = 0xFFFFFFFFu;
        SharedUnsigned[2] = 0xFFFFFFFFu;
    }
    GroupMemoryBarrierWithGroupSync();

    int o;
    InterlockedMin(SharedSigned[0], v);                       // no original
    InterlockedMax(SharedSigned[1], v, o);                    // original
    InterlockedMin(SharedSigned[4], o);                       // the first max returns INT_MIN
    InterlockedCompareExchange(SharedSigned[2], 0, 7, o);     // exactly one match; the rest read 7
    InterlockedAdd(SharedSigned[6], o);
    InterlockedCompareExchange(SharedSigned[3], -1, -2, o);   // a negative comparand
    if (o == -1)
    {
        InterlockedAdd(SharedSigned[7], 1);
    }
    InterlockedCompareStore(SharedSigned[5], 4, 9);           // never matches (4 != 3)

    uint u;
    InterlockedMin(SharedUnsigned[0], word);
    InterlockedMax(SharedUnsigned[1], word, u);
    InterlockedCompareExchange(SharedUnsigned[2], 0xFFFFFFFFu, 0x80000001u, u);
    if (u == 0xFFFFFFFFu)
    {
        InterlockedAdd(SharedUnsigned[3], 1u);
    }
    GroupMemoryBarrierWithGroupSync();

    if (index < 8)
    {
        Groups[group.x * 16 + index] = SharedSigned[index];
        Groups[group.x * 16 + 8 + index] = (int)SharedUnsigned[index];
    }

    // ---- structured buffers: the host sets the initial words (see the case file)
    int s;
    InterlockedMin(Signed[0], v);
    InterlockedMax(Signed[1], v, s);
    InterlockedMin(Signed[10], s);                             // the first max returns the initial INT_MIN
    InterlockedMin(Signed[11], v, s);
    InterlockedMax(Signed[9], s);                              // the first min returns the initial INT_MAX
    InterlockedCompareExchange(Signed[2], 0, 7, s);
    InterlockedAdd(Signed[4], s);
    if (s == 0)
    {
        InterlockedAdd(Signed[3], 1);
    }
    InterlockedCompareExchange(Signed[5], 123, 9, s);          // never matches
    InterlockedAdd(Signed[6], s);
    InterlockedCompareExchange(Signed[7], -1, -2, s);          // negative comparand
    if (s == -1)
    {
        InterlockedAdd(Signed[8], 1);
    }
    InterlockedCompareStore(Signed[12], 0, 5);                 // one store wins; the rest see 5 and do nothing

    uint t;
    InterlockedMin(Unsigned[0], word);
    InterlockedMax(Unsigned[1], word, t);
    InterlockedMin(Unsigned[8], t);
    InterlockedCompareExchange(Unsigned[2], 0xFFFFFFFFu, 0x80000001u, t);
    if (t == 0xFFFFFFFFu)
    {
        InterlockedAdd(Unsigned[3], 1u);
    }
    InterlockedCompareExchange(Unsigned[4], 5u, 6u, t);        // never matches
    InterlockedAdd(Unsigned[5], t);
    InterlockedCompareStore(Unsigned[6], 0u, 0xFFFFFFF0u);

    // ---- byte-address buffer (unsigned only in HLSL)
    uint r;
    Raw.InterlockedMin(0, word);
    Raw.InterlockedMax(4, word, r);
    Raw.InterlockedMin(24, r);
    Raw.InterlockedCompareExchange(8, 0xFFFFFFFFu, 0x80000001u, r);
    if (r == 0xFFFFFFFFu)
    {
        Raw.InterlockedAdd(12, 1u);
    }
    Raw.InterlockedCompareExchange(16, 5u, 6u, r);             // never matches
    Raw.InterlockedAdd(20, r);
    Raw.InterlockedCompareStore(28, 0u, 0xFFFFFFF0u);
}
