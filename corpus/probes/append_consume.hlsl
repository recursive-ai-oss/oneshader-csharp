// SPDX-License-Identifier: MIT OR Apache-2.0
// probe: CSMain cs
// AppendStructuredBuffer and ConsumeStructuredBuffer methods are not supported.
[[vk::binding(0, 0)]] AppendStructuredBuffer<float4> Output;
[[vk::binding(1, 0)]] ConsumeStructuredBuffer<float4> Input;

[numthreads(8, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    Output.Append(Input.Consume() * 2.0);
}
