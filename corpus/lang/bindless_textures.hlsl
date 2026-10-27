// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository.
// Bindless textures: unbounded Texture2D and SamplerState arrays indexed with a per-instance value that differs between
// primitives (NonUniformResourceIndex), in a pixel shader that samples with implicit derivatives.

[[vk::binding(0, 0)]] Texture2D<float4> Textures[] : register(t0, space1);
[[vk::binding(1, 0)]] SamplerState Filters[] : register(s0, space2);

struct VSOut
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
    nointerpolation uint slot : TEXCOORD1;
};

[shader("vertex")]
VSOut VSMain(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
{
    // Two triangles per cell of a 3x2 grid: corners 0,1,2 and 1,2,3 of the cell.
    uint corner = (vertexId % 3) + (vertexId / 3);
    float2 c = float2(corner & 1, corner >> 1);
    float2 cell = float2(instanceId % 3, instanceId / 3);
    VSOut o;
    o.position = float4((cell + c) / float2(3.0, 2.0) * 2.0 - 1.0, 0.0, 1.0);
    o.uv = c;
    // Neighbouring cells use different textures and filters, so the index is not uniform across a wave.
    o.slot = (instanceId * 3 + 1) % 5;
    return o;
}

[shader("pixel")]
float4 PSMain(VSOut i) : SV_Target
{
    uint slot = NonUniformResourceIndex(i.slot);
    float4 a = Textures[slot].Sample(Filters[NonUniformResourceIndex(i.slot & 1)], i.uv);
    float4 b = Textures[NonUniformResourceIndex((i.slot + 1) % 5)].Sample(Filters[0], i.uv * 0.5);
    return lerp(a, b, 0.25);
}
