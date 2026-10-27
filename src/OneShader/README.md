# OneShader

**A HLSL compiler for .NET, written in C#, with no dependency beyond .NET itself.** Modern HLSL
(Shader Model 6 style) for vertex, pixel and compute shaders goes in; SPIR-V for Vulkan comes out,
with reflection: inputs and outputs, resource bindings, buffer layouts and thread-group sizes.
NativeAOT and trimming are supported.

Compiles to SPIR-V (Vulkan), Metal Shading Language and WGSL (WebGPU).

Most projects want [RecursiveAI.OneShader.Build](https://www.nuget.org/packages/RecursiveAI.OneShader.Build),
which compiles `*.hlsl` during `dotnet build` and generates C# bindings. This package is the
compiler itself, for compiling at run time (hot reload, tools) and for the support types the
generated code uses.

## Install

```sh
dotnet add package RecursiveAI.OneShader
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

## Pipeline layouts

`OneShader.Layouts` turns a shader into what a renderer creates, as plain data with no graphics API behind it: descriptor set
layouts and push-constant ranges for Vulkan, per-stage argument indices for Metal, bind group layouts for WebGPU, and vertex
input for all three. It reads the generated classes (no parsing) or a `ShaderReflection` (for shaders compiled at run time).

```csharp
using OneShader.Layouts;

// Built with the project: one call per target.
PipelineLayout pipeline = Shaders.Sprite.Pipeline;                    // vertex + pixel entry points of the file
var vulkan = pipeline.ToVulkan();                                     // SetLayouts[i].Bindings, PushConstantRanges
foreach (var set in vulkan.SetLayouts)
    foreach (var b in set.Bindings)
        Console.WriteLine($"set {set.Set} binding {b.Binding}: {b.DescriptorType} x{b.DescriptorCount} stages {b.StageFlags}");

var vertex = Shaders.Sprite.VSMain.Layout.Vertex!.ToVulkan(VertexStepMode.Instance);   // VkVertexInputBinding/AttributeDescription data

// Any combination, and conflicts come back as values:
if (!PipelineLayout.TryCreate([Quad.VSMain.Layout, Quad.PSBlur.Layout], out var blur, out var error))
    Console.WriteLine(error);

// Compiled at run time:
EntryLayout.TryCreate(result.Reflection!, "PSMain", out var ps, out _);
```

`PipelineLayout.TryCreate` merges entry points that run together: bindings at the same set and binding must agree on kind, count
and type and get the union of their stages; push-constant blocks of the same name and size are shared. It returns a
`LayoutError` (`ResourceConflict`, `DuplicateStage`, ...) with the message to show, never an exception. The three conversions:

- `ToVulkan(VulkanLayoutOptions?)`: a `DescriptorSetLayoutInfo` per set from 0 (unused sets are empty, as Vulkan needs them),
  and a `PushConstantRangeInfo` per block, using the numeric values of `VkDescriptorType`, `VkShaderStageFlagBits` and
  `VkFormat`. `DynamicBuffers` names buffers to declare as dynamic-offset descriptors.
- `TryToMetal(...)`: for each stage, the `[[buffer(n)]]`, `[[texture(n)]]` and `[[sampler(n)]]` index of every resource (an array
  of N takes N indices), the buffer index to pass to `setVertexBytes`/`setFragmentBytes`/`setBytes`, and `MetalStageLayout.TryFind(set, binding)`
  to go from a Vulkan-style slot to the index. `VertexLayout.ToMetal(bufferIndex, step)` gives the `MTLVertexDescriptor` data
  (default buffer index 30; pass the generated `MslVertexBufferIndex`). Fails with `MissingTarget` if `Msl` was not built.
- `TryToWebGpu(..., WebGpuLayoutOptions?)`: `GPUBindGroupLayoutEntry` data (`ToWebGpuString()` gives the API strings), with the
  push-constant block as a uniform buffer entry at the group and binding the compiler reserved, and `VertexLayout.ToWebGpu(step)` for
  `GPUVertexBufferLayout`. What WebGPU cannot express (resource arrays, typed buffers, 1D array textures, storage textures without
  `[[vk::image_format]]`) is an `Unsupported` error that says what to change. Reflection does not record whether a texture is a
  depth texture sampled with a comparison sampler, or how a storage texture is accessed, so those come from `WebGpuLayoutOptions`
  (`DepthTextures`, `StorageAccess`, ...); defaults are `float` textures and `write-only` storage textures.

Creating layouts allocates; do it when a shader loads, not per frame.

## What works

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
- Unbounded resource arrays and `WaveMatch`/`WaveMultiPrefix*` are not supported. Wave and quad intrinsics work; a shader that uses them needs subgroup support on the device (`EntryPointInfo.SubgroupOperations`).

## License

MIT OR Apache-2.0, at your option. Copyright © Recursive AI, LLC.
