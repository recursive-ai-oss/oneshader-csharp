// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository.
// Member functions: this, calls between members, static members, members that change the object,
// and member calls on elements of a read-write buffer.

struct Counter
{
    uint value;
    uint step;

    static Counter Make(uint start, uint stepBy)
    {
        Counter c;
        c.value = start;
        c.step = stepBy;
        return c;
    }

    void Advance() { value += step; }

    uint Next()
    {
        uint old = value;
        Advance();
        return old;
    }

    uint Peek() { return this.value; }
};

struct Stats
{
    float lo;
    float hi;
    uint count;

    void Reset()
    {
        lo = 1000.0;
        hi = -1000.0;
        count = 0;
    }

    void Add(float v)
    {
        lo = min(lo, v);
        hi = max(hi, v);
        this.count++;
    }

    float Range() { return hi - lo; }
};

[[vk::binding(0, 0)]] RWStructuredBuffer<uint4> Results : register(u0);
[[vk::binding(1, 0)]] RWStructuredBuffer<Stats> Accum : register(u1);

[shader("compute")]
[numthreads(4, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    Counter c = Counter::Make(10 + id.x, 3);
    uint a = c.Next();
    uint b = c.Next();
    c.Advance();

    Stats s;
    s.Reset();
    s.Add(1.5);
    s.Add(-2.25);
    s.Add(0.75 + id.x);

    // Member calls on a buffer element change the element in place.
    Accum[id.x].Reset();
    Accum[id.x].Add(float(id.x));
    Accum[id.x].Add(4.0);

    Results[id.x] = uint4(a + (b << 8), c.Peek(), asuint(s.Range()), s.count);
}
