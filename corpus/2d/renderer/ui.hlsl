// SPDX-License-Identifier: MIT OR Apache-2.0
// Copyright (c) Recursive AI, LLC. Taken unchanged from a 2D game renderer by the same authors.

// UI primitives: rounded rectangles with per-corner radii, borders, gradients, shadows, images and glyphs, in one pixel shader.
//
// Each instance carries a rectangle in its own (local) coordinates and a 2x3 matrix to the screen. Edges are anti-aliased from the
// signed distance to the rounded box, so the shapes stay sharp under any transform. The output is premultiplied and the colours are
// handled as in Quad.hlsl (sRGB bytes in, linear blending).
//
// Clips are a table of rounded boxes (set 0, binding 1; four float4 each). An instance names the innermost clip, and each entry
// names its parent, so nested clips with rounded corners or rotation multiply together (up to four deep).
//
// Kinds: 0 fill, 1 border, 2 shadow, 3 image (rgba page), 4 glyph (coverage page).

[[vk::binding(0, 0)]] cbuffer Frame
{
    float2 Viewport;
    float2 FramePad;
};

[[vk::binding(1, 0)]] StructuredBuffer<float4> Clips;

[[vk::binding(0, 1)]] Texture2D Tex;
[[vk::binding(1, 1)]] SamplerState Samp;

struct UiIn
{
    [[vk::location(0)]] float4 Rect : RECT;
    [[vk::location(1)]] float4 M : MATRIX;
    [[vk::location(2)]] float2 T : TRANSLATE;
    [[vk::location(3)]] float4 Radii : RADII;
    [[vk::location(4)]] float4 Widths : WIDTHS;
    [[vk::location(5)]] float4 Uv : UVRECT;
    [[vk::location(6)]] float4 C0 : COLOR0;
    [[vk::location(7)]] float4 C1 : COLOR1;
    [[vk::location(8)]] float4 Grad : GRADIENT;
    [[vk::location(9)]] uint4 Flags : FLAGS;
    uint VertexId : SV_VertexID;
};

struct UiOut
{
    float4 Pos : SV_Position;
    [[vk::location(0)]] float2 Local : TEXCOORD0;
    [[vk::location(1)]] nointerpolation float4 Rect : TEXCOORD1;
    [[vk::location(2)]] nointerpolation float4 Radii : TEXCOORD2;
    [[vk::location(3)]] nointerpolation float4 Widths : TEXCOORD3;
    [[vk::location(4)]] nointerpolation float4 Uv : TEXCOORD4;
    [[vk::location(5)]] nointerpolation float4 C0 : TEXCOORD5;
    [[vk::location(6)]] nointerpolation float4 C1 : TEXCOORD6;
    [[vk::location(7)]] nointerpolation float4 Grad : TEXCOORD7;
    [[vk::location(8)]] nointerpolation uint3 Flags : TEXCOORD8;
};

float3 SrgbToLinear(float3 c)
{
    return lerp(c / 12.92, pow(max((c + 0.055) / 1.055, 0.0), 2.4), step(0.04045, c));
}

[shader("vertex")]
UiOut VSMain(UiIn i)
{
    uint k = (0x231210u >> (i.VertexId * 4u)) & 0xFu;
    float2 c = float2(k & 1u, k >> 1u);
    float pad = asfloat(i.Flags.w);
    float2 local = i.Rect.xy - pad + c * (i.Rect.zw + 2.0 * pad);
    // Row vectors: x' = x * m11 + y * m21 + tx, y' = x * m12 + y * m22 + ty.
    float2 p = float2(local.x * i.M.x + local.y * i.M.z + i.T.x, local.x * i.M.y + local.y * i.M.w + i.T.y);

    UiOut o;
    o.Pos = float4(p.x / Viewport.x * 2.0 - 1.0, 1.0 - p.y / Viewport.y * 2.0, 0.0, 1.0);
    o.Local = local;
    o.Rect = i.Rect;
    o.Radii = i.Radii;
    o.Widths = i.Widths;
    o.Uv = i.Uv;
    o.C0 = i.C0;
    o.C1 = i.C1;
    o.Grad = i.Grad;
    o.Flags = i.Flags.xyz;
    return o;
}

// Signed distance to a box with a different radius at each corner. Radii order: top-left, top-right, bottom-right, bottom-left.
float RoundedBox(float2 p, float2 center, float2 hext, float4 radii)
{
    float2 q = p - center;
    float r = q.x < 0.0 ? (q.y < 0.0 ? radii.x : radii.w) : (q.y < 0.0 ? radii.y : radii.z);
    r = min(r, min(hext.x, hext.y));
    float2 d = abs(q) - (hext - r);
    return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0) - r;
}

