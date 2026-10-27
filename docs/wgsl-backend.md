# The WGSL backend

`WgslBackend.Emit(IrModule, entryPointName, WgslOptions?, DiagnosticBag)` writes one entry point, and everything it
reaches, as WGSL text (`CompileTarget.Wgsl` gives one UTF-8 blob per entry point). It returns `null` and reports
`OSH52xx` errors for constructs WGSL cannot express; it never throws for unsupported input. Output is deterministic.
Code is in `src/OneShader/Wgsl/`; naga validates every corpus entry point (`tests/OneShader.Tests/WgslBackendTests.cs`).

## Mapping decisions

- **Push constants** (WebGPU core has none) are a `var<uniform>` of 16-byte rows like a constant buffer, with the SPIR-V
  block's byte layout. It is bound at the highest bind group the shader uses and the binding after that group's highest one,
  so it cannot collide with the shader's own bindings; the slot is in reflection and in the generated `Bindings.PushConstants`.
  The host binds a uniform buffer of at least the block size rounded up to 16 bytes.
- **Buffers are raw words, so layouts equal the SPIR-V target's.** A constant buffer is `array<vec4<u32>, N>` (WGSL 13.4.1:
  uniform arrays have a 16-byte stride, one vec4 per row); structured and byte-address buffers are `array<u32>`
  (`array<atomic<u32>>` when the entry point uses atomics on them; a buffer the entry point never writes is `read`).
  Every access computes its byte offset from the reflected layout and bitcasts; vectors inside one row are read as a
  swizzle. Signed atomic min/max on a buffer and compare-exchange use small helper functions.
  `WgslLayoutTests` checks the word indices of every scalar, vector and matrix in every corpus buffer against the reflected offsets.
- **Matrices are transposed**, as in SPIR-V: HLSL `floatRxC` is `matRxC<f32>` (R columns of C components), `m[i]` is row i, and
  every `mul` swaps its operands. Matrix products with one-row or one-column matrices are not supported.
- **Bindings:** `@group(set) @binding(binding)`, equal to the Vulkan set and binding.
- **Stage I/O:** inputs and outputs are module-level private variables copied from the entry parameters and into the return
  struct. Arrays and matrices are split one location per element or row. Integer fragment inputs and vertex outputs are `flat`.
- **Control flow** is rebuilt from the IR's structure markers: `if`, `switch`, `loop` with `continuing` (a do-while test becomes
  `break if`). A `break` out of a loop from inside a `switch` goes through a flag variable. Phis are variables assigned on each edge.
  WGSL has no fall-through, so a `switch` case that runs into the next one gets that case's statements again after its own (a chain of
  n cases that fall through each other is written out in full, so the text grows with n squared); a `default` in the middle becomes the
  last clause.
- **Entry names:** the HLSL name, with anything that is not a letter, digit or underscore turned into an underscore and a trailing
  underscore added to names WGSL reserves or predeclares (`loop` becomes `loop_`): `WgslBackend.EntryFunctionName`, which reflection and the
  generated `WgslEntryPoint` use.
- **Textures:** a texture used with a comparison sampler is a depth texture; storage textures need `[[vk::image_format]]` and take
  `read`, `write` or `read_write` from how the entry point uses them.
- **Derivatives:** pixel shaders that sample or take derivatives start with `diagnostic(off, derivative_uniformity);`, since HLSL allows
  them under non-uniform control flow.

## Wave and quad intrinsics

WebGPU's optional `subgroups` feature. A module that uses any wave or quad intrinsic starts with `enable subgroups;` and
`diagnostic(off, subgroup_uniformity);` (HLSL does not require wave operations to run in uniform control flow), and the lane index and count are the
`subgroup_invocation_id` and `subgroup_size` built-ins. The host must create the device with the `subgroups` feature: reflection
(`ShaderReflection.UsesSubgroups`, `EntryPointInfo.SubgroupOperations`) and `PipelineLayout.TryToWebGpu` (`RequiredFeatures`) say so. Mapping:
`subgroupAdd`/`Mul`/`Min`/`Max`/`And`/`Or`/`Xor`, `subgroupExclusiveAdd`/`Mul` for the prefix functions, `subgroupBallot`, `subgroupAny`/`All`,
`subgroupElect`, `subgroupShuffle`, `subgroupBroadcastFirst`, `quadSwapX`/`Y`/`Diagonal`, `quadBroadcast`; `WaveActiveAllEqual` compares with
`subgroupBroadcastFirst` and the count-bits functions add `u32(condition)`. naga's WGSL reader does not parse the subgroups extension yet, so
these modules are validated by the browser's compiler (Tint) in the WebGPU parity run instead.

