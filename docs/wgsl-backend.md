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
- **Textures:** a texture used with a comparison sampler is a depth texture; storage textures need `[[vk::image_format]]` and take
  `read`, `write` or `read_write` from how the entry point uses them.
- **Derivatives:** pixel shaders that sample or take derivatives start with `diagnostic(off, derivative_uniformity);`, since HLSL allows
  them under non-uniform control flow.

## Not available (diagnostic OSH5202)

clip and cull distances (optional WebGPU feature); `CalculateLevelOfDetail`; wave and quad intrinsics; resource arrays (WebGPU has no
binding arrays: one error per array, which suggests separate resources or a `Texture2DArray`; `NonUniformResourceIndex` is just its
index);
`Buffer<T>`/`RWBuffer<T>`; append and consume buffers; atomics on textures; `double`, 64-bit and 16-bit types; integer and bool
matrices; `PrimitiveID`, layer and viewport index; recursion; storage textures without a format or read-written in a format other than
r32float/r32uint/r32sint; bool stage variables; texel offsets on cube textures; control flow that is not structured.

## Known differences

WGSL lets an implementation fuse a multiply and an add, so float results can differ in the last bits from the SPIR-V target's; in the
parity run against DXC the `particles` case differs in the last bits on one driver version and matches on others.
`precise` has no WGSL equivalent. Memory-only barriers become full barriers and must sit in uniform control flow. `isnan`, `isinf` and
`isfinite` test exponent bits, which WGSL implementations may assume never fire.

## Checking

Every corpus file, optimized and not, either validates with `naga` or reports one of the errors above (the table in
`WgslBackendTests`). `ONESHADER_PARITY_TARGET=wgsl tools/run-parity.sh` runs the GPU comparison against DXC on the WGSL output
translated to SPIR-V by `naga`; cases that need an unavailable construct are skipped and listed as `unsupported-on-target` in the
parity table.
