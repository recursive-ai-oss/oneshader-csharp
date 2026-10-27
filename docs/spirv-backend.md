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

## Wave and quad intrinsics

`GroupNonUniform*` instructions with Subgroup scope, chosen as DXC's output chooses them: `WaveReadLaneAt` is `OpGroupNonUniformShuffle`,
`WaveActiveCountBits`/`WavePrefixCountBits` are `OpGroupNonUniformBallotBitCount` (Reduce / ExclusiveScan) of a ballot, the prefix functions are
exclusive scans, `QuadReadAcross*` is `OpGroupNonUniformQuadSwap` and `QuadReadLaneAt` is `OpGroupNonUniformQuadBroadcast`. The lane index and
count are the `SubgroupLocalInvocationId` and `SubgroupSize` input variables. The capabilities (`GroupNonUniform`, `Vote`, `Arithmetic`, `Ballot`,
`Shuffle`, `Quad`) are added only for what the entry point uses; reflection lists the same classes (`EntryPointInfo.SubgroupOperations`, values equal
to Vulkan's `VkSubgroupFeatureFlagBits`) so a host can check `subgroupSupportedOperations` before creating the pipeline.
`WaveMatch` and `WaveMultiPrefix*` are front-end errors (OSH0350): partitioned operations exist in Vulkan only as a vendor extension.

## Specialization constants

`[[vk::constant_id(N)]] const bool|int|uint|float Name = default;` is `OpSpecConstantTrue`/`OpSpecConstantFalse` (bool) or `OpSpecConstant` (the default's
bits) with `OpDecorate SpecId N` and an `OpName`, written where it is first used, so an entry point carries only the constants it reaches. The ids are DXC's
(a test compares the decorations with DXC's output for the corpus shaders). Expressions on constants are ordinary instructions; DXC emits
`OpSpecConstantOp` for the initializer of a `static const` that depends on one, and the result is the same for every pipeline. The optimizer treats a
constant as an unknown value, so branches on it stay. Pass the values in `VkSpecializationInfo` (`SpecializationValues.ToVulkan()`): every constant is a
4-byte value, a bool is a `VkBool32`.

## 16-bit and 64-bit types

With `Enable16BitTypes`, `half`/`min16float`/`min10float` are `OpTypeFloat 16` and `min16int`/`min16uint` and the `int16_t` family are 16-bit
integers (capabilities `Float16`, `Int16`, and for buffer members `StorageBuffer16BitAccess`, `UniformAndStorageBuffer16BitAccess`,
`StoragePushConstant16`, `StorageInputOutput16`). `double` is `Float64`, `int64_t`/`uint64_t` are `Int64`, and 64-bit `Interlocked*` adds `Int64Atomics`.
Buffer offsets, array strides and matrix strides equal DXC's (checked with `spirv-dis` on the corpus `types_*` cases). Without the switch, min-precision
types are 32-bit and carry `RelaxedPrecision` on members and results; a `min16int` constant is cut to 16 bits while the arithmetic stays 32-bit
(observed in DXC's output). Observed DXC choices followed: double arguments to float-only intrinsics are converted to float (we warn, OSH0361);
unsuffixed float literals stay double until they meet a concrete type. One DXC behaviour is not copied: `(float)int64_t` goes through a 32-bit
`SConvert` in DXC and truncates; we convert directly. `EntryPointInfo.RequiredFeatures` lists the device features the capabilities need.
Note: on the RTX 4070 (driver 595) a 16-bit signed remainder with a negative dividend returns a different value from RADV, llvmpipe and Metal;
the corpus case `types_int16` agrees between DXC and this compiler on every device because both emit `OpSRem`.

## Debug information

`SpirvOptions.DebugInfo` (set by `CompileOptions.DebugInfo`; see [debugging.md](debugging.md)).

**Lines** writes what DXC's `-Zi` writes: an `OpString` for each file, `OpSource HLSL 600 <file> "<text>"` (with `OpSourceContinued` when the
text is long) for the main file and every included file, and an `OpLine <file> <line> <column>` before each statement's first instruction, `OpNoLine` where
an instruction has no source position. Phis and variables carry no line, so the block layout rules hold, and the line is written before a block's
merge instruction, never between it and the branch.

**Full** imports `NonSemantic.Shader.DebugInfo.100` and declares `SPV_KHR_non_semantic_info`. The records, in the order the specification
requires (types, constants and variables, then the debug records, then code):

- `DebugSource` for every file the compile read (the main file first), with its text; long text continues in `DebugSourceContinued`.
- `DebugCompilationUnit` (debug format 1, DWARF 4, language HLSL), `DebugEntryPoint` with the compiler name and the equivalent command line.
- `DebugFunction` with `DebugTypeFunction` for every HLSL function that has code in the module, including functions that were inlined; the entry
  function's wrapper has its own. `DebugFunctionDefinition` opens the entry block of each emitted function.
- `DebugLexicalBlock` for each brace block that holds code, nested to match the source; `DebugScope` before the instructions of each scope (and again
  at the start of each SPIR-V block, because a scope ends with its block) and `DebugInlinedAt` chains for calls the optimizer inlined.
- `DebugLine` before each statement's instructions (first line of the statement only, so a debugger highlights one line) and `DebugNoLine` where an
  instruction has no position.
- `DebugLocalVariable` with `DebugDeclare` for each named local or parameter that is still in memory, and `DebugLocalVariable` with `DebugValue` at
  function entry for parameters that arrive as values.
- `DebugGlobalVariable` for private and group-shared variables and for resources (a constant buffer is described by the struct it holds).
- `DebugTypeBasic`, `DebugTypeVector`, `DebugTypeMatrix` (one column per HLSL row, as in the SPIR-V type), `DebugTypeArray` and `DebugTypeComposite`
  with `DebugTypeMember` for structs. Sizes and offsets are natural packing marked `FlagUnknownPhysicalLayout`; they are for display, the real layout is in the
  `Offset` decorations. Resource and sampler types are opaque composites named after the HLSL type.

Where the positions come from: every IR instruction carries the HLSL position of the expression that made it (through `#include` and macro
expansion; a macro's expansion is attributed to its use). Scopes are found by asking which source function and brace block contain that position, so they
do not depend on how the optimizer reshaped the IR. Instructions that moved into a function without an inline record keep their lines but belong to the
function that holds them.

Compared with DXC's output for the same shaders (`-fspv-debug=vulkan-with-source`): the same instruction set, extension and record kinds, apart from
`DebugValue` for locals the optimizer moved into registers (we describe locals that stay in memory; compile with the optimizer off to see all of
them), `DebugTypeTemplate` for resource types (we name them), `DebugNoScope`, and `DebugOperation`. Counts of `DebugLine` differ because
statements are split into instructions differently.

## Not supported yet (reported as OSH5002)

comparison or bit-casting of whole matrices, bool matrices, products involving one-row/column matrices.

## Bindless: unbounded arrays and descriptor heaps

- **Unbounded arrays** (`Texture2D Tex[] : register(t0, space1)`, also samplers, structured, byte-address and constant buffers, read-write
  textures) are `OpTypeRuntimeArray` variables with capability `RuntimeDescriptorArray` and the extension `SPV_EXT_descriptor_indexing`,
  as DXC emits them. A run-time index needs no `*ArrayDynamicIndexing` capability on an unbounded array (DXC adds none either).
- **`NonUniformResourceIndex(i)`** adds `ShaderNonUniform` and decorates `NonUniform`: the index, the access chain into the array, any
  access chain derived from it, the loaded descriptor and the sampled image built from it, and data loaded through a non-uniform buffer pointer.
  It also declares the `*ArrayNonUniformIndexing` capability of the descriptor type. This is a superset of DXC's list: DXC omits the
  `StorageBufferArrayNonUniformIndexing` and `UniformBufferArrayNonUniformIndexing` capabilities for buffer arrays, which Vulkan's rules
  require, and decorates arithmetic on loaded data as well. The test `DescriptorIndexingMatchesDxc` states the allowed differences.
- **`ResourceDescriptorHeap[i]` / `SamplerDescriptorHeap[i]`** (Shader Model 6.6) follow DXC's default "emulated heap" mapping
  (`docs/SPIR-V.rst`, *ResourceDescriptorHeaps & SamplerDescriptorHeaps*): each resource type read from a heap becomes an unbounded array of
  that type, and all arrays of one heap share one binding. The resource heap takes the first binding of set 0 that no other resource uses,
  assigned after every other resource, the sampler heap the next one, unless `CompileOptions.ResourceHeapBinding` / `SamplerHeapBinding`
  (the CLI's `-fvk-bind-resource-heap <binding> <set>` and `-fvk-bind-sampler-heap`) name them; a heap given a binding another resource
  uses is OSH0330. A heap that is read as textures and buffers puts several descriptor types on one binding, so the host needs
  `VK_EXT_mutable_descriptor_type` (`DeviceFeatures.MutableDescriptorType`). The element must get a resource type where it is used: a
  variable initializer, an assignment, a cast, or an argument of a user function; anything else is OSH0336 with the line to write.
  Differences from DXC: one variable per resource type (DXC may repeat a type), a read-write texture from a heap has no declared image
  format (DXC derives one from the element type), and the counter heap (`-fvk-bind-counter-heap`, `IncrementCounter` on a heap buffer) is not
  supported (append and consume buffers cannot come from a heap, OSH0350).
