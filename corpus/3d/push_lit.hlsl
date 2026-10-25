// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: a lit draw whose per-draw values (model matrix, tint, palette index,
// light direction, uv scale, weights) come from push constants read by both the vertex and the pixel stage.
// Layout of the block (vector-relaxed std430): Model 0, Tint 64, PaletteIndex 80, LightDirection 84,
// UvScale 96, Weights 104, size 112.

struct PerDraw
{
    float4x4 Model;
    float4 Tint;
    uint PaletteIndex;
    float3 LightDirection; // direction the light travels
    float2 UvScale;
    float Weights[2];
};

[[vk::push_constant]] ConstantBuffer<PerDraw> Draw;

[[vk::binding(0, 0)]] cbuffer Frame
{
    row_major float4x4 ViewProjection;
};

[[vk::binding(1, 0)]] StructuredBuffer<float4> Palette;

struct VertexInput
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
    float2 Uv : TEXCOORD0;
};

struct VertexOutput
{
    float4 Position : SV_Position;
    float3 Normal : TEXCOORD0;
    float2 Uv : TEXCOORD1;
};

[shader("vertex")]
VertexOutput VSMain(VertexInput input)
{
    float4 world = mul(Draw.Model, float4(input.Position, 1.0));
    VertexOutput output;
    output.Position = mul(ViewProjection, world);
    // The model matrix is a rotation and uniform scale, so its upper 3x3 transforms normals correctly.
    output.Normal = mul((float3x3)Draw.Model, input.Normal);
    output.Uv = input.Uv * Draw.UvScale;
    return output;
}

[shader("pixel")]
float4 PSMain(VertexOutput input) : SV_Target0
{
    float3 N = normalize(input.Normal);
    float lambert = saturate(dot(N, -normalize(Draw.LightDirection)));
    float stripe = step(0.5, frac(input.Uv.y * 8.0));
    float3 albedo = Palette[Draw.PaletteIndex].rgb * Draw.Tint.rgb * lerp(Draw.Weights[0], Draw.Weights[1], stripe);
    return float4(albedo * (0.15 + lambert), Draw.Tint.a);
}
