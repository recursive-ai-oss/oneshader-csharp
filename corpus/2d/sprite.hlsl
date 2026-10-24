// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: batched, textured, tinted sprites.
//
// One vertex buffer holds every sprite of the batch (position, uv, packed colour); the
// vertices are already in pixel space and a single view-projection matrix maps them to clip
// space. The pixel shader multiplies the atlas texel by the tint and outputs premultiplied alpha.

[[vk::binding(0, 0)]] cbuffer Frame
{
    float4x4 ViewProjection;
};

[[vk::binding(1, 0)]] Texture2D<float4> Atlas;
[[vk::binding(2, 0)]] SamplerState AtlasSampler;

struct VertexInput
{
    float2 Position : POSITION;
    float2 Uv : TEXCOORD0;
    float4 Tint : COLOR0; // unorm8x4 in the vertex buffer
};

struct VertexOutput
{
    float4 Position : SV_Position;
    float2 Uv : TEXCOORD0;
    float4 Tint : COLOR0;
};

[shader("vertex")]
VertexOutput VSMain(VertexInput input)
{
    VertexOutput output;
    output.Position = mul(ViewProjection, float4(input.Position, 0.0, 1.0));
    output.Uv = input.Uv;
    output.Tint = input.Tint;
    return output;
}

[shader("pixel")]
float4 PSMain(VertexOutput input) : SV_Target0
{
    float4 color = Atlas.Sample(AtlasSampler, input.Uv) * input.Tint;
    return float4(color.rgb * color.a, color.a);
}
