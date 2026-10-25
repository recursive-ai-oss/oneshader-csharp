// SPDX-License-Identifier: MIT OR Apache-2.0
// probe: VSMain vs
// SV_ClipDistance outputs: the compiler throws instead of compiling or diagnosing.
struct VertexOutput
{
    float4 Position : SV_Position;
    float Clip0 : SV_ClipDistance0;
    float Clip1 : SV_ClipDistance1;
};

VertexOutput VSMain(float3 position : POSITION)
{
    VertexOutput output;
    output.Position = float4(position, 1.0);
    output.Clip0 = position.x;
    output.Clip1 = position.y;
    return output;
}
