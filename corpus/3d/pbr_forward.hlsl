// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: forward-lit glTF metal/rough material with base colour, normal and
// occlusion-roughness-metallic maps, point, spot and directional lights, Cook-Torrance GGX.

#include "brdf.hlsli"

#define MAX_LIGHTS 8

struct Light
{
    float3 Position;
    float Range;
    float3 Color;
    float Intensity;
    float3 Direction; // direction the light travels (spot and directional)
    uint Type;        // 0 point, 1 spot, 2 directional
    float SpotScale;  // 1 / max(cos(inner) - cos(outer), 0.001)
    float SpotOffset; // -cos(outer) * SpotScale
};

[[vk::binding(0, 0)]] cbuffer Frame
{
    row_major float4x4 ViewProjection;
    float3 CameraPosition;
    float Exposure;
    float3 AmbientColor;
    uint LightCount;
};

[[vk::binding(1, 0)]] cbuffer Object
{
    float4x4 World;                 // column_major, the HLSL default
    row_major float3x3 NormalMatrix; // inverse transpose of the upper 3x3 of World
};

[[vk::binding(2, 0)]] cbuffer Material
{
    float4 BaseColorFactor;
    float3 EmissiveFactor;
    float MetallicFactor;
    float RoughnessFactor;
    float NormalScale;
    float OcclusionStrength;
    float AlphaCutoff;
};

[[vk::binding(3, 0)]] cbuffer Lights
{
    Light Lights[MAX_LIGHTS];
};

[[vk::binding(0, 1)]] Texture2D<float4> BaseColorMap;
[[vk::binding(1, 1)]] Texture2D<float4> NormalMap;
[[vk::binding(2, 1)]] Texture2D<float4> OrmMap; // r occlusion, g roughness, b metallic
[[vk::binding(3, 1)]] SamplerState MaterialSampler;

struct VertexInput
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
    float2 Uv : TEXCOORD0;
    float4 Tangent : TANGENT; // w is the bitangent sign
};

struct VertexOutput
{
    float4 Position : SV_Position;
    float3 WorldPosition : TEXCOORD0;
    float3 WorldNormal : TEXCOORD1;
    float4 WorldTangent : TEXCOORD2;
    float2 Uv : TEXCOORD3;
};

[shader("vertex")]
VertexOutput VSMain(VertexInput input)
{
    VertexOutput output;
    float4 world = mul(World, float4(input.Position, 1.0));
    output.WorldPosition = world.xyz;
    output.WorldNormal = mul(NormalMatrix, input.Normal);
    output.WorldTangent = float4(mul((float3x3)World, input.Tangent.xyz), input.Tangent.w);
    output.Uv = input.Uv;
    output.Position = mul(ViewProjection, world);
    return output;
}

[shader("pixel")]
float4 PSMain(VertexOutput input, bool isFrontFace : SV_IsFrontFace) : SV_Target0
{
    float4 baseColor = BaseColorMap.Sample(MaterialSampler, input.Uv) * BaseColorFactor;
    float3 orm = OrmMap.Sample(MaterialSampler, input.Uv).rgb;
    float3 tangentNormal = NormalMap.Sample(MaterialSampler, input.Uv).xyz * 2.0 - 1.0;
    if (baseColor.a < AlphaCutoff)
    {
        discard;
    }

    float3 N0 = normalize(input.WorldNormal);
    if (!isFrontFace)
    {
        N0 = -N0; // seen from behind (two-sided materials)
    }
    float3 T = normalize(input.WorldTangent.xyz - N0 * dot(N0, input.WorldTangent.xyz));
    float3 B = cross(N0, T) * input.WorldTangent.w;
    tangentNormal.xy *= NormalScale;
    float3 N = normalize(mul(tangentNormal, float3x3(T, B, N0)));

    float occlusion = 1.0 + OcclusionStrength * (orm.r - 1.0);
    float roughness = max(orm.g * RoughnessFactor, MIN_ROUGHNESS);
    float metallic = saturate(orm.b * MetallicFactor);
    float3 diffuseColor = baseColor.rgb * (1.0 - metallic);
    float3 f0 = lerp(0.04, baseColor.rgb, metallic);
    float3 V = normalize(CameraPosition - input.WorldPosition);

    float3 color = AmbientColor * diffuseColor * occlusion;
    uint count = min(LightCount, (uint)MAX_LIGHTS);
    [loop]
    for (uint i = 0; i < count; ++i)
    {
        Light light = Lights[i];
        float3 L;
        float attenuation;
        if (light.Type == 2)
        {
            L = -normalize(light.Direction);
            attenuation = 1.0;
        }
        else
        {
            float3 toLight = light.Position - input.WorldPosition;
            float distance = length(toLight);
            L = toLight / max(distance, 1e-4);
            attenuation = RangeAttenuation(distance, light.Range);
            if (light.Type == 1)
            {
                float angle = saturate(dot(normalize(light.Direction), -L) * light.SpotScale + light.SpotOffset);
                attenuation *= angle * angle;
            }
        }
        color += EvaluateLight(N, V, L, light.Color * (light.Intensity * attenuation), diffuseColor, f0, roughness);
    }
    color += EmissiveFactor;
    return float4(color * Exposure, baseColor.a);
}
