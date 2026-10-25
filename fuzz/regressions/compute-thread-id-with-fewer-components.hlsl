// HLSL lets a compute shader ask for fewer components of its ids; Vulkan's built-ins always have three.
RWStructuredBuffer<uint> Out;

[shader("compute")]
[numthreads(4, 4, 1)]
void main(uint2 id : SV_DispatchThreadID, uint2 group : SV_GroupID, uint local : SV_GroupThreadID)
{
    Out[id.y * 16 + id.x] = group.x + group.y + local;
}
