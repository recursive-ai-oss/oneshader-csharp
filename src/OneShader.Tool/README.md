# OneShader.Tool

**`oneshader`: the OneShader HLSL compiler on the command line.** It compiles a vertex, pixel or
compute shader to SPIR-V (Vulkan 1.2+), Metal Shading Language or WGSL, and takes DXC's core
flags, so a DXC command line mostly works unchanged. Use it from CMake, Make, scripts or any build
system that is not MSBuild. (In an MSBuild project, [RecursiveAI.OneShader.Build](https://www.nuget.org/packages/RecursiveAI.OneShader.Build)
compiles shaders during `dotnet build` and generates C# bindings.)

## Install

```sh
dotnet tool install -g RecursiveAI.OneShader.Tool
oneshader --version
```

Needs the .NET 10 runtime. For a single native binary with no runtime and the fastest start-up
(about 2.4 times faster per process than DXC), publish it with NativeAOT from this repository:

```sh
dotnet publish src/OneShader.Tool -c Release -r linux-x64 -o out    # out/oneshader
```

## Use

```sh
oneshader -T ps_6_0 -E PSMain -spirv sprite.hlsl -Fo sprite.ps.spv
oneshader --target msl --target wgsl --all-entries sprite.hlsl -Fo build/sprite.out
oneshader -T cs_6_0 -E CSMain -D TILE=16 -I include --reflect histogram.json --depfile histogram.d histogram.hlsl -Fo histogram.spv
```

Diagnostics go to standard error as `file(line,col): error OSH0102: message`, the form IDEs and CMake
parse. Exit code: **0** success, **1** the shader did not compile (or an output could not be written),
**2** the command line is wrong. Warnings print and do not change the exit code unless `-WX` is given.

### DXC-compatible options

| Option | Meaning |
|---|---|
| `-T <profile>` | `vs_6_x`, `ps_6_x`, `cs_6_x` (also `_5_x`); the stage comes from the profile, the model number is accepted and otherwise unused. Geometry, tessellation, mesh and library profiles are rejected. |
| `-E <name>` | Entry point; with `-T` and no `-E` it is `main`, as in DXC. |
| `-D NAME[=VALUE]` | Macro; without a value it is `1`. |
| `-I <dir>` | `#include` search directory. |
| `-Fo <file>` | Output file. Several outputs (entry points × targets) are written beside it: `name.<entry><ext>` when there are several entry points, and `.spv`/`.metal`/`.wgsl` when there are several targets. Without `-Fo`, a single output goes to standard output. |
| `-spirv` | Emit SPIR-V. This is also the default when no target is named (DXC's default is DXIL, which is not implemented). |
| `-O0`, `-Od` / `-O1`..`-O3` | Optimizer off / on (default on). |
| `-Zpr` / `-Zpc` | Row-major / column-major default matrix packing. |
| `-HV 2021` | Accepted (`202x` too); other versions are rejected. |
| `-WX` | Warnings are errors. |
| `-enable-16bit-types` | `half` and `min16*` are 16-bit. |
| `-fspv-target-env=vulkan1.2` | Accepted (`vulkan1.3` too); the output is valid for Vulkan 1.2 and newer. |
| `-nologo` | Accepted, ignored. |

Values may follow the flag after a space or directly (`-T ps_6_0`, `-Tps_6_0`). Every other `-fvk-*`
and `-fspv-*` option, and DXC options such as `-Zi` or `-Fh`, is rejected with a message naming it.
oneshader always uses DXC's default Vulkan layout and binding rules; use `[[vk::binding]]`,
`[[vk::location]]` and `[[vk::offset]]` in the shader.

### oneshader options

| Option | Meaning |
|---|---|
| `--target spirv\|msl\|wgsl` | Target; repeat for several. |
| `--all-entries` | Compile every `[shader("vertex"\|"pixel"\|"compute")]` entry point. This is also what happens without `-T`. Cannot be combined with `-T`/`-E`. |
| `--reflect <file.json>` | Write reflection as JSON (schema below). |
| `--depfile <file>` | Write a Make-style dependency file, `outputs: files read`, with `$`, `#` and spaces escaped. Written on success. |
| `--no-names` | Leave source names out of the output. |
| `--version`, `--help` | |

## Reflection JSON, schema version 1

The same data as the library's `ShaderReflection`, keys in camelCase, enums by name, output in a fixed
order, `\n` line ends and no machine paths, so the same compile gives the same bytes. Changes that
break readers raise `schemaVersion`.

```
{ "schemaVersion": 1, "source": "<file name>", "targets": ["SpirV", ...],
  "resources": [ { "name", "kind", "hlslType", "resourceType", "count", "stages": ["Vertex"|"Pixel"|"Compute"],
                   "slots": [ { "target", "set", "binding" } ], "counterSlots": [ ... ], "layout": <type> } ],
  "pushConstants": [ { "name", "stages", "slots", "layout": <type> } ],
  "entryPoints": [ { "name", "stage", "workgroupSize": [x, y, z] (compute), "vertexStride" (vertex),
                     "inputs": [ <variable> ], "outputs": [ <variable> ], "usedResources": [ names ],
                     "pushConstant": name (optional) } ] }

<variable> = { "name", "semantic", "semanticIndex", "location" (optional), "builtIn", "scalar", "components",
               "format" (optional), "offset", "interpolation" }
<type>     = { "kind": "Scalar|Vector|Matrix|Array|Struct", "size", "alignment", and by kind:
               Scalar: "scalar" | Vector: "scalar", "components" | Matrix: "scalar", "rows", "columns", "matrixStride", "packing"
               Array: "length", "stride", "element": <type> | Struct: "name", "members": [ { "name", "offset", "type": <type> } ] }
```

Sizes, offsets and strides are the byte layout of the buffer, equal for every target.

Licensed under MIT OR Apache-2.0.
