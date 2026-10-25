# Changelog

All notable changes to the packages `RecursiveAI.OneShader` and `RecursiveAI.OneShader.Build`.
Versions follow [Semantic Versioning](https://semver.org/); before 1.0 the API may change between previews.

## [Unreleased]

## [0.3.0-preview.1]

- Push constants: `[[vk::push_constant]]` on a struct-typed global or a `ConstantBuffer<T>` compiles on every target. SPIR-V gets a `PushConstant` block whose member offsets equal DXC's (vector-relaxed std430, not the cbuffer rules); MSL takes it as a `constant T&` at a Metal buffer index (set it with `setVertexBytes`, `setFragmentBytes` or `setBytes`); WGSL binds it as a uniform buffer at the next free binding of the highest bind group (WebGPU has no push constants). Reflection gains `ShaderReflection.PushConstants` (layout, stages, per-target slots) and `EntryPointInfo.PushConstant`; the generated class gains a `PushConstants` struct (offsets and `Size` equal to SPIR-V) and `Bindings.PushConstants` (`Size`, `Stages` for the `VkPushConstantRange`, `MslIndex`, `WgslGroup`/`WgslBinding`). An entry point that reads two blocks is OSH0333 at the second block; a block over 128 bytes (Vulkan's guaranteed `maxPushConstantsSize`) is warning OSH0334; `[[vk::push_constant]]` on a `cbuffer` is OSH0333 (DXC rejects it too).
- Build package: `OneShaderTargets` (`SpirV;Msl;Wgsl`, default `SpirV`; per-item `Targets` metadata) embeds Metal and WGSL sources next to the SPIR-V. Entry-point classes gain `Msl`, `MslFunctionName`, `MslVertexBufferIndex`, `Wgsl` and `WgslEntryPoint`; resource bindings gain `MslIndex` / `MslCounterIndex` and `ResourceBinding.MetalIndex`. A target's refusal (for example OSH5202 on WGSL) is a build error on the HLSL line only when that target is selected; unknown target names are OSH0903.
- New WGSL target (`CompileTarget.Wgsl`): vertex, fragment and compute entry points as WGSL text for WebGPU, with the same buffer byte layouts as the SPIR-V target. See docs/wgsl-backend.md for what is not available.
- New: Metal Shading Language output (`CompileTarget.Msl`, `MslBackend`): one source file per entry point with the same buffer byte layout as the SPIR-V output, Metal argument indices in reflection, and `tools/validate-msl.sh` to check the files with Apple's compiler. See `docs/msl-backend.md`.

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
