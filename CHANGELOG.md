# Changelog

All notable changes to the packages `RecursiveAI.OneShader` and `RecursiveAI.OneShader.Build`.
Versions follow [Semantic Versioning](https://semver.org/); before 1.0 the API may change between previews.

## [Unreleased]

## [0.2.0-preview.1]

- Optimizer, on by default (`CompileOptions.Optimize`): inlining, SSA promotion, constant folding, common-subexpression and dead-code elimination, control-flow simplification. SPIR-V is about 38% smaller than before and close to DXC's size.
- HLSL 2021 language features: function and struct templates, member functions and `this`, single inheritance, operator overloads, `sizeof`, static locals, resource-typed locals and parameters.
- Bounded resource arrays end to end (`Texture2D T[4]`, `SamplerState S[2]`, arrays of structured buffers and constant buffers): constant and run-time indexing, `NonUniformResourceIndex`, elements passed to functions, reflection and generated `Count`. Unbounded arrays are a clear error.
- `AppendStructuredBuffer` and `ConsumeStructuredBuffer` (`Append`, `Consume`, `GetDimensions`) with the hidden counter exposed in reflection (`counter` records) and in the generated bindings (`CounterBinding`, a `<name>.counter` entry in `All`).
- A constant buffer may share its name with one of its members, and a whole `ConstantBuffer<S>` can be copied into a struct local (`S s = cb;`).
- Static struct members, member functions defined outside their struct, template argument deduction through struct templates (`Box<T>`), and integer literal arithmetic in 64 bits (`(1 << 31) >> 3` is `0x10000000`, as in DXC).
- A resource array occupies one binding number (it used to take one per element when numbering unannotated resources).
- Stage inputs and outputs may be arrays and matrices (one location per element or row, like DXC), and `SV_ClipDistance` / `SV_CullDistance` work in vertex outputs and pixel inputs, as scalars, vectors or arrays.
- `StructuredBuffer<float3>` and other elements whose size is not a multiple of their alignment use the aligned stride (16 bytes for `float3`), which Vulkan validation requires; `bool` members of constant and structured buffers are stored as 32-bit integers.
- New: `Texture2D.CalculateLevelOfDetail` that validates, `GatherCmp`/`GatherCmpRed` on 2D, array and cube textures, `f16tof32`, `f32tof16`, `faceforward`, `log10`.
- Overlapping `packoffset` members are an error (OSH0332) instead of invalid SPIR-V.
- `ShaderCompiler.Compile` turns an unexpected internal exception into error OSH7000 on line 1 of the file instead of throwing; the build task reports internal errors at line 1 as well.

- Hot reload support: `CompileResult.Files` lists the files a compile read (for file watchers), `ShaderCompiler.CompileFile` compiles from disk and reports an unreadable file as diagnostic OSH0003, and `InterfaceCompatibility.Compare` tells whether a recompiled shader still matches the generated C# (constant-buffer offsets and types, vertex inputs, bindings, entry points, thread-group sizes), listing each difference. Generated shader classes gain a `Reflection` member with the reflection they were generated from. Concurrent `Compile` calls are tested to give the same bytes as serial ones. `samples/HotReload` shows the loop.

## [0.1.0-preview.1]

First preview.

- HLSL compiler for vertex, pixel and compute shaders (modern HLSL, HLSL 2021): preprocessor, constant buffers, textures and samplers, structured and byte-address buffers, storage images, groupshared memory, barriers and atomics.
- SPIR-V output for Vulkan 1.2, with reflection; buffer layouts and descriptor bindings follow DXC's Vulkan conventions.
- MSBuild integration: `*.hlsl` compiled during `dotnet build`, errors on the HLSL line, embedded SPIR-V and generated C# structs, vertex inputs and binding constants.
- Known limits: SPIR-V only; single resources only (no resource arrays); no templates, member functions, operator overloads or wave intrinsics yet; `dotnet build` only.
