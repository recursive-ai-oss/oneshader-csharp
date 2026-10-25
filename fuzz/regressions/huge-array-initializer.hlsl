// An array initializer covering 65535 elements would need one SPIR-V instruction longer than the format allows.
RWStructuredBuffer<float> Out;

[shader("compute")]
[numthreads(1, 1, 1)]
void main()
{
    float arr[65535] = { 1.0 };
    Out[0] = arr[3];
}
