// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: screen-space ambient occlusion. A hemisphere kernel, rotated per
// pixel by a tiled noise texture, is oriented along the view-space normal and tested against the
// linear depth buffer.

#define KERNEL_SIZE 16

[[vk::binding(0, 0)]] cbuffer Ssao
{
    float4x4 Projection;   // view to clip
    float2 TanHalfFov;     // tan(fovY / 2) * aspect, tan(fovY / 2)
    float2 NoiseScale;     // target size / noise texture size
    float Radius;          // view-space units
    float Bias;
    float Intensity;
    float Pad;
    float4 Kernel[KERNEL_SIZE]; // xyz in the +z hemisphere
};

[[vk::binding(1, 0)]] Texture2D<float> LinearDepth;   // positive view-space distance along -z
[[vk::binding(2, 0)]] Texture2D<float4> ViewNormals;  // xyz in view space
[[vk::binding(3, 0)]] Texture2D<float4> Noise;        // xy random in [0,1], tiled
[[vk::binding(4, 0)]] SamplerState PointClamp;
[[vk::binding(5, 0)]] SamplerState PointRepeat;

struct VertexOutput
{
    float4 Position : SV_Position;
    float2 Uv : TEXCOORD0;
};

[shader("vertex")]
VertexOutput VSMain(uint vertexId : SV_VertexID)
{
    VertexOutput output;
    output.Uv = float2((vertexId << 1) & 2, vertexId & 2);
    output.Position = float4(output.Uv * 2.0 - 1.0, 0.0, 1.0);
    return output;
}

float3 ViewPosition(float2 uv, float depth)
{
    float2 ndc = uv * 2.0 - 1.0;
    return float3(ndc.x * TanHalfFov.x * depth, -ndc.y * TanHalfFov.y * depth, -depth);
}

[shader("pixel")]
float PSMain(VertexOutput input) : SV_Target0
{
    float depth = LinearDepth.SampleLevel(PointClamp, input.Uv, 0);
    if (depth <= 0.0)
    {
        return 1.0; // background
    }
    float3 origin = ViewPosition(input.Uv, depth);
    float3 normal = normalize(ViewNormals.SampleLevel(PointClamp, input.Uv, 0).xyz);
    float2 random = Noise.SampleLevel(PointRepeat, input.Uv * NoiseScale, 0).xy * 2.0 - 1.0;
    float3 randomVec = float3(random, 0.0);
    float3 tangent = normalize(randomVec - normal * dot(randomVec, normal));
    float3 bitangent = cross(normal, tangent);
    float3x3 tbn = float3x3(tangent, bitangent, normal);

    float occlusion = 0.0;
    [loop]
    for (int i = 0; i < KERNEL_SIZE; ++i)
    {
        float3 samplePos = origin + mul(Kernel[i].xyz, tbn) * Radius;
        float4 clip = mul(Projection, float4(samplePos, 1.0));
        float2 sampleUv = (clip.xy / clip.w) * 0.5 + 0.5;
        float sceneDepth = LinearDepth.SampleLevel(PointClamp, sampleUv, 0);
        float rangeCheck = smoothstep(0.0, 1.0, Radius / max(abs(depth - sceneDepth), 1e-4));
        occlusion += (sceneDepth > 0.0 && sceneDepth < -samplePos.z - Bias ? 1.0 : 0.0) * rangeCheck;
    }
    return saturate(1.0 - Intensity * occlusion / KERNEL_SIZE);
}
