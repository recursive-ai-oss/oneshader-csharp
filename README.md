# OneShader

A HLSL compiler for .NET with no dependencies beyond the base class library. Modern HLSL (Shader Model 6 style) for vertex, pixel and compute stages goes in; SPIR-V, MSL and WGSL come out, each with reflection. An MSBuild task and source generator compile `*.hlsl` files during `dotnet build`, report errors on the HLSL line, and generate matching C# structs and binding code.

**Status: preview (0.1).** Vertex, pixel and compute shaders compile to SPIR-V for Vulkan, with reflection and build-time C# bindings. Metal and WebGPU output, resource arrays and some HLSL 2021 features are not there yet; see the package READMEs in `src/OneShader` and `src/OneShader.Build`, and `CHANGELOG.md`.

## Packages

- `RecursiveAI.OneShader`: the compiler and runtime API.
- `RecursiveAI.OneShader.Build`: the MSBuild task and source generator.

## Building

```
dotnet build
dotnet test
```

Build output goes to the directory named by `ONESHADER_ARTIFACTS` (or `.artifacts` in the repo when it is unset).

Tests that compare against reference compilers use released binaries fetched by `tools/fetch-oracles.sh`; versions are pinned in `tools/oracles.env`.

## Licence

MIT OR Apache-2.0, at your option. See `LICENSE-MIT` and `LICENSE-APACHE`.
