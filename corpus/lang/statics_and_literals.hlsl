// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository.
// Static struct members, member functions defined after their struct, template deduction through a struct template,
// a constant buffer sharing its name with a member, a whole ConstantBuffer<T> copied into a local, and integer literals
// evaluated in wide arithmetic.

struct Grid
{
    static const uint Cells = 8;
    static const float Scale = 2.5;
    static const uint Mask = Cells - 1;

    uint origin;

    uint Wrap(uint x);
    uint Index(uint x, uint y) { return Wrap(origin + y * Cells + x); }
    static uint Twice(uint x);
    static Grid Make(uint at) { Grid g; g.origin = at; return g; }
};

uint Grid::Wrap(uint x)
{
    return x & Mask;
}

uint Grid::Twice(uint x)
{
    return x * 2;
}

template<typename T>
struct Box
{
    T value;
};

template<typename T, uint N>
struct Pair
{
    T item[N];
};

template<typename T>
T Unbox(Box<T> b)
{
    return b.value;
}

template<typename T>
Box<T> Wrap(T v)
{
    Box<T> b;
    b.value = v;
    return b;
}

template<typename T>
T Last(Pair<T, 2> p)
{
    return p.item[1];
}

struct Light
{
    float4 color;
    uint kind;
};

[[vk::binding(0, 0)]] cbuffer Lights : register(b0)
{
    Light Lights[3];
};

struct Frame
{
    float4 tint;
    uint k;
    float2 pair[2];
};

[[vk::binding(2, 0)]] ConstantBuffer<Frame> FrameData : register(b1);

[[vk::binding(1, 0)]] RWStructuredBuffer<uint4> Results : register(u0);

[shader("compute")]
[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    Grid g = Grid::Make(13);
    uint a = g.Index(3, 2);
    uint b = Grid::Twice(Grid::Cells) + (uint)Grid::Scale;
    uint cells[Grid::Cells];
    cells[Grid::Mask] = 7;

    Box<float> f = Wrap(1.5);
    Box<uint> u = Wrap(4u);
    Pair<int, 2> p;
    p.item[0] = 3;
    p.item[1] = 9;

    uint total = 0;
    for (uint i = 0; i < 3; i++)
    {
        total += Lights[i].kind;
    }

    Results[0] = uint4(a, b, cells[7], Grid::Mask);
    Results[1] = uint4(asuint(Unbox(f)), Unbox(u), Last(p), total);
    Results[2] = uint4((1 << 31) >> 3, (3000000000 * 2) >> 1, 0xFFFFFFFF + 1, 100000 * 100000);
    // A whole ConstantBuffer<T> copied into a local struct.
    Frame frame = FrameData;
    Results[3] = asuint(Lights[1].color) + uint4(frame.k, asuint(frame.tint.y), asuint(frame.pair[1].x), asuint(frame.pair[0].y));
}
