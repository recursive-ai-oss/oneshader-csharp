# OneShader

**A HLSL compiler for .NET, written in C#, with no dependency beyond .NET itself.** Modern HLSL
(Shader Model 6 style) for vertex, pixel and compute shaders goes in; SPIR-V for Vulkan comes out,
with reflection: inputs and outputs, resource bindings, buffer layouts and thread-group sizes.
NativeAOT and trimming are supported.

**Preview.** Version 0.3 compiles to SPIR-V (Vulkan), Metal Shading Language and WGSL (WebGPU). The API may still change before 1.0.

Most projects want [RecursiveAI.OneShader.Build](https://www.nuget.org/packages/RecursiveAI.OneShader.Build),
which compiles `*.hlsl` during `dotnet build` and generates C# bindings. This package is the
compiler itself, for compiling at run time (hot reload, tools) and for the support types the
generated code uses.

## Install

```sh
dotnet add package RecursiveAI.OneShader --prerelease
```

Target framework `net10.0`. The namespace is `OneShader`.

## Quick start

```csharp
using OneShader;

string source = File.ReadAllText("Shaders/Blur.hlsl");
CompileResult result = ShaderCompiler.Compile(source, "Shaders/Blur.hlsl", new CompileOptions
{
    Defines = [new("RADIUS", "4")],
    IncludeDirectories = ["Shaders/Common"],
});

foreach (var d in result.Diagnostics)
    Console.WriteLine(d);              // Shaders/Blur.hlsl(12,5): error OSH0300: ...

if (result.Success)
{
    ReadOnlySpan<byte> spirv = result.Find("PSMain", CompileTarget.SpirV)!.Data;
    var reflection = result.Reflection!;   // resources, bindings, buffer layouts, entry points
}
```

Errors are returned as diagnostics with file, line and column, never thrown. Includes can come from
disk or from memory through `CompileOptions.IncludeResolver`. Output is deterministic.

## Hot reload

During development, watch the HLSL file, recompile it with the runtime API and swap the new SPIR-V in, as long as the
interface the C# was built against has not changed. Projects using the build package get that interface as
`Shaders.Sprite.Reflection` (generated from `Sprite.hlsl`).

```csharp
CompileResult result = ShaderCompiler.CompileFile("Shaders/Sprite.hlsl");   // diagnostics, not exceptions
watcher.Watch(result.Files);                                              // the file and its #includes, even after an error

if (!result.Success) { Print(result.Diagnostics); return; }                // keep running the last good shader

CompatibilityReport report = InterfaceCompatibility.Compare(Shaders.Sprite.Reflection, result.Reflection!);
if (report.IsCompatible)
    renderer.Replace(result.Find("PSMain", CompileTarget.SpirV)!.Data);
else
    Console.WriteLine(report);   // "Frame.Tint moved from offset 64 to 80" ... "Rebuild the project ..."
```

Compatible means everything the generated C# relies on is unchanged: constant-buffer and structured-buffer layouts
(member offsets, types, array and matrix strides, sizes), vertex input locations, formats, offsets and stride, resource
set, binding, kind, array size and stage visibility, the entry points present and their stages, and compute thread-group
sizes. Edits to function bodies, new entry points, and resources that become visible to fewer stages are compatible.
`ShaderCompiler.Compile` can be called from several threads at once. `samples/HotReload` is a complete console loop.

## What works in 0.3

- Vertex, pixel and compute shaders in modern HLSL (HLSL 2021): structs, functions, constant
  buffers, textures and samplers, structured and byte-address buffers, storage images,
  groupshared memory, barriers and atomics, and the preprocessor (`#include`, macros, `#if`).
- SPIR-V for Vulkan 1.2, validated with `spirv-val`, and checked by running each shader in its
  test corpus on a GPU against the same shader compiled by DXC.
- Buffer layouts and descriptor bindings follow the conventions DXC uses for Vulkan.
- Metal Shading Language (`CompileTarget.Msl`, Metal 2.4+) and WGSL (`CompileTarget.Wgsl`) with the same buffer byte
  layouts as the SPIR-V, checked with Apple's `metal` compiler and `naga`, and by running the test corpus on Metal and in a
  browser on WebGPU against the DXC reference.
- Push constants (`[[vk::push_constant]]` on a struct global or `ConstantBuffer<T>`): a `PushConstant` block in SPIR-V with
  DXC's offsets, a `constant T&` argument in MSL, a uniform buffer in WGSL; `ShaderReflection.PushConstants` lists the
  layout, the stages and where each target takes the bytes.

## Known limits

- SPIR-V, Metal (MSL) and WebGPU (WGSL) output. Limits are listed in `docs/msl-backend.md` and
  `docs/wgsl-backend.md`.
- Unbounded resource arrays and wave intrinsics are not supported.

## License

MIT OR Apache-2.0, at your option. Copyright © Recursive AI, LLC.
