// SPDX-License-Identifier: MIT OR Apache-2.0
// probe: VSMain vs expect-error
// Two members packed at the same offset: DXC reports an error; the candidate accepts it and emits invalid SPIR-V.
[[vk::binding(0, 0)]] cbuffer Overlap
{
    float4 A : packoffset(c0);
    float4 B : packoffset(c0);
};

float4 VSMain(float3 position : POSITION) : SV_Position
{
    return A + B;
}
