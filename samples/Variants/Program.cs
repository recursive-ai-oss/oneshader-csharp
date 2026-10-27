using System;
using VariantsSample.Shaders;

// Everything below is generated from Shaders/Material.hlsl.
Console.WriteLine($"variants: {Material.Variants.Count}");
foreach (var variant in Material.Variants.All)
{
    Console.WriteLine($"{variant.Name}: {variant.Defines} -> vertex {variant.VSMain.SpirV.Length} bytes of SPIR-V, pixel {variant.PSMain.SpirV.Length} bytes, {variant.PSMain.Wgsl.Length} bytes of WGSL, {variant.Bindings.Length} bindings");
}

// A typed key picks one combination, without allocating.
var pbrCutout = Material.Variants.Get(new Material.Key(alphaTest: true, lighting: Material.LightingValue.Pbr));
Console.WriteLine($"picked: {pbrCutout.Name}, normal map bound: {HasBinding(pbrCutout, "NormalMap")}");
var plain = Material.Variants.Get(new Material.Key(alphaTest: false, lighting: Material.LightingValue.None));
Console.WriteLine($"picked: {plain.Name}, normal map bound: {HasBinding(plain, "NormalMap")}");

// The constant buffer struct is the same for every variant and is generated once.
Console.WriteLine($"constant buffer: {Material.Frame.Size} bytes");

static bool HasBinding(Material.Variant variant, string name)
{
    foreach (var b in variant.Bindings)
    {
        if (b.Name == name)
        {
            return true;
        }
    }

    return false;
}
