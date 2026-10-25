// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: cascaded shadow lookup. The cascade is picked by view depth and the
// shadow is a 3x3 percentage-closer filter over hardware depth comparisons.

#define CASCADE_COUNT 4

[[vk::binding(0, 0)]] cbuffer Frame
{
    row_major float4x4 ViewProjection;
    row_major float4x4 View;
    row_major float4x4 CascadeViewProjection[CASCADE_COUNT];
    float4 CascadeSplits; // far view depth of each cascade
    float3 LightDirection; // direction the light travels
    float ShadowTexelSize; // 1 / shadow map resolution
    float3 LightColor;
    float ShadowBias;
    float3 AmbientColor;
};

[[vk::binding(1, 0)]] Texture2DArray<float> ShadowMap;
[[vk::binding(2, 0)]] SamplerComparisonState ShadowSampler;

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

uint SelectCascade(float viewDepth)
{
    uint cascade = 0;
    [unroll]
    for (uint i = 0; i < CASCADE_COUNT - 1; ++i)
    {
        if (viewDepth > CascadeSplits[i])
        {
            cascade = i + 1;
        }
    }
    return cascade;
}

float ShadowFactor(float3 worldPosition, float NoL, uint cascade)
{
    float4 clip = mul(CascadeViewProjection[cascade], float4(worldPosition, 1.0));
    float3 ndc = clip.xyz / clip.w;
    float2 uv = ndc.xy * 0.5 + 0.5;
    // The slope-scaled bias keeps surfaces facing away from the light from shadowing themselves.
    float depth = ndc.z - ShadowBias * (1.0 + 2.0 * (1.0 - NoL));
    if (any(uv < 0.0) || any(uv > 1.0) || depth > 1.0)
    {
        return 1.0;
    }
    float sum = 0.0;
    [unroll]
    for (int y = -1; y <= 1; ++y)
    {
        [unroll]
        for (int x = -1; x <= 1; ++x)
        {
            float2 offset = float2(x, y) * ShadowTexelSize;
            sum += ShadowMap.SampleCmpLevelZero(ShadowSampler, float3(uv + offset, (float)cascade), depth);
        }
    }
    return sum / 9.0;
}

// Eight points on a golden-angle spiral inside the unit disc.
static const float2 SpiralDisc[8] =
{
    float2(0.250, 0.000), float2(-0.319, 0.292), float2(0.049, -0.557), float2(0.402, 0.525),
    float2(-0.738, -0.130), float2(0.700, -0.445), float2(-0.117, 0.893), float2(-0.447, -0.859)
};

// Same lookup with eight rotated spiral taps instead of the grid; the rotation comes from
// interleaved gradient noise of the pixel position, so neighbouring pixels use different angles.
float ShadowSpiral(float3 worldPosition, float NoL, uint cascade, float2 pixel)
{
    float4 clip = mul(CascadeViewProjection[cascade], float4(worldPosition, 1.0));
    float3 ndc = clip.xyz / clip.w;
    float2 uv = ndc.xy * 0.5 + 0.5;
    float depth = ndc.z - ShadowBias * (1.0 + 2.0 * (1.0 - NoL));
    if (any(uv < 0.0) || any(uv > 1.0) || depth > 1.0)
    {
        return 1.0;
    }
    float angle = 6.2831853 * frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
    float s = sin(angle);
    float c = cos(angle);
    float sum = 0.0;
    [unroll]
    for (int i = 0; i < 8; ++i)
    {
        float2 p = SpiralDisc[i];
        float2 offset = float2(p.x * c - p.y * s, p.x * s + p.y * c) * (2.0 * ShadowTexelSize);
        sum += ShadowMap.SampleCmpLevelZero(ShadowSampler, float3(uv + offset, (float)cascade), depth);
    }
    return sum / 8.0;
}

[shader("pixel")]
float4 PSSpiral(VertexOutput input) : SV_Target0
{
    float3 N = normalize(input.WorldNormal);
    float NoL = saturate(dot(N, -normalize(LightDirection)));
    uint cascade = SelectCascade(input.ViewDepth);
    float shadow = ShadowSpiral(input.WorldPosition, NoL, cascade, input.Position.xy);
    float3 albedo = 0.8;
    return float4(albedo * (AmbientColor + LightColor * NoL * shadow), 1.0);
}

[shader("pixel")]
float4 PSMain(VertexOutput input) : SV_Target0
{
    float3 N = normalize(input.WorldNormal);
    float NoL = saturate(dot(N, -normalize(LightDirection)));
    uint cascade = SelectCascade(input.ViewDepth);
    float shadow = ShadowFactor(input.WorldPosition, NoL, cascade);
    float3 albedo = 0.8;
    float3 color = albedo * (AmbientColor + LightColor * NoL * shadow);
    return float4(color, 1.0);
}
