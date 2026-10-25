// SPDX-License-Identifier: MIT OR Apache-2.0
// probe: VSMain vs
// SV_CullDistance output: the compiler throws instead of compiling or diagnosing.
struct VertexOutput
{
    float4 Position : SV_Position;
    float Cull : SV_CullDistance0;
};

VertexOutput VSMain(float3 position : POSITION)
{
    VertexOutput output;
    output.Position = float4(position, 1.0);
    output.Cull = position.x;
    return output;
}
