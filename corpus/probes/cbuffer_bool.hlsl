// SPDX-License-Identifier: MIT OR Apache-2.0
// probe: PSMain ps
// A bool member of a constant buffer: DXC stores it as a 32-bit integer; the candidate emits OpTypeBool in uniform storage.
[[vk::binding(0, 0)]] cbuffer Flags
{
    float4 Tint;
    bool Enabled;
};

float4 PSMain(float4 color : TEXCOORD0) : SV_Target0
{
    return Enabled ? color * Tint : color;
}
