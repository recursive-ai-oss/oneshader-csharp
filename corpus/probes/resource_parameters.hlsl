// SPDX-License-Identifier: MIT OR Apache-2.0
// probe: PSMain ps
// A texture and a sampler passed to a helper function; the candidate asks for [forceinline].
[[vk::binding(0, 0)]] Texture2D<float4> Source;
[[vk::binding(1, 0)]] SamplerState Linear;

float4 Fetch(Texture2D<float4> t, SamplerState s, float2 uv)
{
    return t.Sample(s, uv);
}

float4 PSMain(float2 uv : TEXCOORD0) : SV_Target0
{
    return Fetch(Source, Linear, uv) + Fetch(Source, Linear, uv * 2.0);
}
