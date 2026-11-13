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
- **Packed dot products.** `dot4add_i8packed` / `dot4add_u8packed` are `OpSDot` / `OpUDot` with `PackedVectorFormat4x8Bit` followed by `OpIAdd`
  (the intrinsic wraps), declaring `DotProduct`, `DotProductInput4x8BitPacked` and `SPV_KHR_integer_dot_product`; DXC's output is the same.
  Vulkan needs `shaderIntegerDotProduct` (reflected as `DeviceFeatures.IntegerDotProduct`).
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
  The arithmetic that computes a `precise` value carries `NoContraction`: `FAdd`, `FSub`, `FMul`, `FDiv`, `FRem`, `FNegate`, the
  products (`OpVectorTimesMatrix`, `OpMatrixTimesVector`, `OpMatrixTimesMatrix`, `OpOuterProduct`) and `OpDot`, on the same
  operations DXC decorates (checked in `PreciseTests`). Matrix arithmetic is decorated on each row operation. Extended instructions
  (`Fma` from `mad`/`fma`, `FMix` from `lerp`, ...) and composites are not arithmetic instructions and cannot carry it; DXC leaves them
  undecorated too.
- **`mad` and `fma` are not guaranteed to be fused.** A floating-point `mad(a, b, c)` or `fma(a, b, c)` becomes
  GLSL.std.450 `Fma` in SPIR-V, `fma` in WGSL and `fma` in MSL. Only Metal defines `fma` as a single rounding.
  Vulkan's precision rules let `Fma` be a multiply and an add, each rounded, and WGSL's accuracy for `fma` is
  inherited from `x * y + z`, so on Vulkan and WebGPU a driver may or may not fuse. No shader construct forces
  fusion there; `precise` only forbids it. Code that needs bit-identical results across devices (quantization,
  hashing, exact rounding) should compute that step in integer arithmetic, or make the result independent of the
  rounding of `a * b`.

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

## Cooperative matrices and bfloat16

`CooperativeMatrix<T, Rows, Cols, MatrixUse::A|B|Accumulator>` becomes `OpTypeCooperativeMatrixKHR` with subgroup scope (capability `CooperativeMatrixKHR`,
extension `SPV_KHR_cooperative_matrix`); `Load`/`Store` are `OpCooperativeMatrixLoadKHR`/`StoreKHR` with the layout (`RowMajorKHR`/`ColumnMajorKHR`) and the
stride as given, from a (RW)StructuredBuffer or a `groupshared` array; `MulAdd` is `OpCooperativeMatrixMulAddKHR`; `Zero()` is a composite construct of a zero;
`Length()` and `m[i]` are `OpCooperativeMatrixLengthKHR` and an access chain into the matrix. The element order of `m[i]` is the driver's, so only element-wise
work that does not depend on the position is portable. Every call must be reached by the whole subgroup.

An entry point that uses a cooperative matrix switches to the Vulkan memory model (`OpMemoryModel Logical Vulkan`, capability `VulkanMemoryModel`): loads and
stores of Workgroup, StorageBuffer and Uniform pointers carry `NonPrivatePointer`, and barriers add `MakeAvailable|MakeVisible`. Other entry points keep the
GLSL450 model, so their output is unchanged. The host needs `VK_KHR_cooperative_matrix` with the `cooperativeMatrix` feature and the `vulkanMemoryModel` feature.
The module stays at SPIR-V 1.5; SPIR-V below 1.6 may use cooperative matrices only in a pipeline created with
`VK_PIPELINE_SHADER_STAGE_CREATE_REQUIRE_FULL_SUBGROUPS_BIT` (`VK_EXT_subgroup_size_control`, workgroup size a multiple of the subgroup size; on Vulkan 1.3 the
flag is core). Which shapes a device offers comes from `vkGetPhysicalDeviceCooperativeMatrixPropertiesKHR`; the compiler accepts any shape and element
combination the language allows and leaves that check to the host (on the RTX 4070: 16x16x16 f16 and bf16 with a float accumulator). Integer matrices (int8 operands
with an int32 accumulator) are refused with OSH0337 for now.

`bfloat16_t` is `OpTypeFloat 16 BFloat16KHR` (capability `BFloat16TypeKHR`, `SPV_KHR_bfloat16`; with cooperative matrices also
`BFloat16CooperativeMatrixKHR`). Arithmetic is done in float: operands convert to float, results convert back, rounding to nearest even; conversions to and from
anything but float go through float, because `OpFConvert` between bfloat16 and other widths is not allowed. Vectors and matrices of bfloat16 are error OSH0311.
Reflection `DeviceFeatures`: `CooperativeMatrix`, `VulkanMemoryModel`, `FullSubgroups` (set exactly on the entry points that use cooperative matrices: create their pipeline with `REQUIRE_FULL_SUBGROUPS`; it travels with the device features but is a pipeline creation flag), `BFloat16`, `BFloat16CooperativeMatrix` (bfloat16 buffers also set `StorageBuffer16`).
The corpus cases `coop_f16`, `coop_bf16` and `coop_layouts` run on a Vulkan device against a CPU result (DXC cannot compile this surface).

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
