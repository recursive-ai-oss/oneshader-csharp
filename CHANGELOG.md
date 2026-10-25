# Changelog

All notable changes to the packages `RecursiveAI.OneShader` and `RecursiveAI.OneShader.Build`.
Versions follow [Semantic Versioning](https://semver.org/); before 1.0 the API may change between previews.

## [Unreleased]

- Bounded resource arrays end to end (`Texture2D T[4]`, `SamplerState S[2]`, arrays of structured buffers and constant buffers): constant and run-time indexing, `NonUniformResourceIndex`, elements passed to functions, reflection and generated `Count`. Unbounded arrays are a clear error.
- `AppendStructuredBuffer` and `ConsumeStructuredBuffer` (`Append`, `Consume`, `GetDimensions`) with the hidden counter exposed in reflection (`counter` records) and in the generated bindings (`CounterBinding`, a `<name>.counter` entry in `All`).
- A constant buffer may share its name with one of its members.
- Static struct members, member functions defined outside their struct, template argument deduction through struct templates (`Box<T>`), and integer literal arithmetic in 64 bits (`(1 << 31) >> 3` is `0x10000000`, as in DXC).
- A resource array occupies one binding number (it used to take one per element when numbering unannotated resources).

## [0.1.0-preview.1]

First preview.

- HLSL compiler for vertex, pixel and compute shaders (modern HLSL, HLSL 2021): preprocessor, constant buffers, textures and samplers, structured and byte-address buffers, storage images, groupshared memory, barriers and atomics.
- SPIR-V output for Vulkan 1.2, with reflection; buffer layouts and descriptor bindings follow DXC's Vulkan conventions.
- MSBuild integration: `*.hlsl` compiled during `dotnet build`, errors on the HLSL line, embedded SPIR-V and generated C# structs, vertex inputs and binding constants.
- Known limits: SPIR-V only; single resources only (no resource arrays); no templates, member functions, operator overloads or wave intrinsics yet; `dotnet build` only.
