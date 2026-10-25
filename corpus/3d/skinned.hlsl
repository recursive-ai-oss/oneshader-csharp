// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: linear-blend vertex skinning. Four joint indices and weights per
// vertex come from a second vertex buffer; joint matrices live in a structured buffer.

[[vk::binding(0, 0)]] cbuffer Frame
{
    row_major float4x4 ViewProjection;
    float3 LightDirection; // direction the light travels
    float Ambient;
};

[[vk::binding(1, 0)]] StructuredBuffer<float4x4> JointMatrices; // already includes the inverse bind pose

struct VertexInput
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
    float2 Uv : TEXCOORD0;
    uint4 Joints : BLENDINDICES;
    float4 Weights : BLENDWEIGHT;
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
    float4x4 skin = JointMatrices[input.Joints.x] * input.Weights.x
        + JointMatrices[input.Joints.y] * input.Weights.y
        + JointMatrices[input.Joints.z] * input.Weights.z
        + JointMatrices[input.Joints.w] * input.Weights.w;
    float4 world = mul(skin, float4(input.Position, 1.0));
    VertexOutput output;
    output.Position = mul(ViewProjection, world);
    // Joint matrices are rigid here, so the upper 3x3 transforms normals correctly.
    output.Normal = mul((float3x3)skin, input.Normal);
    output.Uv = input.Uv;
    return output;
}

[shader("pixel")]
float4 PSMain(VertexOutput input) : SV_Target0
{
    float3 N = normalize(input.Normal);
    float lambert = saturate(dot(N, -normalize(LightDirection)));
    float stripe = step(0.5, frac(input.Uv.y * 8.0));
    float3 albedo = lerp(float3(0.85, 0.35, 0.25), float3(0.95, 0.85, 0.6), stripe);
    return float4(albedo * (Ambient + lambert), 1.0);
}
