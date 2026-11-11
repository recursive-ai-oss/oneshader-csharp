// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: packed 8-bit dot products (Shader Model 6.4). Lane 0 is the least significant byte, products are
// summed and added to the accumulator, and the sum wraps modulo 2^32.

[[vk::binding(0, 0)]] StructuredBuffer<uint> A;
[[vk::binding(1, 0)]] StructuredBuffer<uint> B;
[[vk::binding(2, 0)]] RWStructuredBuffer<int> S;
[[vk::binding(3, 0)]] RWStructuredBuffer<uint> U;
[[vk::binding(4, 0)]] RWStructuredBuffer<int> Sums;

[shader("compute")]
[numthreads(64, 1, 1)]
void Dot4(uint id : SV_DispatchThreadID)
{
    uint a = A[id];
    uint b = B[id];
    S[id] = dot4add_i8packed(a, b, S[id]);
    U[id] = dot4add_u8packed(a, b, U[id]);

    // Byte sums through an all-ones operand and an accumulator that is an expression rather than a loaded value.
    int zero = (int)(id >> 20);
    Sums[id] = dot4add_i8packed(a, 0x01010101u, zero) + (int)dot4add_u8packed(b, 0x01010101u, 0u);
}
