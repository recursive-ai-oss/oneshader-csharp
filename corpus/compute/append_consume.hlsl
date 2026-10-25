// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository.
// A work list drained with ConsumeStructuredBuffer and a result list filled with AppendStructuredBuffer. Each
// buffer's hidden counter is bound explicitly, so the shader compiles to the same bindings with any compiler.

struct Item
{
    uint id;
    uint value;
};

[[vk::binding(0, 0), vk::counter_binding(1)]] ConsumeStructuredBuffer<Item> Pending : register(u0);
[[vk::binding(2, 0), vk::counter_binding(3)]] AppendStructuredBuffer<Item> Done : register(u1);
[[vk::binding(4, 0)]] cbuffer Limits : register(b0)
{
    uint Count;
    uint3 Padding;
};
[[vk::binding(5, 0)]] RWStructuredBuffer<uint> Stats : register(u2);

[shader("compute")]
[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Count)
    {
        return;
    }

    Item item = Pending.Consume();
    uint buffered, stride;
    Done.GetDimensions(buffered, stride);
    if ((item.value & 1) == 0)
    {
        Item result;
        result.id = item.id;
        result.value = item.value * 3 + stride;
        Done.Append(result);
    }

    // The capacity and element size of the output list as the shader sees them.
    if (id.x == 0)
    {
        Stats[0] = buffered;
        Stats[1] = stride;
    }
}
