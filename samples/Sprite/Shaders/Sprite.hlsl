cbuffer Frame : register(b0) { float4x4 ViewProjection; };
Texture2D<float4> Atlas : register(t1);
SamplerState AtlasSampler : register(s2);

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
float4 PSMain(VSOut i) : SV_Target { return Atlas.Sample(AtlasSampler, i.Uv) * i.Color; }
