# Changelog

All notable changes to the packages `RecursiveAI.OneShader` and `RecursiveAI.OneShader.Build`.
Versions follow [Semantic Versioning](https://semver.org/); before 1.0 the API may change between previews.

## [Unreleased]

- New: Metal Shading Language output (`CompileTarget.Msl`, `MslBackend`): one source file per entry point with the same buffer byte layout as the SPIR-V output, Metal argument indices in reflection, and `tools/validate-msl.sh` to check the files with Apple's compiler. See `docs/msl-backend.md`.
- Stage inputs and outputs may be arrays and matrices (one location per element or row, like DXC), and `SV_ClipDistance` / `SV_CullDistance` work in vertex outputs and pixel inputs, as scalars, vectors or arrays.
- `StructuredBuffer<float3>` and other elements whose size is not a multiple of their alignment use the aligned stride (16 bytes for `float3`), which Vulkan validation requires; `bool` members of constant and structured buffers are stored as 32-bit integers.
- New: `Texture2D.CalculateLevelOfDetail` that validates, `GatherCmp`/`GatherCmpRed` on 2D, array and cube textures, `f16tof32`, `f32tof16`, `faceforward`, `log10`.
- Overlapping `packoffset` members are an error (OSH0332) instead of invalid SPIR-V.
- `ShaderCompiler.Compile` turns an unexpected internal exception into error OSH7000 on line 1 of the file instead of throwing; the build task reports internal errors at line 1 as well.

## [0.1.0-preview.1]

First preview.

- HLSL compiler for vertex, pixel and compute shaders (modern HLSL, HLSL 2021): preprocessor, constant buffers, textures and samplers, structured and byte-address buffers, storage images, groupshared memory, barriers and atomics.
- SPIR-V output for Vulkan 1.2, with reflection; buffer layouts and descriptor bindings follow DXC's Vulkan conventions.
- MSBuild integration: `*.hlsl` compiled during `dotnet build`, errors on the HLSL line, embedded SPIR-V and generated C# structs, vertex inputs and binding constants.
- Known limits: SPIR-V only; single resources only (no resource arrays); no templates, member functions, operator overloads or wave intrinsics yet; `dotnet build` only.
