// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: multi-channel signed-distance-field glyphs (median of three)
// with an outline.

[[vk::binding(0, 0)]] cbuffer Frame
{
    float4x4 ViewProjection;
};

[[vk::binding(1, 0)]] cbuffer Style
{
    float4 TextColor;
    float4 OutlineColor;
    float2 AtlasSize;   // texels
    float PxRange;      // distance range of the atlas, in atlas texels
    float OutlineWidth; // in screen pixels
};

[[vk::binding(2, 0)]] Texture2D<float4> GlyphAtlas;
[[vk::binding(3, 0)]] SamplerState GlyphSampler;

struct VertexInput
{
    float2 Position : POSITION;
    float2 Uv : TEXCOORD0;
};

struct VertexOutput
{
    float4 Position : SV_Position;
    float2 Uv : TEXCOORD0;
};

[shader("vertex")]
VertexOutput VSMain(VertexInput input)
{
    VertexOutput output;
    output.Position = mul(ViewProjection, float4(input.Position, 0.0, 1.0));
    output.Uv = input.Uv;
    return output;
}

float Median(float3 v)
{
    return max(min(v.x, v.y), min(max(v.x, v.y), v.z));
}

[shader("pixel")]
float4 PSMain(VertexOutput input) : SV_Target0
{
    float3 msd = GlyphAtlas.Sample(GlyphSampler, input.Uv).rgb;
    float signedDistance = Median(msd) - 0.5;

    // Screen pixels per unit of signed distance: how far the atlas range reaches on screen.
    float2 unitRange = float2(PxRange, PxRange) / AtlasSize;
    float2 screenTexSize = 1.0 / max(fwidth(input.Uv), float2(1e-6, 1e-6));
    float screenPxRange = max(0.5 * dot(unitRange, screenTexSize), 1.0);
    float screenDistance = screenPxRange * signedDistance;

    float fill = saturate(screenDistance + 0.5);
    float outer = saturate(screenDistance + OutlineWidth + 0.5);

    float4 outline = float4(OutlineColor.rgb * OutlineColor.a, OutlineColor.a) * outer;
    float4 text = float4(TextColor.rgb * TextColor.a, TextColor.a) * fill;
    return text + outline * (1.0 - text.a);
}
