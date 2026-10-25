// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: control flow and value shapes the optimizer rewrites. Loop-carried
// variables (phis), branches on constants inside loops, partially written vectors, dynamically indexed
// local arrays, integer wrap-around, shifts, float constants and short-circuit operators.

static const bool kUseFast = false;
static const uint kRounds = 6;

[[vk::binding(0, 0)]] StructuredBuffer<uint> Input;
[[vk::binding(1, 0)]] RWStructuredBuffer<uint> Output;

uint Loops(uint x)
{
    uint sum = 0;
    uint prod = 1;
    for (uint i = 0; i < kRounds; i++)
    {
        if (kUseFast)
        {
            sum += i * 1000;
        }
        else
        {
            sum ^= (x >> i) & 7;
        }

        if ((x + i) % 3 == 0)
        {
            continue;
        }

        prod = prod * 3 + i;
        if (prod > 5000)
        {
            break;
        }
    }

    uint w = x & 15;
    while (w > 0)
    {
        sum += w;
        w = w >> 1;
    }

    return sum * 7 + prod;
}

uint Vectors(uint x)
{
    float4 v = float4(1.0, 2.0, 3.0, 4.0);
    v.y = (float)(x & 15);
    v.xz = float2(v.w, v.y + 0.5);
    v.w *= 2.0;
    float3 s = v.zyx;
    uint4 u = uint4(asuint(s.x), asuint(s.y), asuint(s.z), asuint(v.w));
    return u.x ^ (u.y * 3) ^ (u.z * 5) ^ u.w;
}

uint Arrays(uint x)
{
    uint table[8] = { 3, 1, 4, 1, 5, 9, 2, 6 };
    uint acc = 0;
    for (uint i = 0; i < 4; i++)
    {
        acc = acc * 11 + table[(x + i) & 7];
        table[i] = acc & 0xFF;
    }

    return acc + table[x & 7];
}

uint Constants(uint x)
{
    uint x2 = 33;
    uint big = 0xFFFFFFF0u;
    uint wrap = big + 0x20u;
    int neg = -2147483647 - 1;
    int one = 1;
    int shifted = (one << 31) >> 3;
    uint sh = 1u << (x2 & 31);
    float a = 0.1;
    float b = 0.2;
    float c = a + b;
    float z = -0.0 * 1.0;
    uint r = wrap ^ (uint)(neg + 3) ^ (uint)shifted ^ sh ^ asuint(c) ^ asuint(z) ^ asuint(a * 3.0 - b);
    r += (uint)(7.9 + 0.2) + (uint)(int)(-3.5);
    return r + x * 0 + (x | 0) + (x & 0xFFFFFFFFu) + (x ^ 0);
}

uint ShortCircuit(uint x)
{
    uint r = 0;
    if (x > 3 && (x & 1) == 0 || x == 1)
    {
        r += 1;
    }

    bool both = (x & 2) != 0 && (x & 4) != 0;
    r += both ? 10 : 20;
    r += (x > 100 || Input[(x + 1) & 255] > 50) ? 100 : 200;
    return r;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint x = Input[id.x];
    Output[id.x] = Loops(x);
    Output[id.x + 256] = Vectors(x) + Arrays(x);
    Output[id.x + 512] = Constants(x) ^ ShortCircuit(x);
}
