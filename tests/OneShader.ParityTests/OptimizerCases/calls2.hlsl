// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: calls in awkward places (loop conditions, short-circuit operands, ternaries,
// loop steps), void functions with early returns, and struct results that carry arrays.

struct Window
{
    uint lo;
    uint hi;
    uint taps[3];
};

[[vk::binding(0, 0)]] StructuredBuffer<uint> Input;
[[vk::binding(1, 0)]] RWStructuredBuffer<uint> Output;

bool Small(uint v)
{
    if (v == 0)
    {
        return true;
    }

    for (uint i = 2; i < 6; i++)
    {
        if (v % i == 0)
        {
            return false;
        }
    }

    return v < 40;
}

uint Step(uint v)
{
    if (v & 1)
    {
        return v * 3 + 1;
    }

    return v >> 1;
}

void Clamp2(inout uint v, out uint hit)
{
    hit = 0;
    if (v < 5)
    {
        return;
    }

    hit = 1;
    v = 5 + (v & 3);
}

Window Build(uint seed)
{
    Window w;
    w.lo = seed;
    w.hi = seed + 10;
    w.taps[0] = 0;
    w.taps[1] = 0;
    w.taps[2] = 0;
    for (uint i = 0; i < 3; i++)
    {
        w.taps[i] = (seed >> i) & 15;
        if (w.taps[i] == 7)
        {
            w.lo = 77;
            return w;
        }
    }

    return w;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint x = Input[id.x];
    uint count = 0;
    uint v = x;
    while (!Small(v) && count < 20)
    {
        v = Step(v);
        count++;
    }

    uint r = v * 64 + count;
    uint guard = 0;
    for (uint k = x & 7; Small(k) || k < 4; k = Step(k) & 15)
    {
        r += k;
        guard++;
        if (r > 100000 || guard > 12)
        {
            break;
        }
    }

    bool both = (x & 1) && Small(x >> 1);
    r = r * 3 + (both ? Step(x) : Step(x + 1));

    uint c = x;
    uint hit;
    Clamp2(c, hit);
    r = r * 3 + c * 2 + hit;

    Window w = Build(x);
    r = r * 5 + w.lo + w.hi + w.taps[0] * 7 + w.taps[1] * 11 + w.taps[2] * 13;

    Output[id.x] = r;
}
