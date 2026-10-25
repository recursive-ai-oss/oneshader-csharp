// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: final composite. Adds bloom to the HDR scene, applies exposure, then
// an ACES-style filmic curve (input and output colour matrices around a rational fit of the
// reference rendering transform) or an extended Reinhard curve, then the sRGB transfer function.

[[vk::binding(0, 0)]] cbuffer Tonemap
{
    float Exposure;     // linear multiplier
    float BloomStrength;
    float WhitePoint;   // extended Reinhard: smallest luminance mapped to white
    uint Mode;          // 0 ACES-style, 1 extended Reinhard, 2 none (clamp)
};

[[vk::binding(1, 0)]] Texture2D<float4> Scene;
[[vk::binding(2, 0)]] Texture2D<float4> Bloom;
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

static const float3x3 AcesInput =
{
    0.59719, 0.35458, 0.04823,
    0.07600, 0.90834, 0.01566,
    0.02840, 0.13383, 0.83777
};

static const float3x3 AcesOutput =
{
     1.60475, -0.53108, -0.07367,
    -0.10208,  1.10813, -0.00605,
    -0.00327, -0.07276,  1.07602
};

float3 RrtAndOdtFit(float3 v)
{
    float3 a = v * (v + 0.0245786) - 0.000090537;
    float3 b = v * (0.983729 * v + 0.4329510) + 0.238081;
    return a / b;
}

float3 AcesFilmic(float3 color)
{
    color = mul(AcesInput, color);
    color = RrtAndOdtFit(color);
    return saturate(mul(AcesOutput, color));
}

float3 ReinhardExtended(float3 color)
{
    float luma = dot(color, float3(0.2126, 0.7152, 0.0722));
    float mapped = luma * (1.0 + luma / (WhitePoint * WhitePoint)) / (1.0 + luma);
    return saturate(color * (mapped / max(luma, 1e-5)));
}

float3 LinearToSrgb(float3 c)
{
    float3 low = c * 12.92;
    float3 high = 1.055 * pow(c, 1.0 / 2.4) - 0.055;
    return lerp(low, high, c > 0.0031308);
}

[shader("pixel")]
float4 PSMain(VertexOutput input) : SV_Target0
{
    float3 hdr = Scene.Sample(LinearClamp, input.Uv).rgb + Bloom.Sample(LinearClamp, input.Uv).rgb * BloomStrength;
    hdr *= Exposure;
    float3 mapped;
    if (Mode == 0)
    {
        mapped = AcesFilmic(hdr);
    }
    else if (Mode == 1)
    {
        mapped = ReinhardExtended(hdr);
    }
    else
    {
        mapped = saturate(hdr);
    }
    return float4(LinearToSrgb(mapped), 1.0);
}
