// A sprite material with two keyword axes. The build compiles it once per combination, defining
//   ALPHA_TEST            (a flag: defined as 1, or not defined at all)
//   LIGHTING              (the value: NONE, LAMBERT or PBR) and LIGHTING_NONE / LIGHTING_LAMBERT / LIGHTING_PBR as 1.
cbuffer Frame : register(b0) { float4x4 ViewProjection; float4 Tint; };
Texture2D<float4> Atlas : register(t1);
SamplerState AtlasSampler : register(s2);
#if LIGHTING_LAMBERT || LIGHTING_PBR
Texture2D<float4> NormalMap : register(t3);
#endif

struct VSIn  { float2 Position : POSITION; float2 Uv : TEXCOORD0; float4 Color : COLOR0; };
struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; float4 Color : COLOR0; };

[shader("vertex")]
VSOut VSMain(VSIn i)
{
    VSOut o;
    o.Position = mul(ViewProjection, float4(i.Position, 0, 1));
    o.Uv = i.Uv;
    o.Color = i.Color;
    return o;
}

[shader("pixel")]
float4 PSMain(VSOut i) : SV_Target
{
    float4 c = Atlas.Sample(AtlasSampler, i.Uv) * i.Color * Tint;
#if ALPHA_TEST
    clip(c.a - 0.5);
#endif
#if LIGHTING_LAMBERT
    float3 n = NormalMap.Sample(AtlasSampler, i.Uv).xyz * 2.0 - 1.0;
    c.rgb *= saturate(dot(n, normalize(float3(0.3, 0.5, 0.8))));
#elif LIGHTING_PBR
    float3 n = NormalMap.Sample(AtlasSampler, i.Uv).xyz * 2.0 - 1.0;
    float3 l = normalize(float3(0.3, 0.5, 0.8));
    float d = saturate(dot(n, l));
    c.rgb = c.rgb * d + pow(d, 16.0) * 0.04;
#endif
    return c;
}
