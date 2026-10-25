// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: frustum culling of bounding spheres. Visible instance indices are
// appended to a list and counted in an indirect draw argument buffer. Each group reserves its
// range with a single global atomic after counting its own survivors in group-shared memory.

#define GROUP_SIZE 64

struct Instance
{
    float3 Center;
    float Radius;
};

[[vk::binding(0, 0)]] cbuffer Cull
{
    float4 Planes[6]; // inside-facing, xyz normal (unit length), w offset
    uint InstanceCount;
    uint IndexCountPerInstance;
};

[[vk::binding(1, 0)]] StructuredBuffer<Instance> Instances;
[[vk::binding(2, 0)]] RWStructuredBuffer<uint> VisibleIndices;
[[vk::binding(3, 0)]] RWStructuredBuffer<uint> VisibleFlags;
// Indirect indexed draw: indexCount, instanceCount, firstIndex, baseVertex, firstInstance.
[[vk::binding(4, 0)]] RWByteAddressBuffer DrawArgs;

groupshared uint GroupCount;
groupshared uint GroupBase;

bool InsideFrustum(Instance instance)
{
    [unroll]
    for (int i = 0; i < 6; ++i)
    {
        if (dot(Planes[i].xyz, instance.Center) + Planes[i].w < -instance.Radius)
        {
            return false;
        }
    }
    return true;
}

[shader("compute")]
[numthreads(GROUP_SIZE, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID, uint groupIndex : SV_GroupIndex)
{
    if (groupIndex == 0)
    {
        GroupCount = 0;
        GroupBase = 0;
    }
    GroupMemoryBarrierWithGroupSync();

    bool visible = id.x < InstanceCount && InsideFrustum(Instances[id.x]);
    uint local = 0;
    if (visible)
    {
        InterlockedAdd(GroupCount, 1, local);
    }
    if (id.x < InstanceCount)
    {
        VisibleFlags[id.x] = visible ? 1u : 0u;
    }
    GroupMemoryBarrierWithGroupSync();

    if (groupIndex == 0)
    {
        if (GroupCount > 0)
        {
            DrawArgs.InterlockedAdd(4, GroupCount, GroupBase);
        }
        if (id.x == 0)
        {
            DrawArgs.Store(0, IndexCountPerInstance);
        }
    }
    GroupMemoryBarrierWithGroupSync();

    if (visible)
    {
        VisibleIndices[GroupBase + local] = id.x;
    }
}
