// SPDX-License-Identifier: MIT OR Apache-2.0
// probe: PSMain ps
// The faceforward intrinsic is not declared.
float4 PSMain(float3 v : TEXCOORD0, float3 n : TEXCOORD1, float3 reference : TEXCOORD2) : SV_Target0
{
    return float4(faceforward(n, v, reference), 1.0);
}
