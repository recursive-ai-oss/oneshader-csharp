# OneShader.Build

**HLSL shaders compiled during `dotnet build`, with C# bindings generated from them.** Put
`*.hlsl` files in a project and reference this package: each shader is compiled to SPIR-V by
[OneShader](https://www.nuget.org/packages/RecursiveAI.OneShader), a HLSL compiler written in C#
with no dependency beyond .NET itself. Shader errors are build errors on the HLSL line, and
constant buffers, vertex inputs and resource bindings become C# types and constants laid out to
match the compiled shader, so a CPU/GPU mismatch fails the build instead of the frame.

Vulkan (SPIR-V) is the default target; Metal (MSL) and WebGPU (WGSL) are opt-in with `OneShaderTargets`.

## Install

```sh
dotnet add package RecursiveAI.OneShader.Build
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
- for a `[[vk::push_constant]]` block: a blittable `PushConstants` struct and `Bindings.PushConstants` with its `Size`, the
  stages for the `VkPushConstantRange`, and the Metal index and WebGPU group and binding of the targets you selected;
- per entry point: `Layout`, its resources, push-constant block and vertex input as plain data, and per file `Pipeline` (vertex + pixel
  or one compute entry point) with `ToVulkan()`, `TryToMetal()` and `TryToWebGpu()` giving descriptor set layouts, push-constant
  ranges, Metal argument indices and WebGPU bind group layouts (see the `RecursiveAI.OneShader` readme, "Pipeline layouts");
- per entry point: stage, entry name, the SPIR-V bytes; for vertex shaders a `Vertex` struct,
  `Stride` and `Inputs`; for compute shaders the thread-group size. Entry points also carry the
  MSL and WGSL sources when those targets are selected (next section).

## Choosing targets

```xml
<PropertyGroup>
  <OneShaderTargets>SpirV;Msl;Wgsl</OneShaderTargets>   <!-- default: SpirV -->
</PropertyGroup>
```

`OneShaderTargets` is a `;`-list of `SpirV`, `Msl` and `Wgsl` (case-insensitive). A `OneShader` item
can override it with `Targets` metadata. The default stays SPIR-V only because some valid shaders
are refused by a target (for example clip distances or resource arrays on WGSL, OSH5202); a
refusal is a build error on the HLSL line, `Shader.hlsl(12,5): error OSH5202: ...`, only when that
target is selected, and a target nobody asked for costs no build time.

With a target selected, every entry point class gains (in addition to `SpirV`):

| Member | Target | Meaning |
|---|---|---|
| `Msl` | Msl | `ReadOnlySpan<byte>`, UTF-8 Metal Shading Language source |
| `MslFunctionName` | Msl | the function to pass to `newFunction(name:)`; differs from `EntryPoint` when MSL reserves the name (`main` becomes `main0`) |
| `MslVertexBufferIndex` | Msl | vertex shaders with inputs: the Metal buffer index the interleaved vertex buffer (`Stride`, `Inputs`) is bound at |
| `Wgsl` | Wgsl | `ReadOnlySpan<byte>`, UTF-8 WGSL source |
| `WgslEntryPoint` | Wgsl | the entry function name in the WGSL source |

Bindings carry the numbers of every selected target. `Set` and `Binding` are Vulkan's (WGSL uses
the same numbers as `@group`/`@binding`); with Msl selected, each resource class also has
`MslIndex` (and `MslCounterIndex` for Append/Consume counters), the Metal argument index counted
within the resource's class (buffer, texture, sampler) in declaration order, and
`ResourceBinding.MetalIndex` in `Bindings.All` holds the same number (`ResourceBinding.NoMetalIndex`
when Msl is not selected). With only Msl selected, `Set` is 0 and `Binding` repeats the Metal index.
Constant-buffer and vertex structs are the same for every target: all backends use one byte layout.

Support types (`ShaderStage`, `ResourceKind`, `VertexFormat`, `VertexAttribute`,
`ResourceBinding`) come from `RecursiveAI.OneShader`, so a rendering layer in another assembly can
take them. When only these are used, trimming removes the compiler from a NativeAOT app.

Item metadata: `EntryPoints`, `Defines`, `IncludeDirectories`, `Namespace`, `ClassName`, `Targets`.
Properties: `EnableDefaultOneShaderItems`, `OneShaderNamespace`, `OneShaderTargets`,
`OneShaderDefines`, `OneShaderIncludeDirectories`. Shaders are recompiled only when they, a file they include, or the
options change; the output is deterministic.

## What works

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
- Metal Shading Language and WGSL embedded next to the SPIR-V when selected with `OneShaderTargets`, with
  their entry names and Metal binding indices in the generated code; the same corpus runs on Metal and in a
  browser on WebGPU against the DXC reference.
- Push constants (`[[vk::push_constant]]`) on every target: a Vulkan push-constant block, a Metal `constant T&` buffer
  argument, a WebGPU uniform buffer; one set of bytes for all three.
- Hot reload: generated classes carry the reflection they were built from, and
  `InterfaceCompatibility.Compare` tells whether a shader recompiled at run time still matches
  them (see the `RecursiveAI.OneShader` package).

## Known limits

- Clip-space Y points up in Metal and WebGPU and down in Vulkan; the shader is not adjusted, so flip with the viewport.
- Unbounded resource arrays and wave intrinsics are not supported.
- `dotnet build` only (not Visual Studio's .NET Framework MSBuild).

## License

MIT OR Apache-2.0, at your option. Copyright © Recursive AI, LLC.
