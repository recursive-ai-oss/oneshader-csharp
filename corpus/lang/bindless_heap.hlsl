// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository.
// Shader Model 6.6 dynamic resources: textures, buffers and samplers fetched from ResourceDescriptorHeap and
// SamplerDescriptorHeap with indices that come from data, one of them with NonUniformResourceIndex.

struct Item
{
    float4 tint;
    uint texture;
    uint sampler;
    uint pad0;
    uint pad1;
};

[[vk::binding(0, 1)]] StructuredBuffer<Item> Items : register(t0, space1);
[[vk::binding(1, 1)]] RWStructuredBuffer<float4> Results : register(u0, space1);

[shader("compute")]
[numthreads(16, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    Item item = Items[id.x];

    Texture2D<float4> image = ResourceDescriptorHeap[NonUniformResourceIndex(item.texture)];
    SamplerState filter = SamplerDescriptorHeap[item.sampler];
    StructuredBuffer<uint> table = ResourceDescriptorHeap[2];
    RWByteAddressBuffer counters = ResourceDescriptorHeap[3];
    Texture2D<float4> extra = (Texture2D<float4>)ResourceDescriptorHeap[(item.texture + 1) & 1];

    float2 uv = float2((id.x & 3) * 0.25 + 0.1, (id.x >> 2) * 0.25 + 0.1);
    float4 color = image.SampleLevel(filter, uv, 0) * item.tint + extra.Load(int3(id.x & 7, id.x >> 1 & 7, 0)) * 0.25;
    uint k = table[id.x & 3];
    uint ignored;
    counters.InterlockedAdd(k * 4, 1, ignored);

    Results[id.x] = color + float4(k, 0, 0, 0);
}
