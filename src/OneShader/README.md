# OneShader

**A HLSL compiler for .NET, written in C#, with no dependency beyond .NET itself.** Modern HLSL
(Shader Model 6 style) for vertex, pixel and compute shaders goes in; SPIR-V for Vulkan comes out,
with reflection: inputs and outputs, resource bindings, buffer layouts and thread-group sizes.
NativeAOT and trimming are supported.

**Preview.** Version 0.1 targets Vulkan (SPIR-V). The API may still change before 1.0.

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

## What works in 0.1

- Vertex, pixel and compute shaders in modern HLSL (HLSL 2021): structs, functions, constant
  buffers, textures and samplers, structured and byte-address buffers, storage images,
  groupshared memory, barriers and atomics, and the preprocessor (`#include`, macros, `#if`).
- SPIR-V for Vulkan 1.2, validated with `spirv-val`, and checked by running each shader in its
  test corpus on a GPU against the same shader compiled by DXC.
- Buffer layouts and descriptor bindings follow the conventions DXC uses for Vulkan.

## Known limits

- SPIR-V only. Metal (MSL) and WebGPU (WGSL) output are planned; asking for them returns
  diagnostic OSH0002.
- Resources are single (`Count == 1`); resource arrays are not supported yet.
- HLSL templates, struct member functions, operator overloads and wave intrinsics report
  "not supported yet".

## License

MIT OR Apache-2.0, at your option. Copyright © Recursive AI, LLC.
