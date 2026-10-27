Texture2D<float4> T;
Texture3D<float4> V;
SamplerState S;
float4 main(float3 uv : UV) : SV_Target { return T.Sample(S, uv.xy, 1) + V.SampleLevel(S, uv, 0, 0); }
