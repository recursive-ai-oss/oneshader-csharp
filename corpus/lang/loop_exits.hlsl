// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository.
// Every way out of a loop body that is not the loop test: a guarded `continue` or `break` that is not the last statement,
// in [unroll], [loop], while and do-while loops; nested ifs and else arms; switch inside a loop; loops nested in loops;
// `return` from helpers (from loops, nested loops and switch cases) and from the entry point. Each result goes to its own
// slot so a wrong one is easy to find; the last slot (23) stays untouched when the entry point returns early.

StructuredBuffer<uint> In : register(t0);
RWStructuredBuffer<uint> Out : register(u1);

uint ContinueUnrolled(uint x)
{
    uint sum = 0;
    [unroll] for (uint i = 0; i < 8; i++)
    {
        if (((x >> i) & 1) == 0) { continue; }
        sum += i + 1;
        sum ^= 0x10;
    }
    return sum;
}

uint BreakUnrolled(uint x)
{
    uint sum = 0;
    [unroll] for (uint i = 0; i < 8; i++)
    {
        if (i * 7 > x) { break; }
        sum += i;
    }
    return sum;
}

uint WhileContinue(uint x)
{
    uint n = 0, sum = 0;
    [loop] while (n < 12)
    {
        n++;
        if (n % 3 == x % 3) { continue; }
        sum += n;
    }
    return sum;
}

uint DoWhileContinue(uint x)
{
    uint n = 0, sum = 0;
    do
    {
        n++;
        if ((n & 1) != 0) { continue; }
        sum += n;
    } while (n < x % 13 + 1);
    return sum;
}

uint NestedIfContinue(uint x)
{
    uint sum = 0;
    [loop] for (uint i = 0; i < 10; i++)
    {
        if ((i & 1) != 0)
        {
            if (x % (i + 1) == 0) { continue; }
            sum += 100;
        }
        sum += i;
    }
    return sum;
}

uint ElseContinue(uint x)
{
    uint sum = 0;
    for (uint i = 0; i < 9; i++)
    {
        if (((x + i) & 3) != 0) { sum += 1; } else { continue; }
        sum += 10;
    }
    return sum;
}

uint ContinueAndBreakArms(uint x)
{
    uint sum = 0;
    for (uint i = 0; i < 6; i++)
    {
        if (i == x % 6) { break; }
        else if ((i & 1) != 0) { continue; }
        sum += i * 3 + 1;
    }
    return sum;
}

uint NestedUnrolled(uint x)
{
    uint rows = x % 5, cols = (x / 5) % 5, sum = 0;
    [unroll] for (uint r = 0; r < 4; r++)
    {
        if (r >= rows) { continue; }
        [unroll] for (uint c = 0; c < 4; c++)
        {
            if (c >= cols) { continue; }
            sum += r * 4 + c + 1;
        }
    }
    return sum;
}

uint InnerBreakOuterContinue(uint x)
{
    uint sum = 0;
    for (uint i = 0; i < 5; i++)
    {
        bool found = false;
        [unroll] for (uint j = 0; j < 5; j++)
        {
            if (j * i == x % 7) { found = true; break; }
        }
        if (!found) { continue; }
        sum += i + 1;
    }
    return sum;
}

uint CarriedValues(uint x)
{
    uint a = 1, b = x % 5, steps = 0;
    for (uint i = 0; i < 10; i++)
    {
        uint t = a + b;
        if ((t & 4) != 0) { a = b; continue; }
        a = b;
        b = t;
        steps++;
    }
    return a * 100 + b + steps * 10000;
}

uint SwitchContinue(uint x)
{
    uint sum = 0;
    for (uint i = 0; i < 6; i++)
    {
        switch (i & 1)
        {
        case 0:
            if (((x >> i) & 1) != 0) { continue; }
            sum += 1;
            break;
        default:
            sum += 2;
            break;
        }
        sum += 10;
    }
    return sum;
}

uint ReturnFromLoop(uint x)
{
    [loop] for (uint i = 0; i < 16; i++)
    {
        if ((x * (i + 3)) % 7 == 0) { return i; }
    }
    return 99;
}

uint ReturnFromUnrolled(uint x)
{
    uint sum = 0;
    [unroll] for (uint i = 0; i < 6; i++)
    {
        sum += i;
        if (sum > x % 12) { return sum * 100 + i; }
    }
    return 5;
}

uint ReturnFromNested(uint x)
{
    [unroll] for (uint i = 0; i < 4; i++)
    {
        for (uint j = 0; j < 4; j++)
        {
            if (i * j == x % 9 && i != 0) { return i * 10 + j; }
        }
    }
    return 77;
}

uint ReturnFromSwitch(uint x)
{
    uint sum = 0;
    for (uint i = 0; i < 5; i++)
    {
        switch ((x + i) % 4)
        {
        case 1:
            if (i > 2) { return sum + 1000; }
            sum += 1;
            break;
        case 2:
            sum += 20;
            break;
        default:
            sum += 3;
            break;
        }
    }
    return sum;
}

uint NestedLast(uint x)
{
    // The outer if is the last statement, so the inner continue and the outer join meet at the loop's continue block.
    uint sum = 0;
    for (uint i = 0; i < 8; i++)
    {
        if (((x + i) & 1) != 0)
        {
            if (((x >> i) & 3) == 1) { continue; }
            sum += i + 1;
        }
    }
    return sum;
}

uint DoWhileInFor(uint x)
{
    uint sum = 0;
    for (uint i = 0; i < 4; i++)
    {
        if (((x >> i) & 1) != 0) { continue; }
        uint n = 0;
        do
        {
            n++;
            if (n == i + 1) { continue; }
            sum += n * (i + 1);
        } while (n < 3);
    }
    return sum;
}

void HelperReturn(inout uint acc, uint x)
{
    for (uint i = 0; i < 8; i++)
    {
        if (i == x % 8)
        {
            acc += 5;
            return;
        }
        acc += 1;
    }
    acc += 1000;
}

[shader("compute")]
[numthreads(16, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    uint x = In[i];
    uint o = i * 24;
    Out[o + 0] = ContinueUnrolled(x);
    Out[o + 1] = BreakUnrolled(x);
    Out[o + 2] = WhileContinue(x);
    Out[o + 3] = DoWhileContinue(x);
    Out[o + 4] = NestedIfContinue(x);
    Out[o + 5] = ElseContinue(x);
    Out[o + 6] = ContinueAndBreakArms(x);
    Out[o + 7] = NestedUnrolled(x);
    Out[o + 8] = InnerBreakOuterContinue(x);
    Out[o + 9] = CarriedValues(x);
    Out[o + 10] = SwitchContinue(x);
    Out[o + 11] = ReturnFromLoop(x) + ReturnFromUnrolled(x) * 256;
    Out[o + 12] = ReturnFromNested(x) + ReturnFromSwitch(x) * 256;
    uint acc = 0;
    HelperReturn(acc, x);
    Out[o + 13] = acc;

    Out[o + 14] = NestedLast(x);
    Out[o + 15] = DoWhileInFor(x);

    Out[o + 22] = 1;
    [unroll] for (uint k = 0; k < 4; k++)
    {
        if ((x + k) % 11 == 0)
        {
            Out[o + 22] = 2;
            return;
        }
    }
    Out[o + 23] = 3;
}
