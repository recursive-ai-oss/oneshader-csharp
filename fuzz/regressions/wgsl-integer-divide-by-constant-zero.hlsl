// Integer division by a constant zero is undefined in HLSL; WGSL rejects it when the shader is created.
RWStructuredBuffer<int> Out;

[shader("compute")]
[numthreads(1, 1, 1)]
void main()
{
    Out[0] = 7 / 0;
    Out[1] = 7 % 0;
}
