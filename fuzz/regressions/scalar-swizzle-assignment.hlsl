// A swizzle of a scalar names the scalar itself, also on the left of an assignment.
RWStructuredBuffer<float> Out;

[shader("compute")]
[numthreads(1, 1, 1)]
void main()
{
    float a[2];
    float b;
    a[0].x = 1.0;
    b.x = 2.0;
    b.x += a[0].x;
    Out[0] = a[0] + b;
}
