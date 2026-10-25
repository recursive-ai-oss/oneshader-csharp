// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: calls whose bodies return early, from loops, from nested loops and from
// switches, plus out/inout parameters and struct values. Every function gets inlined by the optimizer, so
// the results must not change with it on or off.

struct Pair
{
    uint a;
    uint b;
};

[[vk::binding(0, 0)]] StructuredBuffer<uint> Input;
[[vk::binding(1, 0)]] RWStructuredBuffer<uint> Output;

uint EarlyOut(uint x)
{
    if (x < 10)
    {
        return x * 2;
    }

    if (x > 100)
    {
        return 7;
    }

    return x + 1;
}

uint FirstSquareAbove(uint n)
{
    for (uint i = 0; i < 40; i++)
    {
        if (i * i > n)
        {
            return i;
        }
    }

    return 99;
}

uint NestedSearch(uint target)
{
    for (uint i = 0; i < 6; i++)
    {
        for (uint j = 0; j < 6; j++)
        {
            if (i * j == target && i > 1)
            {
                return i * 10 + j;
            }
        }
    }

    return 1000;
}

uint SwitchInLoop(uint x)
{
    uint acc = 0;
    for (uint i = 0; i < 5; i++)
    {
        switch ((x + i) & 3)
        {
            case 0:
                acc += 1;
                break;
            case 1:
                if (acc > 3)
                {
                    return acc * 100 + i;
                }

                acc += 10;
                break;
            case 2:
                acc += 100;
                break;
            default:
                acc ^= i;
                break;
        }
    }

    return acc;
}

void OutParams(uint x, out uint a, inout uint b)
{
    a = x + 1;
    b += a * 3;
}

Pair MakePair(uint x)
{
    Pair p;
    p.a = x ^ 0x55u;
    p.b = x >> 2;
    if (x & 1)
    {
        p.b += 5;
    }

    return p;
}

uint Sum(Pair p)
{
    return p.a + p.b;
}

uint Chain(uint x)
{
    return Sum(MakePair(EarlyOut(x)));
}

uint WritesGlobal(uint x, uint slot)
{
    if (x == 3)
    {
        Output[slot] = 42;
        return 1;
    }

    Output[slot] = x;
    return 0;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint x = Input[id.x];
    uint r = EarlyOut(x);
    r = r * 31 + FirstSquareAbove(x);
    r = r * 31 + NestedSearch(x & 31);
    r = r * 31 + SwitchInLoop(x);

    uint a;
    uint b = x;
    OutParams(x, a, b);
    r = r * 31 + a + b;
    r = r * 31 + Chain(x);

    uint total = 0;
    for (uint k = 0; k < 4; k++)
    {
        total += EarlyOut(x + k) + (NestedSearch(k + (x & 7)) & 15);
    }

    Output[id.x] = r ^ total;
    uint flag = WritesGlobal(x & 7, id.x + 256);
    Output[id.x + 512] = flag;
}
