// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: tile rendering from a tile-index buffer and a texture array.
//
// One instance per grid cell; the quad is expanded from SV_VertexID and the tile layer is read
// from the index buffer by instance. An index of 0xFFFFFFFF marks an empty cell.

[[vk::binding(0, 0)]] cbuffer Map
{
    float4x4 ViewProjection;
    float2 Origin;   // pixel position of the top-left cell
    float2 TileSize; // pixels per cell
    uint GridWidth;  // cells per row
    uint3 MapPad;
};

[[vk::binding(1, 0)]] StructuredBuffer<uint> Tiles;
[[vk::binding(2, 0)]] Texture2DArray<float4> Tileset;
[[vk::binding(3, 0)]] SamplerState TilesetSampler;

struct VertexOutput
{
    float4 Position : SV_Position;
    float2 Uv : TEXCOORD0;
    nointerpolation uint Layer : TEXCOORD1;
};

[shader("vertex")]
VertexOutput VSMain(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
{
    // Corner order of two triangles: 0 1 2, 2 1 3 (bit pairs packed into one constant).
    uint corner = (0xDA4u >> (vertexId * 2u)) & 3u;
    float2 unit = float2(corner & 1u, corner >> 1u);

    uint cellX = instanceId % GridWidth;
    uint cellY = instanceId / GridWidth;
    float2 pixel = Origin + (float2(cellX, cellY) + unit) * TileSize;

    VertexOutput output;
    output.Position = mul(ViewProjection, float4(pixel, 0.0, 1.0));
    output.Uv = unit;
    output.Layer = Tiles[instanceId];
    return output;
}

[shader("pixel")]
float4 PSMain(VertexOutput input) : SV_Target0
{
    if (input.Layer == 0xFFFFFFFFu)
    {
        return float4(0.0, 0.0, 0.0, 0.0);
    }
    float4 color = Tileset.Sample(TilesetSampler, float3(input.Uv, (float)input.Layer));
    return float4(color.rgb * color.a, color.a);
}
