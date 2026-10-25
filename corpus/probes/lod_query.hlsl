// SPDX-License-Identifier: MIT OR Apache-2.0
// probe: PSMain ps
// Texture2D.CalculateLevelOfDetail: the candidate emits an OpLoad through a non-logical pointer.
[[vk::binding(0, 0)]] Texture2D<float4> Source;
[[vk::binding(1, 0)]] SamplerState Linear;

float4 PSMain(float2 uv : TEXCOORD0) : SV_Target0
{
    return Source.CalculateLevelOfDetail(Linear, uv);
}
