// SPDX-License-Identifier: MIT OR Apache-2.0
// probe: VSMain vs
// SV_ClipDistance as an array member; arrays are rejected as stage outputs.
struct VertexOutput
{
    float4 Position : SV_Position;
    float Clip[2] : SV_ClipDistance;
};

VertexOutput VSMain(float3 position : POSITION)
{
    VertexOutput output;
    output.Position = float4(position, 1.0);
    output.Clip[0] = position.x;
    output.Clip[1] = position.y;
    return output;
}
