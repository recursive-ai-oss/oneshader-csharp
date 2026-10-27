// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: min-precision and half types without 16-bit types enabled. They are 32-bit in
// buffers and arithmetic (a min16int does not wrap at 16 bits); min16* values are marked as relaxed precision.

struct Mixed
{
    min16float a;
    half b;
    min16int c;
    min16uint d;
    min10float e;
    min16float3 v;
    half2 h;
};

[[vk::binding(0, 0)]] cbuffer Params
{
    min16float Gain;
    half Bias;
    min16int Count;
    min16float2 Pair;
};

[[vk::binding(1, 0)]] StructuredBuffer<Mixed> Input;
[[vk::binding(2, 0)]] RWStructuredBuffer<Mixed> Output;
[[vk::binding(3, 0)]] RWStructuredBuffer<float4> Plain;

[shader("compute")]
[numthreads(16, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    Mixed s = Input[id.x];

    min16float x = s.a * Gain + s.v.x;
    min16int i = s.c * Count + 40000;          // 32-bit arithmetic: no wrap at 16 bits
    min16uint u = s.d * 3 + 70000;
    min10float t = s.e + 1;
    half hb = s.b * Bias;
    min16float3 v = s.v * Pair.x + Pair.y;
    half2 h = s.h + Pair;
    float widened = x * 2.0f + hb;               // min-precision mixed with float is float

    Mixed o;
    o.a = x;
    o.b = hb;
    o.c = i;
    o.d = u;
    o.e = t;
    o.v = v;
    o.h = h;
    Output[id.x] = o;

    Plain[id.x] = float4(widened, (float)i, (float)u, dot(v, v) + t);
}
