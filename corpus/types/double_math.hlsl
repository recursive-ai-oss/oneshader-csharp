// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: 64-bit floating point in a constant buffer and structured buffers: arithmetic,
// comparisons, rounding, conversions to and from 32-bit types, asdouble / asuint splitting and fma.

struct Sample
{
    double x;
    double2 xy;
    float w;
    double3 xyz;
};

[[vk::binding(0, 0)]] cbuffer Params
{
    float Gain;
    double Scale;
    double2 Pair;
    uint Seed;
    double3 Axis;
};

[[vk::binding(1, 0)]] StructuredBuffer<Sample> Samples;
[[vk::binding(2, 0)]] RWStructuredBuffer<Sample> Result;
[[vk::binding(3, 0)]] RWStructuredBuffer<uint4> Bits;
[[vk::binding(4, 0)]] RWStructuredBuffer<double> Sums;

[shader("compute")]
[numthreads(16, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    Sample s = Samples[id.x];
    float sw = isnan(s.w) ? 0.0f : s.w;

    // Arithmetic, including values a float cannot hold.
    double a = s.x * Scale + Pair.x;
    double b = s.xy.x / (abs(s.xy.y) + 1.0) - Pair.y;
    double2 v = s.xy * Pair + a;
    double3 w = s.xyz * Axis - b;
    double tiny = 1.0e-300 * (a + 2.0);
    double big = 1.0e300 * (b + 2.0);

    // Rounding and selection.
    double fl = floor(a * 8.0);
    double ce = ceil(b * 8.0);
    double fr = frac(a * 4.0);
    double rn = round(w.x * 16.0);
    double mx = max(a, b);
    double mn = min(a, b);
    double cl = clamp(a, -0.5, 0.5);
    double fused = fma(a, b, w.y);
    double sq = sqrt(abs(a) + 1.0);

    // Conversions.
    float narrowed = (float)a;
    double widened = (double)sw * 1.000000001;
    int asInt = (int)(fl * 0.25);
    uint asUint = (uint)abs(ce);
    double fromInt = (double)(int)(id.x) - 3.5;
    double fromUint = (double)(Seed + id.x) * 0.125;

    // Splitting and joining the 64 bits.
    uint lo, hi;
    asuint(a, lo, hi);
    double joined = asdouble(lo ^ 1u, hi);
    double whole = asdouble(Seed + id.x, 0x3FF00000u);

    Sample o;
    o.x = fused + sq + (a > b ? mx : mn) + cl;
    o.xy = double2(v.x + fl, v.y - ce);
    o.w = narrowed * Gain + (float)(tiny + big > 1.0 ? 1.0 : 0.0);
    o.xyz = double3(w.xy + fr, w.z + rn + widened + fromInt);
    Result[id.x] = o;

    Bits[id.x] = uint4(lo, hi, (uint)asInt + asUint, lo ^ hi);
    Sums[id.x] = joined + whole + fromUint;
}
