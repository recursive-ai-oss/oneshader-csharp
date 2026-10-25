// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository.
// Inheritance: inherited members and member functions, a derived member function hiding a base one,
// passing a derived struct where its base is expected, and derived structs in a structured buffer.

struct Shape
{
    float2 pos;
    float scale;

    float Area() { return 0.0; }
    float2 Center() { return pos; }
    void Move(float2 d) { pos += d; }
};

struct Rect : Shape
{
    float2 size;

    float Area() { return size.x * size.y * scale * scale; }
};

struct RoundRect : Rect
{
    float radius;

    float Inset() { return min(size.x, size.y) * 0.5 - radius; }
};

float2 CenterOf(Shape s)
{
    return s.pos;
}

[[vk::binding(0, 0)]] StructuredBuffer<Rect> Rects : register(t0);
[[vk::binding(1, 0)]] RWStructuredBuffer<uint4> Results : register(u1);

[shader("compute")]
[numthreads(8, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    Rect r = Rects[id.x];
    r.Move(float2(1.0, 2.0));

    RoundRect rr;
    rr.pos = float2(0.5, 0.25);
    rr.scale = 2.0;
    rr.size = float2(4.0, 6.0);
    rr.radius = 0.5;
    rr.Move(float2(0.5, 0.75));

    Shape plain;
    plain.pos = float2(9.0, 8.0);
    plain.scale = 1.0;

    float2 center = r.Center();
    Results[id.x] = uint4(asuint(r.Area()), asuint(center.x + center.y), asuint(rr.Area() + rr.Inset()), asuint(CenterOf(rr).y + CenterOf(r).x + plain.Area() + plain.Center().x));
}
