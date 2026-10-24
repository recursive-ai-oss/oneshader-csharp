// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: fullscreen triangle and texture copy.

[[vk::binding(0, 0)]] Texture2D<float4> Source;
[[vk::binding(1, 0)]] SamplerState SourceSampler;

struct VertexOutput
{
    float4 Position : SV_Position;
    float2 Uv : TEXCOORD0;
};

// Three vertices cover the whole target: (0,0) (2,0) (0,2) in uv space, no vertex buffer.
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
    return Source.Sample(SourceSampler, input.Uv);
}
