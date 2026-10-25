// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository.
// sizeof on types and expressions (usable as constants), and non-const static local variables that keep
// their value between calls and start at zero.

struct Packed
{
    float3 a;
    float b;
    int2 c;
};

static const uint kPackedSize = sizeof(Packed);

uint NextTicket()
{
    static uint issued;
    issued++;
    return issued;
}

float Smooth(float x)
{
    static float previous = 0.5;
    float r = lerp(previous, x, 0.5);
    previous = r;
    return r;
}

[[vk::binding(0, 0)]] RWStructuredBuffer<uint4> Results : register(u0);

[shader("compute")]
[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint five[5];
    float4x4 m = (float4x4)0;
    uint padded[kPackedSize / 4];
    padded[0] = 1;

    uint t0 = NextTicket();
    uint t1 = NextTicket();
    uint t2 = NextTicket();

    float s0 = Smooth(1.0);
    float s1 = Smooth(0.0);
    float s2 = Smooth(2.0);

    Results[0] = uint4(sizeof(float), sizeof(float3), sizeof(m), sizeof(Packed));
    Results[1] = uint4(sizeof(five), sizeof(id), kPackedSize, sizeof(padded));
    Results[2] = uint4(t0, t1, t2, NextTicket());
    Results[3] = uint4(asuint(s0), asuint(s1), asuint(s2), asuint(Smooth(0.0)));
}
