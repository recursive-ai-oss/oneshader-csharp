# OneShader

**A HLSL compiler for .NET, written in C# with no dependency beyond .NET itself.**
Modern HLSL (Shader Model 6 style) for vertex, pixel and compute shaders goes in;
SPIR-V for Vulkan, Metal Shading Language and WGSL come out, each with
reflection. An MSBuild task and a source generator compile your `*.hlsl` files
during `dotnet build`, report errors on the HLSL line, and generate C# structs
and binding code that match the compiled shader.

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](#install)
[![License](https://img.shields.io/badge/license-MIT%20OR%20Apache--2.0-blue)](#license)

How many times faster or slower OneShader compiles a shader than DXC (the
released binary, SPIR-V output), one process per entry point over 68 corpus
entry points, geometric mean, log scale. A bar to the right is a win for
OneShader, a bar to the left is a loss:

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/recursive-ai-oss/oneshader-csharp/main/docs/img/oneshader-hero-dark.svg">
  <img alt="Bar chart: how many times faster or slower OneShader compiles than DXC, log scale. The NativeAOT build is about 2.4 times faster when warm and 2.7 times faster on the first run. Started as an ordinary .NET process, it is slower: about 5 to 6 times with ReadyToRun and 13 to 14 times with the JIT." src="https://raw.githubusercontent.com/recursive-ai-oss/oneshader-csharp/main/docs/img/oneshader-hero-light.svg">
</picture>

[Full results, with allocations per compile, output size, the losses and the method →](docs/performance.md)

## Why OneShader

- **Shader errors are build errors.** Put `*.hlsl` in a project and reference
  `RecursiveAI.OneShader.Build`: a mistake shows up as
  `Shaders/Sprite.hlsl(24,45): error OSH0300: 'Atlass' is not declared. Did you mean 'Atlas'?`
  on the HLSL line, with a stable code, not as a black screen at run time.
- **C# that matches the shader.** Constant buffers, vertex inputs and resource
  bindings become blittable structs and constants whose offsets equal the
  compiled shader's, so a CPU/GPU layout mismatch fails the build
  ([generated bindings](docs/generated-bindings.md)). One layout serves every
  target.
- **Three targets from one front end.** SPIR-V (Vulkan 1.2+) is the default;
  Metal Shading Language and WGSL are opt-in per project
  (`<OneShaderTargets>SpirV;Msl;Wgsl</OneShaderTargets>`). A feature a target
  cannot express is a diagnostic naming the target, never a silent
  miscompile ([MSL](docs/msl-backend.md), [WGSL](docs/wgsl-backend.md)).
- **Checked against DXC and real GPUs.** SPIR-V is validated with `spirv-val`
  and every corpus shader is run on the GPU next to the same shader compiled by
  DXC, with the Vulkan validation layers on; WGSL goes through `naga` and a
  browser WebGPU run ([WebGPU parity](docs/webgpu-parity.md)); MSL goes through
  Apple's compiler and a Metal run.
- **Safe on text you did not write.** The compiler runs inside your build, so
  bad input yields diagnostics, never an exception or a stack overflow, and
  the front end is fuzzed ([fuzzing](fuzz/README.md)). It contains no
  `unsafe` code, uses no reflection, and is NativeAOT and trim clean.
- **Fast, with the losses shown.** As a NativeAOT binary it compiles the
  corpus about 2.4 times faster than DXC, warm, and every one of the 68 entry
  points is faster. Run as an ordinary .NET process it is slower than DXC on
  every one of them (about 6 times with ReadyToRun, 14 times with the JIT),
  because starting the runtime and the compiler's code dominates a compile that
  takes 0.34 ms at the median once the process is running. Its SPIR-V is 5.7%
  larger than DXC's by default because it keeps debug names, and 8.8% smaller
  with them off. Memory use was not measured
  ([every result](docs/performance.md)).

## Install

For building shaders into your project:

```sh
dotnet add package RecursiveAI.OneShader.Build --prerelease
```

For the compiler itself, for hot reload and tools:

```sh
dotnet add package RecursiveAI.OneShader --prerelease
```

Target framework `net10.0`. Build with the .NET SDK (`dotnet build`); Visual
Studio's .NET Framework MSBuild cannot load the build task. Package guides:
[RecursiveAI.OneShader.Build](src/OneShader.Build/README.md) and
[RecursiveAI.OneShader](src/OneShader/README.md).

## Quick start

`Shaders/Sprite.hlsl`:

```hlsl
cbuffer Frame : register(b0) { float4x4 ViewProjection; };
[[vk::binding(1)]] Texture2D<float4> Atlas;
[[vk::binding(2)]] SamplerState AtlasSampler;

struct VSIn { float2 Position : POSITION; float2 Uv : TEXCOORD0; float4 Color : COLOR0; };
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

ReadOnlySpan<byte> vs = Sprite.VSMain.SpirV;                 // embedded SPIR-V
var frame = new Sprite.Frame { ViewProjection = camera };    // explicit layout, Size = 64
uint set = Sprite.Bindings.Atlas.Set, binding = Sprite.Bindings.Atlas.Binding;
```

To compile at run time instead (tools, hot reload):

```csharp
using OneShader;

CompileResult result = ShaderCompiler.CompileFile("Shaders/Sprite.hlsl");
if (!result.Success) { foreach (var d in result.Diagnostics) Console.WriteLine(d); return; }
byte[] spirv = result.Find("PSMain", CompileTarget.SpirV)!.ToArray();
```

## Status

**Preview, version 0.4.** Vertex, pixel and compute shaders compile to
optimized SPIR-V, Metal Shading Language and WGSL with reflection; the build
package embeds each selected target next to generated C# bindings (the
`OneShaderTargets` switch), and the runtime API supports hot reload with a
layout-compatibility check. Not supported: geometry, tessellation, mesh and
ray-tracing stages, unbounded resource arrays and wave intrinsics, and some
features on some targets (the backend pages list them). The API may change
before 1.0. The MSL and WGSL targets and `OneShaderTargets` are new in the 0.3 line; the
[changelog](CHANGELOG.md) says which preview first contains each feature.

## Where to go

- **Using it:** the package guides ([build](src/OneShader.Build/README.md),
  [compiler](src/OneShader/README.md)), the
  [generated bindings](docs/generated-bindings.md), and the
  [reflection format](docs/reflection-format.md).
- **Backends:** [SPIR-V](docs/spirv-backend.md), [MSL](docs/msl-backend.md),
  [WGSL](docs/wgsl-backend.md), the shared [IR](docs/ir.md), and
  [browser WebGPU parity](docs/webgpu-parity.md).
- **Evidence:** [performance and its losses](docs/performance.md) (the
  numbers behind the chart; reproduce with `bench/run.sh`),
  [fuzzing](fuzz/README.md), and the corpus in [`corpus/`](corpus/README.md).
- **What changed:** [CHANGELOG.md](CHANGELOG.md).

## Building

```sh
dotnet build
dotnet test
```

Build output goes to the directory named by `ONESHADER_ARTIFACTS` (or
`.artifacts` in the repository when it is unset). Tests that compare against
reference compilers use released binaries fetched by `tools/fetch-oracles.sh`;
versions are pinned in `tools/oracles.env`.

## License

Licensed under either of [MIT](LICENSE-MIT) or
[Apache-2.0](LICENSE-APACHE) at your option.

Copyright (c) Recursive AI, LLC.
