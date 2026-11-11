// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: float constants at the ends of the f32 range, written to a buffer as bits. The largest finite
// float must reach WGSL as an in-range literal (its shortest round-trip decimal is above the maximum as a real number).

[[vk::binding(0, 0)]] StructuredBuffer<uint> Seed;   // float bits of four values
[[vk::binding(1, 0)]] RWStructuredBuffer<uint> Bits;
[[vk::binding(2, 0)]] RWStructuredBuffer<float> Values;   // the same kind of constants stored as floats

[shader("compute")]
[numthreads(1, 1, 1)]
void CSMain()
{
    // A running maximum that starts at the most negative finite float.
    float best = -3.4028234663852886e38;
    for (uint i = 0; i < 4; i++)
    {
        best = max(best, asfloat(Seed[i]));
    }

    Bits[0] = asuint(best);
    Bits[1] = asuint(-asfloat(0x7f7fffffu));
    Bits[2] = asuint(asfloat(0x7f7fffffu));
    Bits[3] = asuint(3.4028234663852886e38);
    Bits[4] = asuint(-3.4028234663852886e38);
    Bits[5] = asuint(asfloat(0x7f7ffffeu));   // next below the maximum
    Bits[6] = asuint(1.17549435e-38);          // smallest normal
    Bits[7] = asuint(-1.17549435e-38);
    Bits[8] = asuint(-0.0);
    Bits[9] = asuint(16777217.0);              // rounds to 16777216
    Bits[10] = asuint(0.1);
    Bits[11] = asuint(asfloat(0x4b7fffffu));   // 16777215

    Values[0] = -asfloat(0x7f7fffffu);
    Values[1] = 3.4028234663852885981e38;
    Values[2] = -3.4028234663852886e38;
    Values[3] = 1.17549435e-38;
    Values[4] = -0.0;
    Values[5] = 16777217.0;
}
