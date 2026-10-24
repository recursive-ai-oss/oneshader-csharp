// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: 3x3 sharpen filter over a storage image. Each 8x8 group first
// loads its tile plus a one-texel apron into group-shared memory, then filters from there.

static const int TileSize = 8;
static const int Apron = 1;
static const int Cache = TileSize + 2 * Apron;

[[vk::binding(0, 0)]] Texture2D<float4> Source;
[[vk::binding(1, 0)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> Result;

groupshared float4 Tile[Cache * Cache];

[shader("compute")]
[numthreads(TileSize, TileSize, 1)]
void CSMain(uint3 groupId : SV_GroupID, uint3 localId : SV_GroupThreadID, uint groupIndex : SV_GroupIndex)
{
    uint width, height;
    Source.GetDimensions(width, height);
    int2 maxCoord = int2(width, height) - 1;
    int2 origin = int2(groupId.xy) * TileSize - Apron;

    // 64 threads fill 100 cache entries; edges clamp.
    for (int i = (int)groupIndex; i < Cache * Cache; i += TileSize * TileSize)
    {
        int2 coord = clamp(origin + int2(i % Cache, i / Cache), int2(0, 0), maxCoord);
        Tile[i] = Source.Load(int3(coord, 0));
    }
    GroupMemoryBarrierWithGroupSync();

    int2 local = int2(localId.xy) + Apron;
    float4 centre = Tile[local.y * Cache + local.x];
    float4 neighbours = 0.0;
    neighbours += Tile[(local.y - 1) * Cache + local.x];
    neighbours += Tile[(local.y + 1) * Cache + local.x];
    neighbours += Tile[local.y * Cache + local.x - 1];
    neighbours += Tile[local.y * Cache + local.x + 1];

    float4 sharpened = centre * 5.0 - neighbours;
    if (all(int2(groupId.xy) * TileSize + int2(localId.xy) <= maxCoord))
    {
        Result[int2(groupId.xy) * TileSize + int2(localId.xy)] = float4(saturate(sharpened.rgb), centre.a);
    }
}
