using System;
using SpriteSample.Shaders;

// Everything below is generated from Shaders/Sprite.hlsl.
Console.WriteLine($"constant buffer: {Sprite.Frame.Size} bytes, vertex stride: {Sprite.VSMain.Stride}");
Console.WriteLine($"vertex shader: {Sprite.VSMain.SpirV.Length} bytes of SPIR-V");
foreach (var binding in Sprite.Bindings.All)
{
    Console.WriteLine($"{binding.Name}: set {binding.Set}, binding {binding.Binding}, {binding.Kind}, used by {binding.Stages}");
}
