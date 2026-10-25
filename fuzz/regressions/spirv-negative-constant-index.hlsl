// A negative constant index into a storage buffer is out of bounds, but SPIR-V validation rejects a negative constant index outright.
RWStructuredBuffer<uint4> Out;

[shader("compute")]
[numthreads(1, 1, 1)]
void main()
{
    Out[-1] = uint4(1, 2, 3, 4);
}
