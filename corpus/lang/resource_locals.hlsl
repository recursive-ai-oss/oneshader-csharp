// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository.
// Resource-typed locals and parameters: a local that names one of the shader's resources, rebound in straight-line
// code, passed to functions, and used for loads and stores.

[[vk::binding(0, 0)]] Texture2D<float4> TexA : register(t0);
[[vk::binding(1, 0)]] Texture2D<float4> TexB : register(t1);
[[vk::binding(2, 0)]] StructuredBuffer<uint> Input : register(t2);
[[vk::binding(3, 0)]] RWStructuredBuffer<uint4> Results : register(u3);

float4 Fetch(Texture2D<float4> t, int2 p)
{
    return t.Load(int3(p, 0));
}

uint Sum4(StructuredBuffer<uint> data, uint first)
{
    uint s = 0;
    for (uint i = 0; i < 4; i++)
    {
        s += data[first + i];
    }
    return s;
}

void Put(RWStructuredBuffer<uint4> dst, uint slot, uint4 v)
{
    dst[slot] = v;
}

[shader("compute")]
[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    Texture2D<float4> t = TexA;
    float4 a = Fetch(t, int2(1, 2));
    t = TexB;
    float4 b = t.Load(int3(3, 1, 0));

    StructuredBuffer<uint> src = Input;
    uint lo = Sum4(src, 0);
    uint hi = src[7] + Sum4(Input, 4);

    RWStructuredBuffer<uint4> dst = Results;
    Put(dst, 0, uint4(asuint(a.x), asuint(a.y), asuint(a.z), asuint(a.w)));
    dst[1] = uint4(asuint(b.x), asuint(b.y), asuint(b.z), asuint(b.w));
    Put(dst, 2, uint4(lo, hi, 0, 0));
}
