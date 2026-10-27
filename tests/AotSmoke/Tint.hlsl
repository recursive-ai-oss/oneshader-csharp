// Compiled at build time in four variants (FOG on/off x QUALITY low/high); the smoke test looks one up.
cbuffer Params : register(b0) { float4 Color; };

[shader("pixel")]
float4 PS() : SV_Target
{
#if FOG
    float4 c = Color * 0.5;
#else
    float4 c = Color;
#endif
#if QUALITY_HIGH
    c = c * c;
#endif
    return c;
}
