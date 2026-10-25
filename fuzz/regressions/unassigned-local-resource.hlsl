// A resource declared locally and never assigned: one error on the use, no internal failure after it.
[shader("compute")]
[numthreads(1, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    StructuredBuffer<uint> Pixels;
    RWStructuredBuffer<uint> Out;
    uint pixel = Pixels[id.x];
    Out[id.x] = pixel;
}
