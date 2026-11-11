// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: a group returns early when its id is past a limit from a constant buffer, and the groups that stay
// synchronise on group-shared memory after the return. SV_GroupID is the same for the whole group, so the barrier is in uniform
// control flow; WGSL only accepts that when the shader reads the workgroup id itself rather than a copy of it.

[[vk::binding(0, 0)]] cbuffer Limits
{
    uint GroupCount;
    uint Bias;
};

[[vk::binding(1, 0)]] RWStructuredBuffer<uint> Out;

groupshared uint Staging[64];

[shader("compute")]
[numthreads(64, 1, 1)]
void CSMain(uint3 gid : SV_GroupID, uint tid : SV_GroupIndex)
{
    if (gid.x >= GroupCount)
    {
        return;
    }

    Staging[tid] = gid.x * 1000u + tid + Bias;
    GroupMemoryBarrierWithGroupSync();
    Out[gid.x * 64u + tid] = Staging[63u - tid] + Staging[(tid + 1u) & 63u];
}
