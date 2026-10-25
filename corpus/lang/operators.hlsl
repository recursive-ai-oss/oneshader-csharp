// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository.
// Operator overloading (HLSL 2021 member operators): arithmetic, comparison, subscript and call operators,
// and overloads of one operator for different right-hand types.

struct Complex
{
    float re;
    float im;

    static Complex Make(float r, float i)
    {
        Complex c;
        c.re = r;
        c.im = i;
        return c;
    }

    Complex operator+(Complex o) { return Make(re + o.re, im + o.im); }
    Complex operator+(float s) { return Make(re + s, im); }
    Complex operator-(Complex o) { return Make(re - o.re, im - o.im); }
    Complex operator*(Complex o) { return Make(re * o.re - im * o.im, re * o.im + im * o.re); }
    Complex operator*(float s) { return Make(re * s, im * s); }
    bool operator==(Complex o) { return re == o.re && im == o.im; }
    bool operator<(Complex o) { return re * re + im * im < o.re * o.re + o.im * o.im; }
    float operator[](int i) { return i == 0 ? re : im; }
};

struct Poly3
{
    float c0;
    float c1;
    float c2;
    float c3;

    float operator()(float x) { return ((c3 * x + c2) * x + c1) * x + c0; }
};

struct Bits
{
    uint v;

    Bits operator&(Bits o) { Bits r; r.v = v & o.v; return r; }
    Bits operator|(Bits o) { Bits r; r.v = v | o.v; return r; }
    Bits operator^(Bits o) { Bits r; r.v = v ^ o.v; return r; }
    Bits operator+(Bits o) { Bits r; r.v = v + o.v; return r; }
};

[[vk::binding(0, 0)]] RWStructuredBuffer<uint4> Results : register(u0);

[shader("compute")]
[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    Complex a = Complex::Make(1.0, 2.0);
    Complex b = Complex::Make(0.5, -1.0);

    Complex sum = a + b;
    Complex shifted = a + 0.25;
    Complex prod = a * b;
    Complex scaled = (a - b) * 2.0;
    float parts = a[0] + b[1] * 4.0;

    Poly3 p;
    p.c0 = 1.0;
    p.c1 = -2.0;
    p.c2 = 0.5;
    p.c3 = 0.25;

    Bits x;
    x.v = 0xF0F0u;
    Bits y;
    y.v = 0x0FF0u;
    Bits z = ((x & y) | (x ^ y)) + y;

    uint flags = (a == a ? 1u : 0u) | (a == b ? 2u : 0u) | (b < a ? 4u : 0u) | (a < b ? 8u : 0u);

    Results[0] = uint4(asuint(sum.re), asuint(sum.im), asuint(shifted.re), asuint(shifted.im));
    Results[1] = uint4(asuint(prod.re), asuint(prod.im), asuint(scaled.re), asuint(scaled.im));
    Results[2] = uint4(asuint(parts), asuint(p(2.0)), z.v, flags);
}
