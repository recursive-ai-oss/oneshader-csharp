// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: 256-bin luminance histogram of packed RGBA8 pixels. Each group
// counts into group-shared atomics, then adds its counts to the global histogram.

[[vk::binding(0, 0)]] cbuffer Params
{
    uint PixelCount;
    uint3 ParamsPad;
};

[[vk::binding(1, 0)]] StructuredBuffer<uint> Pixels; // 0xAABBGGRR
[[vk::binding(2, 0)]] RWStructuredBuffer<uint> Histogram;

groupshared uint LocalBins[256];

[shader("compute")]
[numthreads(256, 1, 1)]
void CSMain(uint3 dispatchId : SV_DispatchThreadID, uint groupIndex : SV_GroupIndex)
{
    LocalBins[groupIndex] = 0;
    GroupMemoryBarrierWithGroupSync();

    if (dispatchId.x < PixelCount)
    {
        uint pixel = Pixels[dispatchId.x];
        uint r = pixel & 0xFFu;
        uint g = (pixel >> 8) & 0xFFu;
        uint b = (pixel >> 16) & 0xFFu;
        // Integer luma with weights summing to 256, so the bin is exact on every device.
        uint bin = (r * 54u + g * 183u + b * 19u) >> 8;
        uint ignored;
        InterlockedAdd(LocalBins[bin], 1u, ignored);
    }
    GroupMemoryBarrierWithGroupSync();

    uint count = LocalBins[groupIndex];
    if (count != 0)
    {
        uint ignored;
        InterlockedAdd(Histogram[groupIndex], count, ignored);
    }
}
