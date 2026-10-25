// A one-component thread id (uint1) is a vector type of its own and must be fed from the three-component built-in.
RWStructuredBuffer<uint> Out;

[shader("compute")]
[numthreads(8, 1, 1)]
void main(uint1 id : SV_DispatchThreadID, uint1 group : SV_GroupID)
{
    Out[id.x] = group.x;
}
