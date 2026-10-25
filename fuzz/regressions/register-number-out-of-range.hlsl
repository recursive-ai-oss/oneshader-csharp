// Binding, set and register numbers far beyond any device limit are rejected rather than overflowing when a backend adds to them.
struct PerDraw { float4 Tint; };
[[vk::push_constant]] ConstantBuffer<PerDraw> Draw;
[[vk::binding(2147483647, 0)]] StructuredBuffer<float4> Palette;

[shader("pixel")]
float4 main() : SV_Target0
{
    return Palette[0] * Draw.Tint;
}
