// A constant element index whose byte offset overflows 32 bits is out of bounds; each target must still emit valid code.
RWStructuredBuffer<uint4> Out;

[shader("compute")]
[numthreads(1, 1, 1)]
void main()
{
    Out[0x40000001] = uint4(1, 2, 3, 4);
    Out[-1] = uint4(5, 6, 7, 8);
}
