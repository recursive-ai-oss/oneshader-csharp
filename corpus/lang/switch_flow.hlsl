// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository.
// switch statements the targets without fall-through must rebuild: cases that run into the next one,
// stacked labels, a default in the middle (reached by fall-through and by no match), negative labels,
// a conditional break inside a case, return from a case, and a switch inside a loop with continue.

RWStructuredBuffer<uint> Out : register(u0);
RWStructuredBuffer<int> OutSigned : register(u1);

uint Fall(uint op, uint x)
{
    uint r = x;
    switch (op)
    {
    case 0:
        r += 1;
    case 1:
        r *= 3;
        break;
    default:
        r ^= 0x55;
    case 3:
        r += 100;
        break;
    case 4:
    case 5:
        r -= 2;
        break;
    case 6:
        r = 7;
    case 7:
        if ((x & 1) != 0)
            break;
        r += 9;
        break;
    case 8:
        r += 1;
    case 9:
        r += 2;
    case 10:
        r += 4;
        break;
    }
    return r;
}

int Signed(int v)
{
    int r = v;
    switch (v)
    {
    case -2:
        r += 20;
    case -1:
        r -= 5;
        break;
    default:
        r = 1000;
    case 2:
        r += 1;
    case 3:
        return r * 2;
    }
    return r;
}

uint InLoop(uint n)
{
    uint sum = 0;
    for (uint i = 0; i < n; i++)
    {
        switch (i & 3)
        {
        case 0:
            sum += 1;
        case 1:
            sum += 10;
            if (i > 6)
                continue;
            break;
        default:
            sum += 100;
        case 3:
            sum += 1000;
            break;
        }
        sum += 5;
    }
    return sum;
}

[shader("compute")]
[numthreads(16, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    Out[i] = Fall(i % 12, i * 7 + 3) + InLoop(i % 11);
    OutSigned[i] = Signed((int)(i % 8) - 4);
}