// Coverage of a pixel from the distance at its centre: one pixel of linear ramp.
float Cover(float d)
{
    float w = max(fwidth(d), 1e-5);
    return saturate(0.5 - d / w);
}

float4 Premul(float3 srgb, float a)
{
    return float4(SrgbToLinear(srgb) * a, a);
}

[shader("pixel")]
float4 PSMain(UiOut i) : SV_Target
{
    uint kind = i.Flags.x;
    uint gradKind = i.Flags.y;
    uint clipId = i.Flags.z;

    float2 hext = i.Rect.zw * 0.5;
    float2 center = i.Rect.xy + hext;
    float d = RoundedBox(i.Local, center, hext, i.Radii);

    // Colour: a solid colour or a gradient between the two colours, interpolated in linear light.
    float4 c0 = float4(SrgbToLinear(i.C0.rgb), i.C0.a);
    float4 c1 = float4(SrgbToLinear(i.C1.rgb), i.C1.a);
    float t = 0.0;
    if (gradKind == 1u)
    {
        float2 axis = i.Grad.zw - i.Grad.xy;
        t = saturate(dot(i.Local - i.Grad.xy, axis) / max(dot(axis, axis), 1e-5));
    }
    else if (gradKind == 2u)
    {
        t = saturate(length(i.Local - i.Grad.xy) / max(i.Grad.z, 1e-5));
    }

    float4 col = lerp(c0, c1, t);
    float4 outc;

    if (kind == 0u)
    {
        outc = float4(col.rgb * col.a, col.a) * Cover(d);
    }
    else if (kind == 1u)
    {
        // The inner edge is the box shrunk by the widths (left, top, right, bottom), with the radii shrunk to match.
        float2 inLo = i.Rect.xy + i.Widths.xy;
        float2 inHi = i.Rect.xy + i.Rect.zw - i.Widths.zw;
        float2 inHalf = max((inHi - inLo) * 0.5, 0.0);
        float4 inRadii = max(i.Radii - float4(max(i.Widths.x, i.Widths.y), max(i.Widths.z, i.Widths.y), max(i.Widths.z, i.Widths.w), max(i.Widths.x, i.Widths.w)), 0.0);
        float di = RoundedBox(i.Local, (inLo + inHi) * 0.5, inHalf, inRadii);
        outc = float4(col.rgb * col.a, col.a) * (Cover(d) * (1.0 - Cover(di)));
    }
    else if (kind == 2u)
    {
        // A Gaussian blur of a box has an S-shaped edge; a logistic curve with the same spread is close and cheap.
        // Grad.x is the standard deviation, Widths is the box that must stay clear (the element itself).
        float sigma = max(i.Grad.x, 1e-3);
        float a = 1.0 / (1.0 + exp(1.702 * d / sigma));
        float2 hHalf = i.Widths.zw * 0.5;
        float hole = Cover(RoundedBox(i.Local, i.Widths.xy + hHalf, hHalf, i.Radii));
        float4 s = float4(col.rgb * col.a, col.a);
        outc = s * (a * (1.0 - hole));
    }
    else if (kind == 3u)
    {
        float2 uv = i.Uv.xy + i.Uv.zw * saturate((i.Local - i.Rect.xy) / max(i.Rect.zw, 1e-5));
        float4 tx = Tex.Sample(Samp, uv);
        float4 tint = float4(col.rgb * col.a, col.a);
        outc = tx * tint * Cover(d);
    }
    else
    {
        float2 uv = i.Uv.xy + i.Uv.zw * saturate((i.Local - i.Rect.xy) / max(i.Rect.zw, 1e-5));
        float cov = Tex.Sample(Samp, uv).r;
        outc = float4(col.rgb * col.a, col.a) * cov;
    }

    // Clip chain: this instance's clip, then its parents.
    uint id = clipId;
    [loop]
    for (uint depth = 0u; depth < 4u && id != 0u; depth++)
    {
        uint b = (id - 1u) * 4u;
        float4 rect = Clips[b];
        float4 radii = Clips[b + 1u];
        float4 inv = Clips[b + 2u];
        float4 misc = Clips[b + 3u];
        // Pixel centre in framebuffer pixels, mapped back into the clip's own coordinates.
        float2 sp = i.Pos.xy;
        float2 lp = float2(sp.x * inv.x + sp.y * inv.z + misc.x, sp.x * inv.y + sp.y * inv.w + misc.y);
        float2 h = rect.zw * 0.5;
        outc *= Cover(RoundedBox(lp, rect.xy + h, h, radii));
        id = (uint)misc.z;
    }

    return outc;
}
