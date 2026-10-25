// SPDX-License-Identifier: MIT OR Apache-2.0
// probe: PSMain ps
// Texture2D.GatherCmp is not supported.
[[vk::binding(0, 0)]] Texture2D<float> ShadowMap;
[[vk::binding(1, 0)]] SamplerComparisonState ShadowSampler;

float4 PSMain(float2 uv : TEXCOORD0, float depth : TEXCOORD1) : SV_Target0
{
    return ShadowMap.GatherCmp(ShadowSampler, uv, depth);
}
