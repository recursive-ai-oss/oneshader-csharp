// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository.
// precise: results that change if the compiler fuses a multiply and an add or reassociates a sum ((x + y) - x, Kahan
// summation). The inputs come from a buffer so nothing folds at compile time. Division is not here: GPUs allow it
// a few ulp of error, so two correct implementations can differ in the last bit.

StructuredBuffer<float> In : register(t0);
RWStructuredBuffer<float> Out : register(u1);

precise float Mad(float a, float b, float c)
{
    return a * b + c;
}

[shader("compute")]
[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    float a = In[0];
    float c = In[1];
    float x = In[2];
    float y = In[3];

    precise float fused = a * a + c;          // 0 when the product is rounded first
    precise float cancel = (x + y) - x;       // 0 when the sum is rounded first

    // Kahan summation: its compensation term is zero if the compiler reassociates it.
    precise float sum = 0.0;
    precise float comp = 0.0;
    for (uint i = 0; i < 16; i++)
    {
        precise float v = In[8 + i] - comp;
        precise float t = sum + v;
        comp = (t - sum) - v;
        sum = t;
    }

    precise float w;
    w = a * a;
    w = w + c;

    Out[0] = fused;
    Out[1] = cancel;
    Out[2] = comp;
    Out[3] = sum;
    Out[4] = Mad(a, a, c);
    Out[5] = w;
}
