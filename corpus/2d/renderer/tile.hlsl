// SPDX-License-Identifier: MIT OR Apache-2.0
// Copyright (c) Recursive AI, LLC. Taken unchanged from a 2D game renderer by the same authors.

// Tilemap chunks. One quad per visible chunk; the pixel shader finds the tile under each pixel.
//
// Near zoom ("ids"): a chunk is a layer of an R32Uint array holding tile ids; id 0 is empty and id n is cell n-1 of the tileset,
// counted left to right, top to bottom. Far zoom ("flat"): a chunk is a layer of an sRGB array holding one premultiplied colour per
// coarse tile (the average of the tiles beneath it), so the work stays one fetch per pixel however large the map is.
//
// The vertex stage is the one in Quad.hlsl; its uv is in tile units inside the chunk (0..64) and its Extra field is the pool layer.
//   set 1: tileset texture and sampler
//   set 2: binding 0 tile parameters, binding 1 id pool, binding 2 colour pool

[[vk::binding(0, 0)]] cbuffer Frame
{
    float2 Viewport;
    float2 FramePad;
};

[[vk::binding(0, 1)]] Texture2D Tileset;
[[vk::binding(1, 1)]] SamplerState Samp;

[[vk::binding(0, 2)]] cbuffer TileParams
{
    // x: tileset columns, y: tileset rows, z: tile width in texels, w: tile height in texels.
    float4 Info;
};

[[vk::binding(1, 2)]] Texture2DArray<uint> Ids;
[[vk::binding(2, 2)]] Texture2DArray<float4> Colors;

struct QuadIn
{
    [[vk::location(0)]] float2 Origin : ORIGIN;
    [[vk::location(1)]] float2 AxisX : AXISX;
    [[vk::location(2)]] float2 AxisY : AXISY;
    [[vk::location(3)]] float4 Uv : UVRECT;
    [[vk::location(4)]] float4 Color : TINT;
    [[vk::location(5)]] uint Extra : EXTRA;
    uint VertexId : SV_VertexID;
};

struct QuadOut
{
    float4 Pos : SV_Position;
    [[vk::location(0)]] float2 Uv : TEXCOORD0;
    [[vk::location(1)]] float4 Color : TEXCOORD1;
    [[vk::location(2)]] nointerpolation uint Extra : TEXCOORD2;
};

float3 SrgbToLinear(float3 c)
{
    return lerp(c / 12.92, pow(max((c + 0.055) / 1.055, 0.0), 2.4), step(0.04045, c));
}

[shader("vertex")]
QuadOut VSMain(QuadIn i)
{
    uint k = (0x231210u >> (i.VertexId * 4u)) & 0xFu;
    float2 c = float2(k & 1u, k >> 1u);
    float2 p = i.Origin + i.AxisX * c.x + i.AxisY * c.y;

    QuadOut o;
    o.Pos = float4(p.x / Viewport.x * 2.0 - 1.0, 1.0 - p.y / Viewport.y * 2.0, 0.0, 1.0);
    o.Uv = i.Uv.xy + i.Uv.zw * c;
    float a = i.Color.a;
    o.Color = float4(SrgbToLinear(i.Color.rgb) * a, a);
    o.Extra = i.Extra;
    return o;
}

[shader("pixel")]
float4 PSIds(QuadOut i) : SV_Target
{
    int2 cell = clamp(int2(floor(i.Uv)), 0, 63);
    uint id = Ids.Load(int4(cell, (int)i.Extra, 0)).x;
    if (id == 0u)
    {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    uint n = id - 1u;
    uint cols = (uint)Info.x;
    float2 tile = float2(n % cols, n / cols);
    // Keep samples half a texel inside the cell so bilinear filtering never reads a neighbouring tile.
    float2 f = clamp(frac(i.Uv), 0.5 / Info.zw, 1.0 - 0.5 / Info.zw);
    float2 uv = (tile + f) / Info.xy;
    return Tileset.SampleLevel(Samp, uv, 0) * i.Color;
}

[shader("pixel")]
float4 PSFlat(QuadOut i) : SV_Target
{
    int2 cell = clamp(int2(floor(i.Uv)), 0, 63);
    return Colors.Load(int4(cell, (int)i.Extra, 0)) * i.Color;
}
