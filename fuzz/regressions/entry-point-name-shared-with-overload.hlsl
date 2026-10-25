// An entry point may share its name with an unused overload; the module must not hold two functions with the same name.
float4 VSMain() { return float4(0, 0, 0, 1); }

[shader("pixel")]
float4 VSMain(float2 uv : TEXCOORD0) : SV_Target0
{
    return float4(uv, 0, 1);
}
