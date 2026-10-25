# The intermediate representation

One typed IR sits between the front end and every backend (SPIR-V, MSL, WGSL). Its types live in
`src/OneShader/IR/`; the HLSL type system it shares is in `src/OneShader/Types/`.

## Shape: SSA values, structured control flow, memory for locals

- **Values are SSA.** Every instruction that yields a value defines it once; operands are
  `IrValue`s (`Constant`, `Argument`, `GlobalVariable`, `Instruction`). `Phi` exists, but the lowering
  from HLSL starts from the simplest form: each HLSL local is an `Alloca` in the entry block
  accessed with `Load`/`Store`, and a later promotion pass can turn those into SSA with phis.
  Backends must accept both forms.
- **Control flow is a CFG that keeps its structure.** Blocks end in exactly one terminator (`Branch`,
  `CondBranch`, `Switch`, `Return`, `Kill`, `Unreachable`). Header blocks carry the same markers
  SPIR-V needs:
  - `StructureKind.Selection`: ends in `CondBranch` or `Switch`; `MergeBlock` is where the arms rejoin.
  - `StructureKind.Loop`: ends in `Branch`; `MergeBlock` is the exit, `ContinueBlock` is the block
    that branches back to the header. `continue` is a branch to the continue block, `break` a
    branch to the merge block of the innermost loop or switch.
  - `ControlHint` carries `[unroll]`, `[loop]`, `[flatten]`, `[branch]`.
  Passes must keep these markers valid (the verifier checks they exist and point into the function).
  Because they are explicit, SPIR-V gets its `OpSelectionMerge`/`OpLoopMerge` directly and the text
  backends can rebuild `if`/`for`/`switch` without a general structurizer.
- **Addressing is logical.** `PointerType(AddressSpace, pointee)` values come only from `Alloca`,
  globals and `AccessChain`. There is no pointer arithmetic or pointer-to-pointer. `AccessChain`
  takes a base pointer plus indices (constant `int` for struct members, any integer for arrays and
  vector components).
- **Types are `HlslType`.** Scalars, vectors, matrices, arrays, structs, resources and
  pointers are the same classes the front end uses, so lowering does not translate types. Matrix
  constants are composites of rows.

## Module layout

`IrModule` holds `Globals`, `Functions` and `EntryPoints` in source order (the dump and every backend
follow that order). An `EntryPoint` names a no-parameter void function and lists its `Inputs` and
`Outputs`: stage variables are module globals (`GlobalKind.Input`/`Output`) with a `Location` or
`BuiltIn`; the lowering wraps the HLSL entry function that takes and returns structs.

Resources are globals of pointer-to-`ResourceType` in `AddressSpace.UniformConstant` (textures,
samplers, typed buffers), `Uniform` (constant buffers) or `StorageBuffer` (structured and
byte-address buffers), with `Set`/`Binding` and, for buffers, the `TypeLayout` from
`BufferLayout`. A `cbuffer` is a `ConstantBuffer` resource whose element is a struct of its members.
Access chains into `ConstantBuffer<T>` start inside `T` (the first index selects a member of `T`);
chains into `StructuredBuffer<T>` start with the element index, then continue inside `T`; a chain
with one index into a byte-address buffer addresses a 32-bit word (used for atomics).

## Opcode conventions

Arithmetic and comparisons are component-wise and typed by their operands; signedness and
float-versus-integer come from the operand type, there are no separate `fadd`/`iadd` opcodes. `Mul`
on matrices is component-wise; the matrix product is `MatMul`. `Precise` on an instruction forbids
reassociation, fusing and algebraic rewrites. Resource operands of texture and buffer opcodes are
pointers (the global, or an access chain into a resource array), never loaded handles.
Texture operands, in order:

| Opcode | Operands | Immediates |
|---|---|---|
| `TextureSample` | texture, sampler, coordinate | constant texel offset (0 to 3 values) |
| `TextureSampleBias` / `Level` | texture, sampler, coordinate, bias or level | offset |
| `TextureSampleGrad` | texture, sampler, coordinate, ddx, ddy | offset |
| `TextureSampleCompare` / `CompareLevelZero` | texture, comparison sampler, coordinate, reference | offset |
| `TextureGather` | texture, sampler, coordinate | component, then offset |
| `TextureGatherCompare` | texture, comparison sampler, coordinate, reference | offset |
| `TextureLoad` | texture, integer coordinate, optional level (or sample index when multisampled) | offset |
| `TextureStore` | texture, integer coordinate, value | |
| `TextureQuerySize` | texture, optional level | |
| `TextureQueryLevels`, `TextureQuerySamples` | texture | |
| `TextureQueryLod` | texture, sampler, coordinate | |
| `ByteAddressLoad` / `Store` | buffer, byte offset (, value) | |

`AtomicRmw` and `AtomicCompareExchange` take a pointer to the target (a groupshared variable, a buffer element, or a
one-index access chain into a byte-address buffer, which addresses a 32-bit word). On a read-write texture the first
operand is the texture pointer and the second the integer coordinate; the values follow (`AtomicRmw`: value,
`AtomicCompareExchange`: comparand, then new value). Both return the original value. `Barrier`'s immediate is a
`BarrierFlags` combination. A global's `ImageFormat` (a name from `ImageFormats`, the `[[vk::image_format]]` value) says
how a storage texture is laid out; without it the format is unspecified.

