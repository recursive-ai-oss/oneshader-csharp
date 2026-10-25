# Changelog

All notable changes to the packages `RecursiveAI.OneShader` and `RecursiveAI.OneShader.Build`.
Versions follow [Semantic Versioning](https://semver.org/); before 1.0 the API may change between previews.

## [Unreleased]

## [0.1.0-preview.1]

First preview.

- HLSL compiler for vertex, pixel and compute shaders (modern HLSL, HLSL 2021): preprocessor, constant buffers, textures and samplers, structured and byte-address buffers, storage images, groupshared memory, barriers and atomics.
- SPIR-V output for Vulkan 1.2, with reflection; buffer layouts and descriptor bindings follow DXC's Vulkan conventions.
- MSBuild integration: `*.hlsl` compiled during `dotnet build`, errors on the HLSL line, embedded SPIR-V and generated C# structs, vertex inputs and binding constants.
- Known limits: SPIR-V only; single resources only (no resource arrays); no templates, member functions, operator overloads or wave intrinsics yet; `dotnet build` only.
