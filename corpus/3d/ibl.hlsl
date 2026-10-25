// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: image-based lighting with the split-sum approximation. Diffuse comes
// from an irradiance cube map, specular from a prefiltered cube map (one roughness per mip level)
// and a 2D scale/bias table indexed by N.V and roughness. A second entry pair draws the sky.

#include "brdf.hlsli"

[[vk::binding(0, 0)]] cbuffer Frame
{
    row_major float4x4 ViewProjection;
    row_major float4x4 InverseViewProjection; // clip to world, for the sky
    float3 CameraPosition;
    float Exposure;
    float3 BaseColor;
    float Metallic;
    float Roughness;
    float PrefilterMaxLod; // mip count of the prefiltered map minus one
    float SpecularIntensity;
    float DiffuseIntensity;
};

[[vk::binding(1, 0)]] TextureCube<float4> IrradianceMap;
[[vk::binding(2, 0)]] TextureCube<float4> PrefilteredMap;
[[vk::binding(3, 0)]] Texture2D<float4> BrdfLut; // r: scale, g: bias for F0
[[vk::binding(4, 0)]] SamplerState LinearClamp;

struct VertexInput
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
};

struct VertexOutput
{
    float4 Position : SV_Position;
    float3 WorldPosition : TEXCOORD0;
    float3 WorldNormal : TEXCOORD1;
};

[shader("vertex")]
VertexOutput VSMain(VertexInput input)
{
    VertexOutput output;
    output.WorldPosition = input.Position;
    output.WorldNormal = input.Normal;
    output.Position = mul(ViewProjection, float4(input.Position, 1.0));
    return output;
}

[shader("pixel")]
float4 PSMain(VertexOutput input) : SV_Target0
{
    float3 N = normalize(input.WorldNormal);
    float3 V = normalize(CameraPosition - input.WorldPosition);
    float NoV = saturate(dot(N, V));
    float3 R = reflect(-V, N);
    float roughness = max(Roughness, MIN_ROUGHNESS);

    float3 diffuseColor = BaseColor * (1.0 - Metallic);
    float3 f0 = lerp(0.04, BaseColor, Metallic);

    float3 irradiance = IrradianceMap.SampleLevel(LinearClamp, N, 0).rgb;
    float3 prefiltered = PrefilteredMap.SampleLevel(LinearClamp, R, roughness * PrefilterMaxLod).rgb;
    float2 brdf = BrdfLut.SampleLevel(LinearClamp, float2(NoV, roughness), 0).rg;

    float3 diffuse = irradiance * diffuseColor * DiffuseIntensity;
    float3 specular = prefiltered * (f0 * brdf.x + brdf.y) * SpecularIntensity;
    return float4((diffuse + specular) * Exposure, 1.0);
}

struct SkyOutput
{
    float4 Position : SV_Position;
    float2 Ndc : TEXCOORD0;
};

[shader("vertex")]
SkyOutput VSSky(uint vertexId : SV_VertexID)
{
    SkyOutput output;
    float2 uv = float2((vertexId << 1) & 2, vertexId & 2);
    output.Ndc = uv * 2.0 - 1.0;
    output.Position = float4(output.Ndc, 0.0, 1.0);
    return output;
}

[shader("pixel")]
float4 PSSky(SkyOutput input) : SV_Target0
{
    float4 world = mul(InverseViewProjection, float4(input.Ndc, 1.0, 1.0));
    float3 direction = normalize(world.xyz / world.w - CameraPosition);
    return float4(PrefilteredMap.SampleLevel(LinearClamp, direction, 0).rgb * Exposure, 1.0);
}
