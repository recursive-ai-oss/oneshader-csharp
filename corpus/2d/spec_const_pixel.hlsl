// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: a fullscreen triangle whose pixel shader branches on a specialization constant.

struct VertexOutput
{
    float4 Position : SV_Position;
    float2 Uv : TEXCOORD0;
};

[[vk::constant_id(0)]] const bool Swap = false;
[[vk::constant_id(1)]] const float Strength = 0.25;
[[vk::constant_id(2)]] const int Bands = 4;

[shader("vertex")]
VertexOutput VSMain(uint vertexId : SV_VertexID)
{
    VertexOutput output;
    output.Uv = float2((vertexId << 1) & 2, vertexId & 2);
    output.Position = float4(output.Uv * 2.0 - 1.0, 0.0, 1.0);
    return output;
}

[shader("pixel")]
float4 PSMain(VertexOutput input) : SV_Target0
{
    float3 c = float3(input.Uv, 0.5);
    if (Swap)
    {
        c = float3(c.z, c.y, c.x) * Strength + float3(1.0 - Strength, 0.0, 0.0);
    }

    // Quantized into Bands steps, so the band count is visible in the picture.
    float steps = (float)Bands;
    c.x = floor(c.x * steps) / steps;
    return float4(c, 1.0);
}
