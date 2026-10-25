// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: smallest and largest depth of every 16x16 screen tile, as used to narrow
// the depth range of tiled light culling. Positive floats order like their bit patterns, so group-shared
// integer atomics on the bits reduce the tile. Zero depth marks empty background and is ignored.

#define TILE 16

[[vk::binding(0, 0)]] cbuffer Params
{
    uint2 Size;
    uint2 Unused;
};

[[vk::binding(1, 0)]] Texture2D<float> Depth;
[[vk::binding(2, 0)]] RWStructuredBuffer<float2> TileBounds; // min, max; (0, 0) for an empty tile

groupshared uint MinBits;
groupshared uint MaxBits;

[shader("compute")]
[numthreads(TILE, TILE, 1)]
void CSMain(uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID, uint index : SV_GroupIndex)
{
    if (index == 0)
    {
        MinBits = 0xFFFFFFFFu;
        MaxBits = 0u;
    }
    GroupMemoryBarrierWithGroupSync();

    uint2 pixel = group.xy * TILE + thread.xy;
    if (all(pixel < Size))
    {
        float depth = Depth.Load(int3(pixel, 0));
        if (depth > 0.0)
        {
            uint bits = asuint(depth);
            InterlockedMin(MinBits, bits);
            InterlockedMax(MaxBits, bits);
        }
    }
    GroupMemoryBarrierWithGroupSync();

    if (index == 0)
    {
        uint tilesX = (Size.x + TILE - 1) / TILE;
        TileBounds[group.x + group.y * tilesX] = MaxBits == 0u ? float2(0.0, 0.0) : float2(asfloat(MinBits), asfloat(MaxBits));
    }
}
