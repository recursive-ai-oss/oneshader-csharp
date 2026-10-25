// Constant indices past the end of a local array or vector are undefined in HLSL; WGSL rejects them at shader creation.
cbuffer P { float4 v; };
RWStructuredBuffer<float> Out;

[shader("compute")]
[numthreads(1, 1, 1)]
void main()
{
    float a[3] = { v.x, v.y, v.z };
    Out[0] = a[7];
    Out[1] = v[9];
    a[5] = 1.0;
    Out[2] = a[0] + a[1];
}
