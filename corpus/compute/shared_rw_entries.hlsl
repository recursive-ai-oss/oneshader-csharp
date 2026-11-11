// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: two compute entry points share two read-write buffers; Scale writes Data and only reads Totals, Sum writes Totals and only reads Data.
// Every target must declare the buffer the same way in both entries so one pipeline layout fits both.

[[vk::binding(0, 0)]] RWStructuredBuffer<uint> Data;
[[vk::binding(1, 0)]] RWByteAddressBuffer Totals;

[shader("compute")]
[numthreads(64, 1, 1)]
void Scale(uint3 id : SV_DispatchThreadID)
{
    Data[id.x] = Data[id.x] * 2u + (Totals.Load(0) & 1u);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void Sum(uint3 id : SV_DispatchThreadID)
{
    Totals.Store(id.x * 4, Data[id.x] + 1u);
}
