// HLSL accepts normalize/reflect on scalars; WGSL has them for vectors only.
cbuffer P { float s; float t; };

[shader("pixel")]
float4 main() : SV_Target
{
    return float4(normalize(s), reflect(s, t), faceforward(s, t, 1.0), 1.0);
}
