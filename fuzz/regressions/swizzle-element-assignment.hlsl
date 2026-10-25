// An element of a multi-component swizzle is not assignable here; it is one error, not a failure in lowering.
StructuredBuffer<float4> Input;

[shader("compute")]
[numthreads(1, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    float4 v = Input[id.x];
    v.xy[0] = 1.0;
}
