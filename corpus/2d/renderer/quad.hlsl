// SPDX-License-Identifier: MIT OR Apache-2.0
// Copyright (c) Recursive AI, LLC. Taken unchanged from a 2D game renderer by the same authors.

// Instanced textured quads: sprites, group composites and post-processing passes.
//
// Colour handling: vertex colours arrive as sRGB-encoded bytes and are decoded to linear here. Textures are sampled from sRGB
// formats (decoded by the hardware) and render targets are sRGB formats (encoded by the hardware), so all blending is linear.
// Everything is premultiplied: the output is rgb * a, a.
//
// Resource slots shared by every 2D program:
//   set 0: binding 0 Frame constants, binding 1 clip table (used by the UI program only, declared everywhere so set 0 is identical)
//   set 1: binding 0 texture, binding 1 sampler
//   set 2: binding 0 material parameters (custom materials, blur, grade)

[[vk::binding(0, 0)]] cbuffer Frame
{
    float2 Viewport;
    float2 FramePad;
};

[[vk::binding(0, 1)]] Texture2D Tex;
[[vk::binding(1, 1)]] SamplerState Samp;

[[vk::binding(0, 2)]] cbuffer Params
{
    float4 P[8];
};

struct QuadIn
{
    [[vk::location(0)]] float2 Origin : ORIGIN;
    [[vk::location(1)]] float2 AxisX : AXISX;
    [[vk::location(2)]] float2 AxisY : AXISY;
    [[vk::location(3)]] float4 Uv : UVRECT;
    [[vk::location(4)]] float4 Color : TINT;
    uint VertexId : SV_VertexID;
};

struct QuadOut
{
    float4 Pos : SV_Position;
    [[vk::location(0)]] float2 Uv : TEXCOORD0;
    [[vk::location(1)]] float4 Color : TEXCOORD1;
};

float3 SrgbToLinear(float3 c)
{
    return lerp(c / 12.92, pow(max((c + 0.055) / 1.055, 0.0), 2.4), step(0.04045, c));
}

[shader("vertex")]
QuadOut VSMain(QuadIn i)
{
    // Two triangles per instance from the vertex index: corners 0,1,2 and 1,3,2 of a unit square.
    uint k = (0x231210u >> (i.VertexId * 4u)) & 0xFu;
    float2 c = float2(k & 1u, k >> 1u);
    float2 p = i.Origin + i.AxisX * c.x + i.AxisY * c.y;

    QuadOut o;
    // Clip space has +Y up, the framebuffer origin is the top-left corner.
    o.Pos = float4(p.x / Viewport.x * 2.0 - 1.0, 1.0 - p.y / Viewport.y * 2.0, 0.0, 1.0);
    o.Uv = i.Uv.xy + i.Uv.zw * c;
    float a = i.Color.a;
    o.Color = float4(SrgbToLinear(i.Color.rgb) * a, a);
    return o;
}

[shader("pixel")]
float4 PSSprite(QuadOut i) : SV_Target
{
    return Tex.Sample(Samp, i.Uv) * i.Color;
}

// Separable blur: nine taps from five linear samples. P[0].xy is the step between taps in uv units (direction / size * radius).
[shader("pixel")]
float4 PSBlur(QuadOut i) : SV_Target
{
    float2 s = P[0].xy;
    float4 sum = Tex.Sample(Samp, i.Uv) * 0.2270270270;
    sum += (Tex.Sample(Samp, i.Uv + s * 1.3846153846) + Tex.Sample(Samp, i.Uv - s * 1.3846153846)) * 0.3162162162;
    sum += (Tex.Sample(Samp, i.Uv + s * 3.2307692308) + Tex.Sample(Samp, i.Uv - s * 3.2307692308)) * 0.0702702703;
    return sum * i.Color;
}

// Colour grade. P[0] = (exposure in stops, contrast, saturation, vignette strength); P[1] = (tint rgb, tint amount).
// Contrast and saturation work on gamma-encoded values so 0.5 is the pivot a person expects.
[shader("pixel")]
float4 PSGrade(QuadOut i) : SV_Target
{
    float4 t = Tex.Sample(Samp, i.Uv);
    float a = t.a;
    float3 rgb = a > 0.0 ? t.rgb / a : float3(0.0, 0.0, 0.0);
    rgb *= exp2(P[0].x);
    float3 g = pow(max(rgb, 0.0), 1.0 / 2.2);
    g = (g - 0.5) * P[0].y + 0.5;
    float luma = dot(g, float3(0.2126, 0.7152, 0.0722));
    g = lerp(float3(luma, luma, luma), g, P[0].z);
    g = lerp(g, g * P[1].rgb, P[1].w);
    rgb = pow(max(g, 0.0), 2.2);
    float2 d = (i.Uv - 0.5) * 1.41421356;
    rgb *= max(1.0 - P[0].w * dot(d, d), 0.0);
    return float4(rgb * a, a) * i.Color.a;
}
