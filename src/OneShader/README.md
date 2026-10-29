# OneShader

**A HLSL compiler for .NET, written in C#, with no dependency beyond .NET itself.** Modern HLSL
(Shader Model 6 style) for vertex, pixel and compute shaders goes in; SPIR-V for Vulkan, Metal
Shading Language and WGSL for WebGPU come out, with reflection: inputs and outputs, resource bindings,
buffer layouts and thread-group sizes. NativeAOT and trimming are supported.

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

## Compile cache

`ShaderCompileCache` answers a compile from memory when the same inputs were compiled before, for hosts that build many
variants of the same shaders (hot reload, tools, material systems). A result is reused when the source text, path,
every option (targets, defines, include directories, entry points, matrix packing, 16-bit types, optimization, names) and the
content of every include the earlier compile read are unchanged; includes are looked up again on each call, so an edited
file, or a new file earlier on the search path, makes the next call compile.

```csharp
var cache = new ShaderCompileCache(maxEntries: 256);          // thread-safe; least recently used results are dropped
CompileResult a = cache.Compile(source, "Material.hlsl", new CompileOptions { Defines = [new("ALPHA_TEST", null)] });
CompileResult b = cache.CompileFile("Shaders/Material.hlsl", options);   // reads the file every call
Console.WriteLine($"{cache.Hits} hits, {cache.Misses} compiles"); cache.Clear();
```

Callers asking for one key at the same time share one compile; different keys compile in parallel. Results (errors included) are shared
objects, so treat them as read-only; they are the bytes a direct `ShaderCompiler.Compile` gives. A cancelled call caches nothing.

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

Specialization constants (`[[vk::constant_id(N)]] const T Name = default;`) are pipeline-time inputs on every target. `SpecializationValues.For(entryLayout)`
holds one value per constant the entry point reads (defaults until you `Set` them) and converts to `VkSpecializationInfo` contents (`ToVulkan()`), Metal
function-constant values (`ToMetal()`) and WebGPU's `constants` record (`ToWebGpu()`); the generated `Specialization` struct does the same by field. To fix
values at compile time instead, pass `CompileOptions.SpecializationOverrides`.

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

One layout for many pipelines:

```csharp
// A material system shares one layout across ten forward entry points and three post-effect pixel shaders (several per stage).
if (!PipelineLayout.TryCreateUnion(allEntries, out var shared, out var error))
    Console.WriteLine(error);     // "Set 1 binding 3 is 'Albedo' (SampledTexture, ...) in entry point 'PsTex' and 'Params' (ConstantBuffer, ...) in entry point 'PsTint'. ..."

// A bind group made once has to fit pipelines that use different stages:
var vk = shared.ToVulkan(new VulkanLayoutOptions
{
    WidenSets = new Dictionary<uint, ShaderStages> { [0] = ShaderStages.Vertex | ShaderStages.Pixel },
    DynamicBindings = [(0u, 1u)],                      // by slot; DynamicBuffers = ["Frame"] does it by name
});
var web = shared.TryToWebGpu(out var wl, out _, new WebGpuLayoutOptions { WidenGroups = ..., DynamicBuffers = ["Frame"] });

// The engine stores the colour as unorm8x4 at its own offset in a 48-byte vertex:
var overrides = new VertexLayoutOverrides { Stride = 48, Attributes = new Dictionary<uint, VertexAttributeOverride> { [4] = new(VertexFormat.Unorm8x4, 44) } };
if (Quad.VSMain.Layout.TryWithVertexOverrides(overrides, out var vs, out error))   // then use vs in TryCreate/TryCreateUnion
    Console.WriteLine(vs!.Vertex!.ToMetal().Attributes[4]);                          // also ToVulkan() and ToWebGpu()
```

`TryCreateUnion` merges resources at one set and binding that agree in kind, count, type, texel type and storage format (stages are united);
a disagreement names both entry points and the slot. Push-constant blocks of the same name and size are shared; blocks of different structs
merge into one range covering all their stages, sized to the largest, when the members that overlap in bytes have the same type at the same
offset (this needs the layout built by `EntryLayout.TryCreate`, which carries `PushConstantType`; generated layouts can only merge blocks of
the same name and size). Entry points of different files number Metal resources independently, so `TryToMetal` reports a slot or push block on
which two entry points of one stage disagree (Vulkan and WebGPU are unaffected, apart from push-constant slots in the WGSL).

Options that shape the output without changing what the shader says: `WidenSets`/`WidenBindings`/`WidenPushConstants` (Vulkan) and
`WidenGroups`/`WidenBindings`/`WidenPushConstants` (WebGPU) OR extra stages into the visibility; `DynamicBuffers` (by name),
`DynamicBindings` (by set and binding) and `ResourceLayout.DynamicOffset` (on a layout you built) make a constant or storage buffer
`*_BUFFER_DYNAMIC` / `hasDynamicOffset` (`DescriptorSetLayoutBindingInfo.IsDynamic`, `BindGroupLayoutEntryInfo.HasDynamicOffset`).
`TryWithVertexOverrides` accepts a format of the same class as the shader's input (float, unorm, snorm and half formats for float inputs,
Uint formats for `uint`, Sint formats for `int`); a different component count is fine, because Vulkan, Metal and WebGPU all drop or
fill components. The attribute must lie inside the stride, and its offset must be a multiple of min(4, format size). Otherwise
`LayoutErrorKind.InvalidVertexInput`.

