// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: bloom downsample with a 13-tap filter (a centre box, four corner
// boxes and the outer ring, weighted 0.5 / 0.125 each). The first level can weight taps by 1/(1+luma)
// so one bright pixel cannot flicker the whole chain, and applies a brightness threshold.

[[vk::binding(0, 0)]] cbuffer Downsample
{
    float2 SourceTexelSize;
    float Threshold;  // luminance below which the first level contributes nothing
    uint UseKaris;    // 1 on the first level
};

[[vk::binding(1, 0)]] Texture2D<float4> Source;
[[vk::binding(2, 0)]] SamplerState LinearClamp;

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

float Luma(float3 c)
{
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}

float3 Tap(float2 uv, float2 offsetTexels)
{
    return Source.SampleLevel(LinearClamp, uv + offsetTexels * SourceTexelSize, 0).rgb;
}

float3 KarisAverage(float3 a, float3 b, float3 c, float3 d)
{
    float wa = 1.0 / (1.0 + Luma(a));
    float wb = 1.0 / (1.0 + Luma(b));
    float wc = 1.0 / (1.0 + Luma(c));
    float wd = 1.0 / (1.0 + Luma(d));
    return (a * wa + b * wb + c * wc + d * wd) / (wa + wb + wc + wd);
}

[shader("pixel")]
float4 PSMain(VertexOutput input) : SV_Target0
{
    float2 uv = input.Uv;
    float3 a = Tap(uv, float2(-2, 2));
    float3 b = Tap(uv, float2(0, 2));
    float3 c = Tap(uv, float2(2, 2));
    float3 d = Tap(uv, float2(-2, 0));
    float3 e = Tap(uv, float2(0, 0));
    float3 f = Tap(uv, float2(2, 0));
    float3 g = Tap(uv, float2(-2, -2));
    float3 h = Tap(uv, float2(0, -2));
    float3 i = Tap(uv, float2(2, -2));
    float3 j = Tap(uv, float2(-1, 1));
    float3 k = Tap(uv, float2(1, 1));
    float3 l = Tap(uv, float2(-1, -1));
    float3 m = Tap(uv, float2(1, -1));

    float3 result;
    if (UseKaris == 1)
    {
        result = KarisAverage(j, k, l, m) * 0.5
            + KarisAverage(a, b, d, e) * 0.125
            + KarisAverage(b, c, e, f) * 0.125
            + KarisAverage(d, e, g, h) * 0.125
            + KarisAverage(e, f, h, i) * 0.125;
        float knee = max(Luma(result) - Threshold, 0.0);
        result *= knee / max(Luma(result), 1e-4);
    }
    else
    {
        result = (j + k + l + m) * 0.125
            + (a + b + d + e) * 0.03125
            + (b + c + e + f) * 0.03125
            + (d + e + g + h) * 0.03125
            + (e + f + h + i) * 0.03125;
    }
    return float4(result, 1.0);
}
