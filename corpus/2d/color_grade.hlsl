// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: exposure, tone mapping, colour matrix, gamma and vignette in one
// fullscreen pass.

#ifndef GRADE_EXPOSURE_SCALE
#define GRADE_EXPOSURE_SCALE 1.0
#endif

[[vk::binding(0, 0)]] cbuffer Grade
{
    float4x4 ColorMatrix; // applied to linear colour after tone mapping
    float Exposure;       // in stops
    float VignetteStrength;
    float VignetteRadius; // in uv units from the centre
    float Gamma;
};

[[vk::binding(1, 0)]] Texture2D<float4> Scene;
[[vk::binding(2, 0)]] SamplerState SceneSampler;

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

// Narkowicz's rational fit of a filmic curve.
float3 ToneMap(float3 x)
{
    return saturate((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14));
}

[shader("pixel")]
float4 PSMain(VertexOutput input) : SV_Target0
{
    float4 scene = Scene.Sample(SceneSampler, input.Uv);
    float3 color = scene.rgb * exp2(Exposure * GRADE_EXPOSURE_SCALE);
    color = ToneMap(color);
    color = saturate(mul(ColorMatrix, float4(color, 1.0)).rgb);
    color = pow(color, 1.0 / Gamma);

    float2 fromCentre = input.Uv - 0.5;
    float vignette = 1.0 - VignetteStrength * smoothstep(VignetteRadius, VignetteRadius + 0.4, length(fromCentre));
    return float4(color * vignette, scene.a);
}