Differences one Vulkan descriptor covers can be allowed, per union, with `UnionOptions` (both off by default):

```csharp
var opt = new UnionOptions { IgnoreTextureDimension = true, MergeStorageAccess = true };
PipelineLayout.TryCreateUnion(allEntries, opt, out var shared, out var error);
```

- `IgnoreTextureDimension`: a `Texture2D` in one entry point and a `TextureCube` in another may share a slot (kind, count, texel type and
  storage format must still agree). Vulkan and Metal are unaffected. WebGPU checks `viewDimension` against the shader, so `TryToWebGpu`
  refuses such a slot (`Unsupported`, naming both types) unless `WebGpuLayoutOptions.TextureDimensions[(group, binding)]` picks one of the
  declared dimensions; a pipeline whose shader declares the other one needs a layout with its own pick.
- `MergeStorageAccess`: `StructuredBuffer` and `RWStructuredBuffer` (or `ByteAddressBuffer` and `RWByteAddressBuffer`) at one slot merge to
  read-write. WebGPU's `storage` binding only fits shaders that declare the buffer read-write, so `TryToWebGpu` refuses the slot unless
  `ReadOnlyStorageBuffers` names it (no shader writes it) or every entry point declares it `RW`.

`PipelineLayout.ToBindings(BindingOptions?)` describes the layout without any API's vocabulary: per set and binding a `BindingDescription`
with `Type` (`UniformBuffer`, `ReadOnlyStorageBuffer`, `StorageBuffer`, `SampledTexture`, `StorageTexture`, `Sampler`, texel buffers),
`Count`, `Stages`, `IsDynamic`, the texture `Dimension` (and every `Dimensions` seen at the slot), `SampleKind` (`Float`, `Sint`, `Uint`, or
`Depth` for names in `BindingOptions.DepthTextures`), `IsComparison`, `StorageFormat` and `MetalIndex`. Read-only against read-write comes from the
HLSL type, which is what Vulkan's `STORAGE_BUFFER` cannot say and WebGPU needs.

One entry point can read several vertex buffers, for example a per-vertex mesh stream and a per-instance stream:

```csharp
// Locations 0..1 come from a 32-byte per-vertex buffer, locations 2.. from a 64-byte per-instance buffer.
if (!entry.Vertex!.TrySplit([new VertexStream(0, 32), new VertexStream(2, 64, VertexStepMode.Instance)], out var streams, out var error, overrides))
    Console.WriteLine(error);   // overlapping attributes, a stride too small, a location in no stream, ...
var vk = streams.ToVulkan();             // bindings 0 and 1, with input rates
var mtl = streams.ToMetal(out error);    // layouts at buffer 30 and 29, with step functions
var web = streams.ToWebGpu();            // vertex.buffers[0..1], with stepMode
```

Stream `i` covers the locations from its first location up to the next stream's. Offsets are relative to the stream: unless `overrides` gives
one, an attribute keeps its place in the layout minus the offset of its stream's first attribute. Metal counts down from the vertex-buffer base
(30) because the compiler puts its own resources at the low indices and Metal's last slot is 30. A stream the entry point reads nothing
from is kept (empty), so slot numbers are the same for every entry point sharing the streams.

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

- Bindless resources: unbounded arrays (`Texture2D Tex[]`) and the SM 6.6 heaps (`ResourceDescriptorHeap[i]`, `SamplerDescriptorHeap[i]`) on SPIR-V
  (descriptor indexing, DXC's heap mapping) and Metal (one argument buffer of 8-byte descriptors each). `ResourceInfo.IsUnbounded`, `Heap` and
  `HeapKinds` describe them; `DeviceFeatures.RuntimeDescriptorArray`, `NonUniformIndexing` and `MutableDescriptorType` say what the device needs. The layout helpers
  give Vulkan a variable-count, partially bound binding (`VulkanLayoutOptions.UnboundedMaxCount`, `PartiallyBound`, `VariableDescriptorCount`, `UpdateAfterBind`),
  Metal a `MetalArgument` with `IsArgumentBuffer` and `DescriptorStride`, and refuse WebGPU.

## Known limits

- SPIR-V, Metal (MSL) and WebGPU (WGSL) output. Limits are listed in `docs/msl-backend.md` and
  `docs/wgsl-backend.md`.
- `WaveMatch`/`WaveMultiPrefix*` are not supported; unbounded resource arrays and descriptor heaps are errors on WGSL only (WebGPU has no binding arrays). Wave and quad intrinsics work; a shader that uses them needs subgroup support on the device (`EntryPointInfo.SubgroupOperations`).

## License

MIT OR Apache-2.0, at your option. Copyright © Recursive AI, LLC.
