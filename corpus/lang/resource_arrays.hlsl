// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository.
// Bounded resource arrays: a texture array and a sampler array chosen by run-time values, elements passed to a
// function, a non-uniform index, and arrays of read-only buffers and constant buffers.

struct Pick
{
    float4 tint;
    uint bias;
};

[[vk::binding(0, 0)]] Texture2D<float4> Atlas[4] : register(t0);
[[vk::binding(1, 0)]] SamplerState Filters[2] : register(s0);
[[vk::binding(2, 0)]] StructuredBuffer<uint> Tables[2] : register(t4);
[[vk::binding(3, 0)]] ConstantBuffer<Pick> Picks[2] : register(b0);
[[vk::binding(4, 0)]] RWStructuredBuffer<float4> Results : register(u0);

float4 Lookup(Texture2D<float4> image, SamplerState filter, float2 uv)
{
    return image.SampleLevel(filter, uv, 0);
}

[shader("compute")]
[numthreads(16, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    uint table = i & 1;
    uint pick = (Tables[table][i >> 1] + Picks[table].bias) & 3;
    uint filter = (i >> 2) & 1;
    float2 uv = float2((i & 3) * 0.2 + 0.07, (i >> 2) * 0.23 + 0.11);

    float4 a = Lookup(Atlas[pick], Filters[filter], uv);
    float4 b = Atlas[(pick + 1) & 3].Load(int3(i & 7, (i >> 1) & 7, 0));
    float4 c = Atlas[NonUniformResourceIndex(pick)].SampleLevel(Filters[filter], uv, 0);
    Texture2D<float4> chosen = Atlas[3 - pick];
    float4 d = chosen.Load(int3(7 - (i & 7), 0, 0));

    // Constant indices into the same arrays.
    float4 fixedLookup = Atlas[2].SampleLevel(Filters[1], uv, 0);
    Results[i] = a + b * 0.5 + c * 0.25 + d * 0.125 + fixedLookup * 0.0625 + Picks[table].tint;
}
