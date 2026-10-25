# The MSL backend

`MslBackend.Emit(IrModule, entryPointName, MslOptions?, DiagnosticBag)` writes one entry point, and everything it
reaches, as a single Metal Shading Language source file (language version 2.4 or newer; macOS 12 and iOS 15).
`Compile` with `CompileTarget.Msl` returns one UTF-8 text blob per entry point. Code is in `src/OneShader/Msl/`.
The same module and options always give the same text. Unsupported constructs are `OSH51xx` errors, never wrong code.

## Mapping decisions

- **Functions.** `vertex`, `fragment` and `kernel` functions. The entry function's name is the HLSL name, except
  reserved words (`main` becomes `main0`, `fragment` becomes `fragment0`): `MslBackend.EntryFunctionName`.
- **Push constants** are a `constant T& name [[buffer(n)]]` argument, where `n` is the next buffer index in declaration order
  like any constant buffer (recorded in reflection's `Msl` slot of the push block). The host fills it with `setVertexBytes`,
  `setFragmentBytes` or `setBytes`; the struct has the same byte layout as the SPIR-V block.
- **Module-scope variables** do not exist in MSL. Stage variables, `static` and `groupshared` variables become locals of
  the entry function; every helper that touches one (directly or through its callees) receives it as an extra reference
  parameter. Resources are entry-function arguments and are passed to helpers the same way.
- **Matrices.** HLSL `floatRxC` is MSL `floatRxC` (R columns of C components), so `m[i]` is row `i`, and `mul(a, b)` is
  `b * a`. Only float and half matrices exist in MSL; others are an error.
- **Buffers keep the byte layout of the SPIR-V target.** Every buffer struct is emitted with `packed_*` vectors, padded
  array elements and explicit `uchar padN[]` members so each member sits at its reflected offset; `bool` is a 32-bit
  `uint`. Matrices are stored as lines (columns when column-major) and converted when loaded or stored. Compile the
  output with `-DONESHADER_CHECK_LAYOUT` to turn on `static_assert`s of every struct size and member offset against the
  reflected layout.
- **Resource indices.** Buffers (constant, structured, byte-address), textures (including `Buffer<T>`) and samplers each
  count up from 0 in declaration order over the whole file, and an array of N resources takes N indices. All entry
  points of a file see the same indices; they are the `Msl` `TargetSlot` in reflection. Direct indices are used rather
  than argument buffers, because they need no encoder objects on the host and are trivially deterministic. Limits: 31
  buffers, 128 textures, 16 samplers per function (`OSH5105`). A vertex function with attributes reads them from buffer
  index `MslBackend.VertexBufferIndex` (30), one interleaved buffer using the reflected offsets and stride; resources
  may not use that index.
- **Stage I/O.** Vertex inputs are a `[[stage_in]]` struct with `[[attribute(n)]]`; vertex outputs and fragment inputs
  use `[[user(locnN)]]` with `[[flat]]`, `[[center_no_perspective]]`, `[[centroid_perspective]]`, `[[sample_perspective]]`;
  fragment outputs use `[[color(n)]]`, `[[depth(any)]]`, `[[sample_mask]]`. `SV_VertexID`/`SV_InstanceID` are
  `[[vertex_id]]` and `[[instance_id]]`, which on Metal already include the draw's base vertex and base instance, as
  `VertexIndex`/`InstanceIndex` do in the SPIR-V target (`tools/metal-runner/probe-draw-ids.swift` shows it). Clip distances are
  `[[clip_distance]]`. Clip-space Y points up in Metal and down in Vulkan: flip with the viewport, the shader does not.
- **Textures.** Textures used with comparison sampling become `depth*` types. Read-write textures get `read`, `write`
  or `read_write` access from their use. Array layers are split from the coordinate. Comparison samplers need their
  compare function set on the host sampler.
- **Control flow.** Rebuilt from the IR's merge/continue markers; short-circuit branches without markers use
  post-dominators. The loop's continue block is repeated before each `continue`.
- **Atomics** cast the address to `atomic_int`/`atomic_uint` with relaxed ordering. Compare-exchange loops over spurious failure.

- **Resource arrays** (`Texture2D T[4]`, `SamplerState S[2]`, arrays of structured, byte-address and constant buffers) are
  `array<texture2d<float>, 4>`, `array<sampler, 2>`, `array<device T*, N>` and `array<constant T*, N>` arguments; element
  `i` has Metal index `base + i`, which is what reflection reports as the slot of the array (its first index).
  `NonUniformResourceIndex(i)` is just `i`: Metal needs no hint.
- **Append and consume buffers** use the hidden counter buffer as a second buffer argument, like the SPIR-V target:
  `Append` adds one with an atomic and stores at the old value, `Consume` subtracts one and loads. `GetDimensions` on
  them is the same error as on any structured buffer.

## Not supported (errors)

`double` and 64-bit float; `SV_CullDistance`; `GetDimensions` on structured buffers (`ArrayLength`: Metal cannot
query it); atomics on textures (Metal 3.1); wave and quad intrinsics; integer/bool matrices; 64-bit integer vectors in
buffers; unbounded resource arrays; jumps out of several nested loops at once; comparison sampling on 1D/3D textures; texel offsets on cube/1D textures; overlapping `packoffset` members.

Known deviations: `discard` in a helper that was not inlined calls `discard_fragment()` and returns, so the caller keeps running with writes suppressed; memory-only barriers are emitted as full threadgroup barriers (MSL 2.4 has no fence); signed integer
overflow is undefined in MSL where HLSL wraps; Metal enables fast-math by default, so NaN and infinity handling is not
guaranteed unless the host disables fast math.

`precise` is honoured whatever the host's math mode. A function with precise arithmetic starts with
`#pragma clang fp reassociate(off)` and `#pragma clang fp contract(off)` (no fused multiply-add, no reordered sums; measured with
`xcrun metal`: fast math sets `fast` on every operation, these two pragmas remove `reassoc` and `contract`), and a precise division is
`precise::divide`, because there is no pragma that stops `a / b` becoming `a * (1 / b)`. The flags `nnan`, `ninf`, `nsz`, `arcp` and
`afn` stay set under fast math. `tools/run-metal-parity.sh run` with `ONESHADER_METAL_MATH=fast` and `safe` both match DXC on
`lang_precise_math`; without the pragmas the fast run differs in 11 bytes.

A `switch` whose cases fall through is written as a C++ `switch` with the same fall-through; values of the case's phis that arrive
from the switch are assigned before it, and a `break` or `continue` inside a nested `if` in a case is written out.

## Checking

`tools/validate-msl.sh <dir>` compiles every `.metal` file in a directory with `xcrun -sdk macosx metal -c` (layout
asserts on) and packs it with `metallib`. Produce the files with
`ONESHADER_MSL_OUT=<dir> dotnet test tests/OneShader.Tests --filter-method '*MslBackendTests*'` (optimized files in
`<dir>`, unoptimized in `<dir>/O0`).

## Running on a Metal GPU

`tools/run-metal-parity.sh` runs every corpus case as MSL on a Metal device and compares it with DXC's SPIR-V on Vulkan.
Three steps share one exchange directory: `export` (a machine with Vulkan and DXC) writes the MSL, the inputs and the
reference outputs; `run` (a Mac with Xcode, no .NET needed) builds `tools/metal-runner` and executes each case with Metal API
and shader validation on, so a validation message fails the case; `compare` prints the per-case table. Allowed differences
are in `corpus/metal-tolerances.txt`; everything else must match as the Vulkan parity suite requires.

The runner draws with a viewport of negative height. Clip-space Y points up in Metal and down in Vulkan, and the corpus
shaders are written for Vulkan; the flipped viewport puts the same vertex positions on the same pixels (including
`SV_Position`). The mirrored viewport reverses the winding Metal sees, so Vulkan's counter-clockwise front face is also
counter-clockwise on Metal (back-face culling on `pbr_forward` shows it). Depth range and the
texture origin already agree. Vertex buffer binding N is Metal buffer 30 - N, described to Metal with a vertex descriptor.
