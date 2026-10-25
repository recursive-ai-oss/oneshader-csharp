// SPDX-License-Identifier: MIT OR Apache-2.0
// probe: PSMain ps
// A float3x3 tangent frame as a pixel input; DXC assigns one location per row.
struct PixelInput
{
    float4 Position : SV_Position;
    float3x3 TangentFrame : TEXCOORD1;
    float2 Uv : TEXCOORD4;
};

float4 PSMain(PixelInput input) : SV_Target0
{
    return float4(mul(float3(input.Uv, 1.0), input.TangentFrame), 1.0);
}
