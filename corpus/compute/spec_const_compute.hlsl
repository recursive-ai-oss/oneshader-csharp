// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: a kernel whose result depends on four specialization constants (bool, int, uint, float),
// on a constant derived from them, and on a loop bound that only the pipeline knows.

[[vk::constant_id(0)]] const bool UseOffset = false;
[[vk::constant_id(1)]] const int Rounds = 4;
[[vk::constant_id(2)]] const uint Mask = 0xFFu;
[[vk::constant_id(3)]] const float Scale = 0.5;

// Derived from specialization constants: not known until the pipeline is created.
static const int Bias = Rounds * 3 + 1;
static const float Shift = Scale * 8.0;

[[vk::binding(0, 0)]] RWStructuredBuffer<uint> OutInts;
[[vk::binding(1, 0)]] RWStructuredBuffer<float> OutFloats;

[shader("compute")]
[numthreads(16, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint acc = 0;
    for (int i = 0; i < Rounds; i++)
    {
        acc += (id.x + (uint)i) & Mask;
    }

    if (UseOffset)
    {
        acc += 1000u;
    }

    OutInts[id.x] = acc + (uint)Bias;
    OutFloats[id.x] = UseOffset ? (float)id.x * Scale + Shift : (float)id.x * Scale - Shift;
}
