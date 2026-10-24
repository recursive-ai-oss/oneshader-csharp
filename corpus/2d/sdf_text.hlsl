// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: single-channel signed-distance-field glyphs with smoothstep
// anti-aliasing and an outline.
//
// The atlas stores distance remapped so that 0.5 is the glyph edge, larger inside. The
// smoothing width follows the screen-space derivative of the distance, so it stays about one
// pixel wide at any text size.

[[vk::binding(0, 0)]] cbuffer Frame
{
    float4x4 ViewProjection;
};

[[vk::binding(1, 0)]] cbuffer Style
{
    float4 TextColor;
    float4 OutlineColor;
    float OutlineWidth; // in distance units: 0 disables the outline, 0.2 is thick
    float Softness;     // extra smoothing, 1 = one derivative step
    float2 StylePad;
};

[[vk::binding(2, 0)]] Texture2D<float> GlyphAtlas;
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

[shader("pixel")]
float4 PSMain(VertexOutput input) : SV_Target0
{
    float distance = GlyphAtlas.Sample(GlyphSampler, input.Uv);
    float width = max(fwidth(distance) * Softness, 1e-4);

    float fill = smoothstep(0.5 - width, 0.5 + width, distance);
    float outer = smoothstep(0.5 - OutlineWidth - width, 0.5 - OutlineWidth + width, distance);

    // Outline colour under the fill colour, both premultiplied.
    float4 outline = float4(OutlineColor.rgb * OutlineColor.a, OutlineColor.a) * outer;
    float4 text = float4(TextColor.rgb * TextColor.a, TextColor.a) * fill;
    return text + outline * (1.0 - text.a);
}