Array textures put the layer in the last coordinate component. `CallIntrinsic` takes the operands of
the HLSL intrinsic in HLSL order; intrinsics with `out` parameters (`modf`, `frexp`, `sincos`)
take pointers. Functions with resource-pointer parameters must be inlined before a backend runs.

## Dump format

`IrPrinter.Print` is the textual form tests compare. Values are numbered in print order per function
(`%0`, `%1`; parameters are `%arg0`), blocks by position (`bb0`), so output does not depend on object
identity, hash order or names. Source names appear only in `;` comments. Example:

```
func @f(%arg0: int) -> int {
bb0: ; (entry)
  %0 = alloca : ptr<function, int> ; sum
  store %0, 0:int
  br bb1
bb1: ; loop merge=bb5 continue=bb4 hint=dontunroll (for.header)
  ...
```

## Verifier

`IrVerifier.Verify` returns messages and never throws. It checks block shape (one terminator, last),
parent links, operand ownership and in-block ordering, phi placement, the types of loads, stores,
arithmetic, comparisons, selects, branches, calls and phis, and the structure markers. For reachable
blocks it also checks that every definition dominates its uses and that each phi names exactly its
block's predecessors (`Cfg` in `src/OneShader/IR/Cfg.cs` provides predecessors, reverse post-order,
dominators and dominance frontiers). Tests run it after building and after every optimizer pass.

## Optimizer

`CompileOptions.Optimize` (default true) runs `Optimizer.Run` (`src/OneShader/Optimization/`) between lowering
and the backends. Reflection is built from the unoptimized module, so it does not change with the optimizer.

1. **Inlining** of every call, callees first. A callee whose only `ret` is outside every structured construct is
   spliced in directly. Any other callee is wrapped in a loop that runs once: a `ret` stores the result and breaks;
   a `ret` inside an inner loop or switch sets a flag and breaks, and that construct's merge block tests the flag
   and breaks outward, so each branch is a legal SPIR-V break. Recursive calls are left alone, and growth is capped.
2. Per function, repeated until nothing changes: **SROA** (structs and small arrays with constant indices become one
   variable per element), **mem2reg** (scalars, vectors and matrices become SSA values; phis are placed on the
   dominance frontier, pruned by liveness), **combine** (constant folding and exact algebraic identities; `precise`
   instructions are only folded), **CSE** along the dominator tree (pure instructions, plus loads from constant
   buffers, resources and stage inputs), **DCE** (mark and sweep, plus stores to variables nobody reads) and
   **CFG simplification** (constant branches, unreachable blocks, straight-line merging).
3. Folding is limited to results that are the same on every implementation: wrapping integer arithmetic, masked
   shifts, float add/sub/mul/div/rem in the operand's own precision. Division by zero, `INT_MIN / -1`, NaN and
   infinite or subnormal operands and results, out-of-range float-to-int conversions and transcendental
   functions are left to the GPU.
4. Merge and continue blocks that a live header names are kept even when unreachable (reduced to `unreachable` or a
   branch to the header); headers, merge blocks and continue blocks are never merged away.

If a pass fails, `Optimizer.Run` returns a message instead of throwing and `Compile` falls back to the unoptimized
module with warning OSH6001. Setting `Optimizer.VerifyEachPass` (tests do) verifies the IR after every pass.

## Decisions worth knowing

- One `Instruction` class for all opcodes, so passes can rewrite in place; `IrBuilder` provides typed
  helpers. There are no use lists: passes build a user map per run (`IrUtil.BuildUsers`) or collect
  replacements in a map and apply them in one sweep (`IrUtil.ApplyReplacements`).
- Constants keep exact bits (`Constant.Bits`), so folding wraps integers as HLSL does and keeps float
  results in the operand's own precision.
- Block names and value names are hints and never take part in equality or output ordering.

## Lowering conventions (front end)

- An HLSL entry point becomes two functions: `Name_body`, the user function with its original parameters,
  and a wrapper named `Name` that loads the input globals into a struct, calls the body and stores the
  outputs. Only the wrapper is an `EntryPoint`.
- Resource arrays are globals whose value type is an `ArrayType` of the resource (`ResourceCount` set);
  an element is an `AccessChain` with the index. Constant-buffer members are `AccessChain(global, type, memberIndex)`.
  A function that takes a resource is compiled once per distinct resource it is called with; a run-time index into a
  resource array is passed to that copy as an ordinary `int` argument.
- `NonUniformResourceIndex(i)` is the intrinsic `NonUniformIndex`: it yields `i` and tells the SPIR-V backend to
  decorate the access chain, the loaded handle and the sampled image built from it as `NonUniform`.
- An append or consume buffer comes with a second global, `counter.var.<name>`, a `RWByteAddressBuffer` in the same set.
  `Append` is `AtomicRmw(Add, counter[0], 1)` followed by a store at the returned slot; `Consume` is an
  `AtomicRmw(Add, counter[0], 0xFFFFFFFF)` followed by a load at the returned value minus one.
- Non-constant `static` initializers run in a function `init_globals` that each wrapper calls first.
- `&&`, `||` and non-pure `?:` are control flow with a `Phi`; pure `?:` is `Select`.
- Initializer lists follow HLSL's flat rules: a run of values fills an aggregate in order (a vector-valued expression
  supplies several scalars) and a braced sub-list starts a new element.
- Dead code after `return`, `break`, `continue` and `discard` is not lowered; merge blocks nothing reaches end in `Unreachable`.
