// A conditional whose result is a one-component vector or a struct selects like any other value.
struct S { float a; float2 b; };
cbuffer Flags { float1 Tint; bool Enabled; float4 V; };

[shader("pixel")]
float1 main(float4 color : TEXCOORD0) : SV_Target0
{
    S x = { 1, float2(2, 3) };
    S y = { 4, float2(5, 6) };
    S z = Enabled ? x : y;
    float1 q = Enabled ? V.x : Tint;
    return Enabled ? color * Tint : z.a + q;
}
