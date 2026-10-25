// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: assigns point lights to clusters. One thread per cluster tests every
// light's bounding sphere against the cluster's box and writes the hits into that cluster's own fixed-size
// slice of the index list, so the result does not depend on thread scheduling.

struct ClusterBounds
{
    float4 Min;
    float4 Max;
};

struct PointLight
{
    float3 Position; // view space
    float Range;
    float3 Color;
    float Intensity;
};

[[vk::binding(0, 0)]] cbuffer Cull
{
    uint ClusterCount;
    uint LightCount;
    uint MaxLightsPerCluster;
    uint Unused;
};

[[vk::binding(1, 0)]] StructuredBuffer<ClusterBounds> Clusters;
[[vk::binding(2, 0)]] StructuredBuffer<PointLight> Lights;
[[vk::binding(3, 0)]] RWStructuredBuffer<uint2> ClusterGrid; // x: first index, y: count
[[vk::binding(4, 0)]] RWStructuredBuffer<uint> LightIndices;

bool SphereIntersectsBox(float3 center, float radius, float3 boxMin, float3 boxMax)
{
    float3 closest = clamp(center, boxMin, boxMax);
    float3 delta = closest - center;
    return dot(delta, delta) <= radius * radius;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint cluster = id.x;
    if (cluster >= ClusterCount)
    {
        return;
    }
    ClusterBounds bounds = Clusters[cluster];
    uint offset = cluster * MaxLightsPerCluster;
    uint count = 0;
    for (uint i = 0; i < LightCount && count < MaxLightsPerCluster; ++i)
    {
        PointLight light = Lights[i];
        if (SphereIntersectsBox(light.Position, light.Range, bounds.Min.xyz, bounds.Max.xyz))
        {
            LightIndices[offset + count] = i;
            ++count;
        }
    }
    ClusterGrid[cluster] = uint2(offset, count);
}
