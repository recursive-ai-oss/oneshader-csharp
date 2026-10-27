// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: 64-bit integer scalars in structured buffers with the one 64-bit atomic every target
// with 64-bit integers has: unsigned InterlockedMax / InterlockedMin on a buffer, result unused.

[[vk::binding(0, 0)]] StructuredBuffer<uint64_t> Values;
[[vk::binding(1, 0)]] RWStructuredBuffer<uint64_t> Maxima;
[[vk::binding(2, 0)]] RWStructuredBuffer<uint64_t> Minima;
[[vk::binding(3, 0)]] RWStructuredBuffer<uint64_t> Mixed;

[shader("compute")]
[numthreads(16, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint64_t v = Values[id.x];
    InterlockedMax(Maxima[id.x & 3], v);
    InterlockedMin(Minima[id.x & 3], v);

    // Wrapping multiply, shifts and a rotate-like mix.
    uint64_t h = (v * 0x9E3779B97F4A7C15ull) ^ (v >> 29);
    Mixed[id.x] = h + (v << 7) + (uint64_t)id.x;
}
