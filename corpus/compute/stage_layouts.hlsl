// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: constant-buffer bools, float3 structured-buffer elements, half conversions,
// faceforward and log10 in one kernel.

[[vk::binding(0, 0)]] cbuffer Flags
{
    float4 Tint;
    bool Enabled;
    float Scale;
    bool2 Pair;
};

[[vk::binding(1, 0)]] StructuredBuffer<float3> Points;
[[vk::binding(2, 0)]] RWStructuredBuffer<float3> Moved;
[[vk::binding(3, 0)]] RWStructuredBuffer<uint> Bits;
[[vk::binding(4, 0)]] RWStructuredBuffer<float4> Misc;

[shader("compute")]
[numthreads(16, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    float3 p = Points[id.x];
    Moved[id.x] = Enabled ? p * Scale + Tint.xyz : p;

    uint3 half3 = f32tof16(p);
    Bits[id.x * 4 + 0] = half3.x;
    Bits[id.x * 4 + 1] = half3.y;
    Bits[id.x * 4 + 2] = half3.z;
    Bits[id.x * 4 + 3] = (Pair.x && Pair.y) ? (Enabled ? 3u : 1u) : 0u;

    // Round trip through half precision, a face-forwarded vector and a base-10 logarithm.
    float3 back = f16tof32(half3);
    float3 facing = faceforward(back, Tint.xyz, p.zxy);
    Misc[id.x] = float4(facing.x + facing.y + facing.z, back.x, back.y, log10(abs(p.x) + 1.0));
}
