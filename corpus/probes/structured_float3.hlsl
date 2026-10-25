// SPDX-License-Identifier: MIT OR Apache-2.0
// probe: CSMain cs
// StructuredBuffer<float3>: DXC uses a 16-byte element stride; the candidate emits 12, which spirv-val rejects.
[[vk::binding(0, 0)]] StructuredBuffer<float3> Input;
[[vk::binding(1, 0)]] RWStructuredBuffer<float3> Output;

[numthreads(8, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    Output[id.x] = Input[id.x] * 2.0;
}
