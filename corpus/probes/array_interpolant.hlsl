// SPDX-License-Identifier: MIT OR Apache-2.0
// probe: VSMain vs
// An array of float4 as a vertex output (per-cascade shadow coordinates); DXC assigns consecutive locations.
struct VertexOutput
{
    float4 Position : SV_Position;
    float4 ShadowUv[4] : TEXCOORD0;
};

VertexOutput VSMain(float3 position : POSITION)
{
    VertexOutput output;
    output.Position = float4(position, 1.0);
    [unroll]
    for (int i = 0; i < 4; ++i)
    {
        output.ShadowUv[i] = float4(position * i, 1.0);
    }
    return output;
}
