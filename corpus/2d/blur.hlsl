// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: separable gaussian blur. Run it twice, once per direction.

[[vk::binding(0, 0)]] cbuffer Blur
{
    float2 Direction; // (1,0) horizontal, (0,1) vertical, in texels
    int Radius;       // taps on each side of the centre
    float Sigma;      // standard deviation in texels
};

[[vk::binding(1, 0)]] Texture2D<float4> Source;
[[vk::binding(2, 0)]] SamplerState SourceSampler;

struct VertexOutput
{
    float4 Position : SV_Position;
    float2 Uv : TEXCOORD0;
};

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
    uint width, height;
    Source.GetDimensions(width, height);
    float2 texel = 1.0 / float2(width, height);

    float4 sum = 0.0;
    float weightSum = 0.0;
    float inverseTwoSigmaSquared = 0.5 / (Sigma * Sigma);
    for (int i = -Radius; i <= Radius; i++)
    {
        float weight = exp(-(float)(i * i) * inverseTwoSigmaSquared);
        sum += Source.SampleLevel(SourceSampler, input.Uv + Direction * texel * (float)i, 0.0) * weight;
        weightSum += weight;
    }
    return sum / weightSum;
}
