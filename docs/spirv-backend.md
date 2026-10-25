# The SPIR-V backend

`SpirvBackend.Emit(IrModule, entryPointName, SpirvOptions?, DiagnosticBag)` turns one entry point of an IR
module, and everything it reaches, into a SPIR-V 1.5 binary for the Vulkan 1.2 environment (Shader capability,
logical addressing, GLSL450 memory model, GLSL.std.450 for math). It returns `null` and reports `OSH50xx`
errors when something cannot be expressed; it does not throw for unsupported input. The same module and
options always give the same bytes. Code is in `src/OneShader/SpirV/`.

## Mapping decisions

- **Matrices are transposed.** An HLSL `floatRxC` is a SPIR-V matrix with R columns of C components, so
  `m[i]` is column `i`, and `mul(a, b)` is `OpMatrixTimesMatrix(b, a)`, `mul(v, m)` is `OpMatrixTimesVector(m, v)`
  and `mul(m, v)` is `OpVectorTimesMatrix(v, m)`. In buffers, HLSL `column_major` becomes `RowMajor` and vice
  versa; `MatrixStride` comes from the buffer layout. One-row or one-column matrices and one-component vectors
  have no SPIR-V matrix type: they are vectors and scalars, and their indices disappear.
- **Buffer layout** types (offset-decorated structs, strided arrays) are separate SPIR-V types from the plain types
  used in registers. Loading or storing a whole struct or array to a buffer converts member by member.
- **Push constants.** A `[[vk::push_constant]]` block is a `Block` struct in the `PushConstant` storage class with no
  descriptor decorations. Its offsets follow the vector-relaxed std430 rules of a structured-buffer element (what DXC emits,
  checked against it by `PushConstantTests`), not the constant-buffer rules.
- **Resources.** A constant buffer is a `Block` struct in `Uniform`; a structured or byte-address buffer is a
  `Block` struct holding one runtime array in `StorageBuffer` (`NonWritable` when read-only). Access chains into
  a storage buffer get the leading `0` for that struct. Textures and samplers are separate variables combined
  with `OpSampledImage` at each use. Storage images use the format from `[[vk::image_format]]` when the shader gives one (no read/write-without-format
  capability then; formats outside the core set add `StorageImageExtendedFormat`) and `Unknown` otherwise. Atomics on a
  texel go through `OpImageTexelPointer`; atomic scopes and barrier semantics match what DXC emits.
  A resource array is one variable of `OpTypeArray` (set and binding as for a single resource); a run-time index
  adds the matching `*ArrayDynamicIndexing` capability, and `NonUniformResourceIndex` adds `ShaderNonUniform`, the
  `*ArrayNonUniformIndexing` capability and `NonUniform` on the index, access chain, loaded handle and sampled image.
  An append or consume buffer's counter is a second storage buffer variable (`counter.var.<name>`) in the buffer's set.
- **Interface.** Every global variable the entry point references is listed on `OpEntryPoint`, as SPIR-V 1.4+
  requires, inputs first, then outputs, then the rest in module order. Integer and double fragment inputs (and the
  vertex outputs that feed them) are `Flat`. Pixel shaders get `OriginUpperLeft`, plus `DepthReplacing` when they
  write depth.
- **Stage variables.** Arrays and matrices (at least 2x2) become one variable per element or row, each with its own
  location, which occupies the same locations as DXC's single array-typed variable. Clip and cull distances are the
  exception: every `SV_ClipDistance`/`SV_CullDistance` variable of an entry point is a slice of one `ClipDistance` /
  `CullDistance` built-in array, ordered by semantic index (an array's elements in order), as DXC lays them out.
- **Booleans in memory.** `OpTypeBool` is not allowed in uniform or storage memory, so `bool` members of buffers are
  32-bit unsigned integers (0 or 1), converted to bool on load and back on store. A structured buffer's element stride
  is its size rounded up to its alignment (16 bytes for a `float3`).
- **Control flow.** Blocks are emitted in reverse post-order of a walk that visits a header's merge and continue
  blocks first, which puts every merge after the blocks inside its construct. Unreachable blocks are dropped; a
  block without a terminator becomes `OpUnreachable`.
- **Discard** is `OpKill`, valid in SPIR-V 1.5; `OpTerminateInvocation` needs a newer environment.
- **Shifts** mask the count to the operand width, as HLSL does. `Ne` on floats is unordered (true for NaN).
  `precise` becomes `NoContraction`.

## Not supported yet (reported as OSH5002)

16-bit floating point, wave and quad intrinsics, unbounded resource arrays (rejected by the front end),
comparison or bit-casting of whole matrices, bool matrices, products involving one-row/column matrices.
