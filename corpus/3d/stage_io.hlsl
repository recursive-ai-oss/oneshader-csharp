// SPDX-License-Identifier: MIT OR Apache-2.0
// Written for this repository: stage variables that are arrays and matrices, clip distances, a comparison gather and
// a level-of-detail query. A vertex buffer supplies a float2x2 (two attributes), the vertex stage passes it on with an
// array of float4 and a float2x2 to the pixel stage, and clips the right and top edges.

[[vk::binding(0, 0)]] Texture2DArray<float> Shadow;
[[vk::binding(1, 0)]] SamplerComparisonState ShadowSampler;
[[vk::binding(2, 0)]] Texture2D<float4> Photo;
[[vk::binding(3, 0)]] SamplerState PhotoSampler;

struct VertexInput
{
    float2 Position : POSITION;
    float2x2 Skew : SKEW;
};

struct VertexOutput
{
    float4 Position : SV_Position;
    float Clip[2] : SV_ClipDistance;
    float4 Tint[2] : TEXCOORD0;
    float2x2 Rot : TEXCOORD2;
    float2 Uv : TEXCOORD4;
};

[shader("vertex")]
VertexOutput VSMain(VertexInput input)
{
    VertexOutput output;
    float2 uv = input.Position * 0.5 + 0.5;
    output.Position = float4(input.Position, 0.0, 1.0);
    output.Uv = uv;
    output.Clip[0] = 0.9 - uv.x;
    output.Clip[1] = 0.95 - uv.y;
    output.Tint[0] = float4(uv, 0.0, 1.0);
    output.Tint[1] = float4(1.0 - uv, 0.5, 1.0);
    output.Rot = input.Skew;
    return output;
}

struct PixelInput
{
    float4 Position : SV_Position;
    float4 Tint[2] : TEXCOORD0;
    float2x2 Rot : TEXCOORD2;
    float2 Uv : TEXCOORD4;
};

[shader("pixel")]
float4 PSMain(PixelInput input) : SV_Target0
{
    float2 r = mul(input.Uv - 0.5, input.Rot);
    float4 g = Shadow.GatherCmp(ShadowSampler, float3(input.Uv, 1.0), 0.5 + r.x * 0.2);
    float lod = Photo.CalculateLevelOfDetail(PhotoSampler, input.Uv * 3.0);
    float4 photo = Photo.Sample(PhotoSampler, input.Uv);
    float4 tint = lerp(input.Tint[0], input.Tint[1], 0.5 + r.y);
    return float4(g.x * tint.x + g.y * 0.25, g.z * tint.y + g.w * 0.25, lod * 0.5 + photo.b * 0.25, 1.0);
}
