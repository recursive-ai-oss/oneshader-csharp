// SPDX-License-Identifier: MIT OR Apache-2.0
// probe: PSMain ps
// A constant buffer and one of its members share a name; DXC accepts it.
[[vk::binding(0, 0)]] cbuffer Lights
{
    float4 Lights[4];
    uint Count;
};

float4 PSMain() : SV_Target0
{
    return Lights[Count & 3];
}
