// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: 16-bit floating point (compile with 16-bit types on): half scalars, vectors and a
// matrix in a constant buffer and in structured buffers, arithmetic, comparisons, intrinsics and conversions.

struct Body
{
    half mass;
    half3 velocity;
    float id;
    half2 uv;
    half2x2 spin;
};

[[vk::binding(0, 0)]] cbuffer Params
{
    half Scale;
    half3 Bias;
    float Gain;
    half2 Range;
    half4 Tint;
    half2x2 Mix;
};

[[vk::binding(1, 0)]] StructuredBuffer<Body> Bodies;
[[vk::binding(2, 0)]] RWStructuredBuffer<Body> Moved;
[[vk::binding(3, 0)]] RWStructuredBuffer<half4> Packed;
[[vk::binding(4, 0)]] RWStructuredBuffer<float4> Widened;

[shader("compute")]
[numthreads(16, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    Body b = Bodies[id.x];

    // Arithmetic in half precision.
    half m = b.mass * Scale + Range.x;
    half3 v = b.velocity * Bias + m;
    v = v - half3(Tint.xyz) * (half)0.25;
    half len2 = dot(v, v);
    half k = len2 > Range.y ? Range.y : len2;
    half3 w = clamp(v, (half)-1.0, (half)1.0) + saturate(v) * (half)0.5;
    half2 uv = lerp(b.uv, Range, (half)0.5);
    half2 spun = mul(b.spin, uv) + mul(Mix, b.uv);

    // Intrinsics with exact results on every device.
    half fl = floor(m * (half)4.0);
    half mx = max(abs(v.x), abs(v.y));
    half mn = min(v.z, w.z);

    // Conversions.
    float f = (float)m * Gain + b.id;
    half back = (half)f;
    int toInt = (int)(m * (half)8.0);
    half fromInt = (half)(int)(id.x + 3u);
    uint toUint = (uint)abs(w.x * (half)16.0);
    half fromUint = (half)(id.x & 0xFFu);

    Body o;
    o.mass = m + fl;
    o.velocity = w;
    o.id = f;
    o.uv = spun;
    o.spin = b.spin * Scale + Mix;
    Moved[id.x] = o;

    Packed[id.x] = half4(k, mx, mn, back + fromInt + fromUint);
    Widened[id.x] = float4(float(toInt), float(toUint), (float)len2, (float)w.y);
}
