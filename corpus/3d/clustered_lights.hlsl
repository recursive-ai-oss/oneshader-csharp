// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: clustered forward shading. The view frustum is cut into a grid of
// screen tiles by exponential depth slices; each cluster owns a range in a shared light-index list.

#include "brdf.hlsli"

struct PointLight
{
    float3 Position;
    float Range;
    float3 Color;
    float Intensity;
};

[[vk::binding(0, 0)]] cbuffer Frame
{
    row_major float4x4 ViewProjection;
    row_major float4x4 View;
    float3 CameraPosition;
    float Exposure;
    uint3 GridSize;      // clusters in x, y and depth slices
    uint TileSize;       // pixels per cluster edge
    float SliceScale;    // GridSize.z / log2(far / near)
    float SliceBias;     // -log2(near) * SliceScale
    float Roughness;
    float Metallic;
    float3 BaseColor;
    float AmbientLevel;
};

[[vk::binding(1, 0)]] StructuredBuffer<PointLight> Lights;
[[vk::binding(2, 0)]] StructuredBuffer<uint2> ClusterGrid; // x: first index in LightIndices, y: light count
[[vk::binding(3, 0)]] StructuredBuffer<uint> LightIndices;

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
    float ViewDepth : TEXCOORD2;
};

[shader("vertex")]
VertexOutput VSMain(VertexInput input)
{
    VertexOutput output;
    output.WorldPosition = input.Position;
    output.WorldNormal = input.Normal;
    output.ViewDepth = -mul(View, float4(input.Position, 1.0)).z;
    output.Position = mul(ViewProjection, float4(input.Position, 1.0));
    return output;
}

uint ClusterIndex(float2 pixel, float viewDepth)
{
    uint2 tile = min((uint2)pixel / TileSize, GridSize.xy - 1);
    float slice = floor(log2(max(viewDepth, 1e-4)) * SliceScale + SliceBias);
    uint z = (uint)clamp(slice, 0.0, (float)(GridSize.z - 1));
    return tile.x + GridSize.x * (tile.y + GridSize.y * z);
}

[shader("pixel")]
float4 PSMain(VertexOutput input) : SV_Target0
{
    float3 N = normalize(input.WorldNormal);
    float3 V = normalize(CameraPosition - input.WorldPosition);
    float3 diffuseColor = BaseColor * (1.0 - Metallic);
    float3 f0 = lerp(0.04, BaseColor, Metallic);
    float roughness = max(Roughness, MIN_ROUGHNESS);

    uint2 range = ClusterGrid[ClusterIndex(input.Position.xy, input.ViewDepth)];
    float3 color = AmbientLevel * diffuseColor;
    for (uint i = 0; i < range.y; ++i)
    {
        PointLight light = Lights[LightIndices[range.x + i]];
        float3 toLight = light.Position - input.WorldPosition;
        float distance = length(toLight);
        float3 L = toLight / max(distance, 1e-4);
        float attenuation = RangeAttenuation(distance, light.Range);
        color += EvaluateLight(N, V, L, light.Color * (light.Intensity * attenuation), diffuseColor, f0, roughness);
    }
    return float4(color * Exposure, 1.0);
}
