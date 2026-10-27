// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: 16-bit integers (compile with 16-bit types on) in structured buffers and a constant
// buffer: wrapping arithmetic, shifts, bit operations, division, comparisons and conversions to and from 32-bit.

struct Cell
{
    int16_t a;
    uint16_t b;
    int16_t2 pair;
    uint16_t3 trio;
    int count;
};

[[vk::binding(0, 0)]] cbuffer Params
{
    int16_t Offset;
    uint16_t Mask;
    int16_t2 Step;
    uint16_t4 Limit;
    int Bias;
};

[[vk::binding(1, 0)]] StructuredBuffer<Cell> Cells;
[[vk::binding(2, 0)]] RWStructuredBuffer<Cell> Result;
[[vk::binding(3, 0)]] RWStructuredBuffer<int4> Wide;
[[vk::binding(4, 0)]] RWStructuredBuffer<uint16_t> Words;

[shader("compute")]
[numthreads(16, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    Cell c = Cells[id.x];

    // Wrapping arithmetic at 16 bits.
    int16_t sq = c.a * c.a;
    int16_t sum = c.a + Offset;
    uint16_t prod = c.b * Mask;
    uint16_t diff = c.b - Limit.x;
    int16_t2 stepped = c.pair + Step * (int16_t)3;
    uint16_t3 mixed = c.trio * Limit.xyz + Limit.w;

    // Shifts and bit operations; signed right shift keeps the sign.
    int16_t sar = c.a >> 3;
    uint16_t shr = c.b >> 5;
    uint16_t shl = c.b << 4;
    uint16_t bits = (c.b & Mask) | (shl ^ (uint16_t)0x0F0F);
    int16_t inv = ~c.a;

    // Division and remainder by non-zero divisors.
    int16_t dsor = (int16_t)(Step.x | 1);
    int16_t quot = c.a / dsor;
    int16_t rem = c.a % dsor;
    uint16_t uq = c.b / (uint16_t)(Mask | 1);

    // Comparisons, selection and clamping.
    int16_t pick = c.a < Offset ? c.a : Offset;
    int16_t clamped = clamp(c.a, (int16_t)-1000, (int16_t)1000);
    int16_t ab = abs(c.a);
    uint16_t hi = max(c.b, Limit.y);
    uint16_t lo = min(c.b, Limit.z);

    // Conversions: sign extension, zero extension, truncation, float.
    int widened = c.a;
    uint zext = c.b;
    int16_t trunc16 = (int16_t)(c.count + Bias);
    float asFloat = (float)c.a * 0.5f;
    int16_t fromFloat = (int16_t)(asFloat * 3.0f);

    Cell o;
    o.a = sq + sum + sar + quot;
    o.b = prod + diff + shr + bits + uq;
    o.pair = stepped + int16_t2(rem, inv);
    o.trio = mixed + uint16_t3(hi, lo, (uint16_t)zext);
    o.count = widened + trunc16;
    Result[id.x] = o;

    Wide[id.x] = int4(widened, (int)zext, (int)pick + clamped, (int)ab + (int)fromFloat);
    Words[id.x] = trunc16 + (uint16_t)asFloat;
}
