using System;
using SpriteSample.Shaders;

// Everything below is generated from Shaders/Sprite.hlsl.
Console.WriteLine($"constant buffer: {Sprite.Frame.Size} bytes, vertex stride: {Sprite.VSMain.Stride}");
Console.WriteLine($"vertex shader: {Sprite.VSMain.SpirV.Length} bytes of SPIR-V");
foreach (var binding in Sprite.Bindings.All)
{
    Console.WriteLine($"{binding.Name}: set {binding.Set}, binding {binding.Binding}, {binding.Kind}, used by {binding.Stages}");
}

// The same shader as a pipeline layout: what a renderer creates, as plain data.
var vulkan = Sprite.Pipeline.ToVulkan();
foreach (var set in vulkan.SetLayouts)
{
    foreach (var b in set.Bindings)
    {
        Console.WriteLine($"vulkan set {set.Set} binding {b.Binding}: {b.DescriptorType} x{b.DescriptorCount}, stages {b.StageFlags}");
    }
}

var vertexInput = Sprite.VSMain.Layout.Vertex!.ToVulkan();
Console.WriteLine($"vertex buffer: stride {vertexInput.Bindings[0].Stride}, {vertexInput.Attributes.Count} attributes");
