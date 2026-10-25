# OneShader.Build

**HLSL shaders compiled during `dotnet build`, with C# bindings generated from them.** Put
`*.hlsl` files in a project and reference this package: each shader is compiled to SPIR-V by
[OneShader](https://www.nuget.org/packages/RecursiveAI.OneShader), a HLSL compiler written in C#
with no dependency beyond .NET itself. Shader errors are build errors on the HLSL line, and
constant buffers, vertex inputs and resource bindings become C# types and constants laid out to
match the compiled shader, so a CPU/GPU mismatch fails the build instead of the frame.

**Preview.** Version 0.2 targets Vulkan (SPIR-V). The API and the generated code may still
change before 1.0.

## Install

```sh
dotnet add package RecursiveAI.OneShader.Build --prerelease
```

Target framework `net10.0`. Build with the .NET SDK (`dotnet build`); Visual Studio's .NET
Framework MSBuild cannot load the build task.

## Quick start

`Shaders/Sprite.hlsl`:

```hlsl
cbuffer Frame : register(b0) { float4x4 ViewProjection; };
[[vk::binding(1)]] Texture2D<float4> Atlas;
[[vk::binding(2)]] SamplerState AtlasSampler;

struct VSIn
{
    float2 Position : POSITION;
    float2 Uv : TEXCOORD0;
    [[oneshader::format("unorm8x4")]] float4 Color : COLOR0;
};
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
```

After `dotnet build`, with no reflection and no file I/O at run time:

```csharp
using MyGame.Shaders;   // <RootNamespace>.Shaders

ReadOnlySpan<byte> vs = Sprite.VSMain.SpirV;        // embedded SPIR-V
ReadOnlySpan<byte> ps = Sprite.PSMain.SpirV;
uint stride = Sprite.VSMain.Stride;                 // 20
foreach (var a in Sprite.VSMain.Inputs) { /* a.Location, a.Format (Float32x2, Unorm8x4, ...), a.Offset */ }

var frame = new Sprite.Frame { ViewProjection = camera };  // explicit layout, Size = 64
uint set = Sprite.Bindings.Atlas.Set, binding = Sprite.Bindings.Atlas.Binding;
```

A mistake in the shader is an ordinary build error:

```
Shaders/Sprite.hlsl(24,45): error OSH0300: 'Atlass' is not declared. Did you mean 'Atlas'?
```

## What is generated

One static class per HLSL file (named after the file, in PascalCase), containing:

- one blittable struct per constant buffer, with field offsets equal to the compiled shader's;
  matrices are `Matrix4x4` members where element (i, j) is HLSL's `M[i][j]`;
- a struct per structured-buffer element type;
- `Bindings`: set, binding, count and kind of every resource, as constants;
- per entry point: stage, entry name, the SPIR-V bytes; for vertex shaders a `Vertex` struct,
  `Stride` and `Inputs`; for compute shaders the thread-group size.

Support types (`ShaderStage`, `ResourceKind`, `VertexFormat`, `VertexAttribute`,
`ResourceBinding`) come from `RecursiveAI.OneShader`, so a rendering layer in another assembly can
take them. When only these are used, trimming removes the compiler from a NativeAOT app.

Item metadata: `EntryPoints`, `Defines`, `IncludeDirectories`, `Namespace`, `ClassName`.
Properties: `EnableDefaultOneShaderItems`, `OneShaderNamespace`, `OneShaderDefines`,
`OneShaderIncludeDirectories`. Shaders are recompiled only when they, a file they include, or the
options change; the output is deterministic.

## What works in 0.2

- Vertex, pixel and compute shaders in modern HLSL (Shader Model 6 style, HLSL 2021): structs,
  functions, templates, member functions, inheritance and operator overloads, constant buffers,
  textures and samplers (including comparison sampling, gathers and LOD queries), structured,
  byte-address and Append/Consume buffers, storage images, bounded resource arrays (constant and
  run-time indexing), groupshared memory, barriers and atomics, and the preprocessor.
- SPIR-V for Vulkan 1.2, optimized (inlining, SSA, constant folding, dead-code removal), validated
  with `spirv-val`, and checked by running every shader in its test corpus on a GPU against the
  same shader compiled by DXC: 2D sprites, text and UI, a PBR forward renderer with clustered
  lights, cascaded shadows, SSAO, bloom, tone mapping, image-based lighting and skinning, and
  compute culling.
- Buffer layouts and descriptor bindings follow the conventions DXC uses for Vulkan, so existing
  shaders keep their layouts.
- Hot reload: generated classes carry the reflection they were built from, and
  `InterfaceCompatibility.Compare` tells whether a shader recompiled at run time still matches
  them (see the `RecursiveAI.OneShader` package).

## Known limits

- The generated bindings and embedded blobs cover SPIR-V; Metal (MSL) and WebGPU (WGSL) are available from the compiler API only.
- Unbounded resource arrays and wave intrinsics are not supported.
- `dotnet build` only (not Visual Studio's .NET Framework MSBuild).

## License

MIT OR Apache-2.0, at your option. Copyright © Recursive AI, LLC.
