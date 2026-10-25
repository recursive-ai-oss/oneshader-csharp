// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: depth-only shadow pass. One instanced mesh drawn from the light's
// point of view; there is no pixel stage, the depth buffer is the result.

[[vk::binding(0, 0)]] cbuffer ShadowPass
{
    row_major float4x4 LightViewProjection;
    float DepthScale; // multiplies clip-space z; 1 for an ordinary pass
};

// One world matrix per instance, translation in the last column.
[[vk::binding(1, 0)]] StructuredBuffer<float4x4> InstanceWorld;

struct VertexInput
{
    float3 Position : POSITION;
    uint InstanceId : SV_InstanceID;
};

[shader("vertex")]
float4 VSMain(VertexInput input) : SV_Position
{
    float4 world = mul(InstanceWorld[input.InstanceId], float4(input.Position, 1.0));
    float4 clip = mul(LightViewProjection, world);
    clip.z *= DepthScale;
    return clip;
}
