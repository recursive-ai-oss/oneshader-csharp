// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: a compute kernel configured entirely by push constants, declared as a
// struct-typed global. Layout of the block (vector-relaxed std430): Count 0, Scale 4, Offset 16, Mode 28,
// Bias 32, Mul 48, size 64.

struct Params
{
    uint Count;
    float Scale;
    float3 Offset;
    uint Mode;
    float2 Bias;
    float4 Mul;
};

[[vk::push_constant]] Params Pc;

[[vk::binding(0, 0)]] StructuredBuffer<float4> Input;
[[vk::binding(1, 0)]] RWStructuredBuffer<float4> Output;

[shader("compute")]
[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Pc.Count)
    {
        return;
    }

    float4 v = Input[id.x];
    v.xyz = v.xyz * Pc.Scale + Pc.Offset;
    v.xy += Pc.Bias;
    v *= Pc.Mul;
    if (Pc.Mode == 1u)
    {
        v = v.wzyx;
    }

    Output[id.x] = v;
}
