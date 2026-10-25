// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: bloom upsample. A 3x3 tent filter over the smaller level is blended
// into the larger level, so running it from the smallest level up accumulates the glow.

[[vk::binding(0, 0)]] cbuffer Upsample
{
    float2 LowerTexelSize; // texel size of the smaller level
    float FilterRadius;    // in texels of the smaller level
    float Blend;           // weight of the glow added to the larger level
};

[[vk::binding(1, 0)]] Texture2D<float4> Lower;   // smaller, already accumulated
[[vk::binding(2, 0)]] Texture2D<float4> Current; // the level being written
[[vk::binding(3, 0)]] SamplerState LinearClamp;

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

[shader("pixel")]
float4 PSMain(VertexOutput input) : SV_Target0
{
    float2 r = FilterRadius * LowerTexelSize;
    float2 uv = input.Uv;
    float3 glow = Lower.SampleLevel(LinearClamp, uv, 0).rgb * 4.0;
    glow += Lower.SampleLevel(LinearClamp, uv + float2(-r.x, 0), 0).rgb * 2.0;
    glow += Lower.SampleLevel(LinearClamp, uv + float2(r.x, 0), 0).rgb * 2.0;
    glow += Lower.SampleLevel(LinearClamp, uv + float2(0, -r.y), 0).rgb * 2.0;
    glow += Lower.SampleLevel(LinearClamp, uv + float2(0, r.y), 0).rgb * 2.0;
    glow += Lower.SampleLevel(LinearClamp, uv + float2(-r.x, -r.y), 0).rgb;
    glow += Lower.SampleLevel(LinearClamp, uv + float2(r.x, -r.y), 0).rgb;
    glow += Lower.SampleLevel(LinearClamp, uv + float2(-r.x, r.y), 0).rgb;
    glow += Lower.SampleLevel(LinearClamp, uv + float2(r.x, r.y), 0).rgb;
    glow *= 1.0 / 16.0;
    return float4(Current.SampleLevel(LinearClamp, uv, 0).rgb + glow * Blend, 1.0);
}
