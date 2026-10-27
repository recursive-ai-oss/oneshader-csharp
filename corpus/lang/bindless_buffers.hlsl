// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository.
// Bindless buffers: unbounded arrays of structured buffers, byte-address buffers, read-write byte-address buffers and constant
// buffers chosen per thread with NonUniformResourceIndex, including atomics on the chosen buffer.

struct Scale
{
    float4 factor;
    uint bias;
    uint3 pad;
};

[[vk::binding(0, 1)]] StructuredBuffer<float4> Sources[] : register(t0, space1);
[[vk::binding(0, 2)]] RWByteAddressBuffer Sinks[] : register(u0, space2);
[[vk::binding(0, 3)]] ConstantBuffer<Scale> Scales[] : register(b0, space3);
[[vk::binding(0, 4)]] ByteAddressBuffer Tables[] : register(t0, space4);
[[vk::binding(0, 0)]] RWStructuredBuffer<float4> Results : register(u0);

[shader("compute")]
[numthreads(32, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    // Which buffer a thread uses depends on its index, so neighbouring threads use different descriptors.
    uint source = NonUniformResourceIndex(i % 3);
    uint sink = NonUniformResourceIndex((i * 5) % 4);
    uint scale = NonUniformResourceIndex((i / 2) % 2);

    float4 v = Sources[source][i / 3] * Scales[scale].factor;
    uint row = Tables[NonUniformResourceIndex(source)].Load(((i / 3) % 4) * 4) + Scales[scale].bias;

    // Each sink counts the threads that chose it and keeps the largest row it saw.
    Sinks[sink].InterlockedAdd(0, 1);
    uint ignored;
    Sinks[sink].InterlockedMax(4, row, ignored);
    Sinks[sink].Store(8 + 4 * i, asuint(v.x));

    Results[i] = v + float4(row, source, sink, scale);
}