## Specialization constants

A constant is `@id(N) override Name: T = default;` (WGSL section 7.3; N is the HLSL `constant_id`, which must be at most 65535 here, otherwise
OSH5202). Only the constants an entry point reads are declared. The pipeline's `constants` record is keyed by the decimal id, which is also the key when an
override has a name: `SpecializationValues.ToWebGpu()` builds it, with a bool as 0 or 1. WebGPU rejects a key the shader does not declare, so use the
constants of the entry point you are building (`EntryLayout.SpecConstants`, or the generated `SpecConstantIds`). The `naga` command-line tool writes
overrides into SPIR-V as plain constants at their defaults, so overrides are verified in the browser run rather than through naga.

## 16-bit floats

With `Enable16BitTypes`, `half` and `min16float` are `f16` and the module starts with `enable f16;` (WebGPU's optional `shader-f16` feature: reflection
`DeviceFeatures.Float16` and `PipelineLayout.TryToWebGpu` `RequiredFeatures` say when to request it). Buffers stay raw 32-bit words: a half is
read as one half of `unpack2x16float(word)` and written with `pack2x16float`; because the other half of a word may belong to a neighbour written by another invocation, a
buffer written with halves is declared `array<atomic<u32>>` and a store clears and sets only its own 16 bits with `atomicAnd`/`atomicOr`. WebGPU does not require half arithmetic
to round exactly as Vulkan does, so results can differ in the last bit of a half (the parity case allows 0.4%). There are no 16-bit integers.

## Debug information

`CompileOptions.DebugInfo` does not change the WGSL. WGSL has no line directive and the browsers' WebGPU implementations do not map shader positions back
to another language, so there is nothing a consumer could use; comments carrying `file:line` would make the text differ between debug and release
builds for no tool's benefit. Source names are kept by the option `KeepNames`.

## Not available (diagnostic OSH5202)

clip and cull distances (optional WebGPU feature); `CalculateLevelOfDetail`; wave and quad intrinsics in vertex shaders (WebGPU's subgroup functions and built-ins exist only in compute and fragment shaders); resource arrays (WebGPU has no
binding arrays: one error per array, which suggests separate resources or a `Texture2DArray`; `NonUniformResourceIndex` is just its
index), unbounded arrays (`Texture2D t[]`) and the SM 6.6 heaps `ResourceDescriptorHeap` / `SamplerDescriptorHeap` (WebGPU has no
descriptor indexing: one error per unbounded array at its declaration and one per heap at its first use, each naming the line and what to do;
only when WGSL is a selected target);
`Buffer<T>`/`RWBuffer<T>`; append and consume buffers; atomics on textures; `double`, 64-bit integers and 16-bit integers (each reported at the line of its first use); integer and bool
matrices; `PrimitiveID`, layer and viewport index; recursion; storage textures without a format or read-written in a format other than
r32float/r32uint/r32sint; bool stage variables; texel offsets on cube textures; control flow that is not structured.

## Known differences

WGSL lets an implementation fuse a multiply and an add, so float results can differ in the last bits from the SPIR-V target's; in the
parity run against DXC the `particles` case differs in the last bits on one driver version and matches on others.
`precise` has no WGSL equivalent: the arithmetic compiles, with warning OSH5205 once per entry point, and the result may differ from the other targets in the last bits. Memory-only barriers become full barriers and must sit in uniform control flow. `isnan`, `isinf` and
`isfinite` test exponent bits, which WGSL implementations may assume never fire.

## Checking

Every corpus file, optimized and not, either validates with `naga` or reports one of the errors above (the table in
`WgslBackendTests`). `ONESHADER_PARITY_TARGET=wgsl tools/run-parity.sh` runs the GPU comparison against DXC on the WGSL output
translated to SPIR-V by `naga`; cases that need an unavailable construct are skipped and listed as `unsupported-on-target` in the
parity table.
