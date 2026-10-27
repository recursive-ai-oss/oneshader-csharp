# Corpus

Shaders and case descriptions used by the execution-parity tests in `tests/OneShader.ParityTests`.
Every file here was written for this repository and is licensed `MIT OR Apache-2.0`; each shader
starts with an `SPDX-License-Identifier` line. No third-party shader is included. A third-party
sample may only be added with a permissive licence (MIT, Apache-2.0, BSD or CC0) recorded in this file.

The shaders are modern HLSL in the subset every target can express (the wave and quad cases below need the
target's subgroup support; their checked results do not depend on the subgroup size), compile
with `dxc -spirv -fspv-target-env=vulkan1.2`, and give every resource an explicit
`[[vk::binding(binding, set)]]`.

## Shaders

| File | What it does | Provenance | Licence |
|---|---|---|---|
| `2d/sprite.hlsl` | Batched textured, tinted sprites: vertex buffer, view-projection constant buffer, atlas and sampler | written for this repository | MIT OR Apache-2.0 |
| `2d/blit.hlsl` | Fullscreen triangle from `SV_VertexID`, texture copy | written for this repository | MIT OR Apache-2.0 |
| `2d/ui_rect.hlsl` | Instanced UI rectangles from a structured buffer: four corner radii, border, solid/linear/radial fill, analytic anti-aliasing, premultiplied output | written for this repository | MIT OR Apache-2.0 |
| `2d/sdf_text.hlsl` | Single-channel SDF glyphs with smoothstep anti-aliasing and outline | written for this repository | MIT OR Apache-2.0 |
| `2d/msdf_text.hlsl` | Multi-channel SDF glyphs (median of three) with outline | written for this repository | MIT OR Apache-2.0 |
| `2d/tilemap.hlsl` | Tile grid from a tile-index buffer and a texture array, one instance per cell | written for this repository | MIT OR Apache-2.0 |
| `2d/renderer/quad.hlsl` | A 2D renderer's instanced textured quads (sprites, composites, post passes), sRGB vertex colours, premultiplied output | from a 2D game renderer by the same authors, unchanged | MIT OR Apache-2.0 |
| `2d/renderer/tile.hlsl` | The same renderer's tilemap program: tile indices from a `Texture2DArray<uint>`, level of detail | from a 2D game renderer by the same authors, unchanged | MIT OR Apache-2.0 |
| `2d/renderer/ui.hlsl` | The same renderer's UI display-list program: rounded rectangles, borders, gradients, clip table | from a 2D game renderer by the same authors, unchanged | MIT OR Apache-2.0 |
| `2d/blur.hlsl` | Separable gaussian blur, direction and radius in a constant buffer | written for this repository | MIT OR Apache-2.0 |
| `2d/color_grade.hlsl` | Exposure, tone mapping, colour matrix, gamma, vignette | written for this repository | MIT OR Apache-2.0 |
| `3d/brdf.hlsli` | Shared GGX / Smith / Schlick helpers and light evaluation, included by the 3D shaders | written for this repository | MIT OR Apache-2.0 |
| `3d/pbr_forward.hlsl` | glTF metal/rough forward shading: base colour, normal and ORM maps, point/spot/directional lights, row- and column-major matrices in constant buffers | written for this repository | MIT OR Apache-2.0 |
| `3d/clustered_lights.hlsl` | Clustered forward shading: per-cluster light ranges and index lists in structured buffers, exponential depth slices | written for this repository | MIT OR Apache-2.0 |
| `3d/shadow_depth.hlsl` | Depth-only instanced pass (no pixel stage) with a structured buffer of world matrices | written for this repository | MIT OR Apache-2.0 |
| `3d/shadow_cascades.hlsl` | Four-cascade shadow lookup: cascade selection by view depth, 3x3 PCF with `SampleCmpLevelZero` on a depth texture array | written for this repository | MIT OR Apache-2.0 |
| `3d/stage_io.hlsl` | Array and matrix stage variables (a float2x2 vertex input, float4[2] and float2x2 interpolants), `SV_ClipDistance`, `GatherCmp` on a depth array and `CalculateLevelOfDetail` | written for this repository | MIT OR Apache-2.0 |
| `3d/ssao.hlsl` | Hemisphere-kernel ambient occlusion from linear depth and view-space normals with a tiled noise rotation | written for this repository | MIT OR Apache-2.0 |
| `3d/bloom_downsample.hlsl` | 13-tap bloom downsample with optional brightness-weighted first level and threshold | written for this repository | MIT OR Apache-2.0 |
| `3d/bloom_upsample.hlsl` | Tent-filter bloom upsample blended into the larger level | written for this repository | MIT OR Apache-2.0 |
| `3d/tonemap.hlsl` | Bloom composite, exposure, ACES-style and extended Reinhard curves, sRGB transfer | written for this repository | MIT OR Apache-2.0 |
| `3d/ibl.hlsl` | Image-based lighting (irradiance cube, prefiltered cube with mips, BRDF table) and a sky pass | written for this repository | MIT OR Apache-2.0 |
| `3d/skinned.hlsl` | Linear-blend skinning from a joint-matrix structured buffer and a second vertex buffer | written for this repository | MIT OR Apache-2.0 |
| `compute/cull_instances.hlsl` | Frustum culling of bounding spheres into an index list and indirect draw arguments with group-shared and global atomics | written for this repository | MIT OR Apache-2.0 |
| `compute/cluster_build.hlsl` | View-space bounds of every cluster of a clustered light grid | written for this repository | MIT OR Apache-2.0 |
| `compute/ibl_prefilter.hlsl` | GGX importance-sampled prefilter of a cube map into a layered `RWTexture2DArray`, Hammersley sequence via `reversebits`, density-based mip selection | written for this repository | MIT OR Apache-2.0 |
| `compute/light_cull.hlsl` | Assigns point lights to clusters (sphere against box) into fixed-size per-cluster lists | written for this repository | MIT OR Apache-2.0 |
| `compute/depth_bounds.hlsl` | Per-tile minimum and maximum depth with group-shared integer atomics on the float bits and `Texture2D.Load` | written for this repository | MIT OR Apache-2.0 |
| `compute/wave_scan_compact.hlsl` | Group-wide exclusive scan and stream compaction from `WavePrefixSum`, `WavePrefixCountBits`, `WaveActiveSum`, `WaveActiveCountBits` and `WaveReadLaneFirst`; wave totals combine through group-shared memory, so the result is the same for any subgroup size | written for this repository | MIT OR Apache-2.0 |
| `compute/wave_reduce.hlsl` | Sixteen group reductions from `WaveActiveSum`/`Product`/`Min`/`Max`/`BitAnd`/`BitOr`/`BitXor` on uint, int, float and vector values | written for this repository | MIT OR Apache-2.0 |
| `compute/wave_ballot.hlsl` | `WaveActiveBallot`, `AnyTrue`, `AllTrue`, `AllEqual`, `CountBits`, lane index and count, `WaveIsFirstLane`, `WaveReadLaneAt`: size-independent totals plus a per-lane record compared with DXC | written for this repository | MIT OR Apache-2.0 |
| `2d/quad_ops.hlsl` | `QuadReadAcrossX`/`Y`/`Diagonal` and `QuadReadLaneAt` in a pixel shader, checked against `ddx_fine`/`ddy_fine` | written for this repository | MIT OR Apache-2.0 |
| `compute/prefix_sum.hlsl` | Block-wise exclusive scan in group-shared memory with barriers | written for this repository | MIT OR Apache-2.0 |
| `compute/stage_layouts.hlsl` | Constant-buffer `bool` members, `StructuredBuffer<float3>` elements, `f32tof16`/`f16tof32`, `faceforward` and `log10` in one kernel (run with the flags on and off) | written for this repository | MIT OR Apache-2.0 |
| `compute/histogram.hlsl` | Luminance histogram with group-shared atomics into a read-write buffer | written for this repository | MIT OR Apache-2.0 |
| `compute/image_filter.hlsl` | Tiled 3x3 sharpen on a `RWTexture2D` through group-shared memory | written for this repository | MIT OR Apache-2.0 |
| `compute/particles.hlsl` | Per-particle integration over a `RWStructuredBuffer` of structs | written for this repository | MIT OR Apache-2.0 |
| `3d/push_lit.hlsl` | A lit sphere whose per-draw model matrix, tint, palette index, light direction, uv scale and weights (112 bytes) come from a `[[vk::push_constant]] ConstantBuffer<T>` read by both the vertex and pixel stage | written for this repository | MIT OR Apache-2.0 |
| `compute/push_kernel.hlsl` | A kernel configured only by a struct-typed `[[vk::push_constant]]` global (64 bytes, with a `float3` and padding) | written for this repository | MIT OR Apache-2.0 |
| `lang/templates.hlsl` | Function and struct templates: deduced and explicit arguments, value parameters, defaults | written for this repository | MIT OR Apache-2.0 |
| `lang/members.hlsl` | Member functions, this, static members, calls on buffer elements | written for this repository | MIT OR Apache-2.0 |
| `lang/switch_flow.hlsl` | `switch` with fall-through between cases, stacked labels, a `default` in the middle, negative labels, `return` from a case and a switch inside a loop with `continue` | written for this repository | MIT OR Apache-2.0 |
| `lang/precise_math.hlsl` | `precise` variables and functions: a multiply-add that differs when fused, `(x + y) - x`, Kahan summation | written for this repository | MIT OR Apache-2.0 |
| `lang/inheritance.hlsl` | Struct inheritance: inherited members, hiding, base-typed parameters, buffers of derived structs | written for this repository | MIT OR Apache-2.0 |
| `lang/operators.hlsl` | Member operator overloads: arithmetic, bitwise, comparison, subscript, call | written for this repository | MIT OR Apache-2.0 |
| `lang/sizeof_static.hlsl` | sizeof constants and non-const static locals | written for this repository | MIT OR Apache-2.0 |
| `lang/resource_arrays.hlsl` | Bounded resource arrays: textures and samplers chosen at run time, elements passed to functions, non-uniform index, arrays of read-only buffers and constant buffers | written for this repository | MIT OR Apache-2.0 |
| `lang/statics_and_literals.hlsl` | Static struct members, member functions defined after the struct, template deduction through `Box<T>`, a cbuffer named like its member, `S s = cb;`, wide literal arithmetic | written for this repository | MIT OR Apache-2.0 |
| `compute/append_consume.hlsl` | Work list drained with `ConsumeStructuredBuffer` into an `AppendStructuredBuffer`, explicit counter bindings | written for this repository | MIT OR Apache-2.0 |
| `lang/resource_locals.hlsl` | Resource-typed locals and parameters resolved at compile time | written for this repository | MIT OR Apache-2.0 |

## Cases

`cases/*.case` describe how to run a shader: entry points, the inputs (generated from the text
in the file, so no binary data is stored), the draw or dispatch, the outputs to compare and the
image tolerance. The format is documented on `CaseFile` in the parity test project. Cases are
written for this repository and licensed `MIT OR Apache-2.0`.

| Case | Shader | Kind | Checks |
|---|---|---|---|
| `sprite` | `2d/sprite.hlsl` | draw, indexed | image, tolerance 1 |
| `blit` | `2d/blit.hlsl` | draw | image, tolerance 1 |
| `ui_rect` | `2d/ui_rect.hlsl` | draw, 9 instances, premultiplied blend over a checkerboard | image, tolerance 2 |
| `sdf_text` | `2d/sdf_text.hlsl` | draw, indexed | image, tolerance 1 |
| `msdf_text` | `2d/msdf_text.hlsl` | draw, indexed | image, tolerance 1 |
| `tilemap` | `2d/tilemap.hlsl` | draw, 48 instances | image, tolerance 1 |
| `blur_h`, `blur_v` | `2d/blur.hlsl` | draw | image, tolerance 1 |
| `color_grade` | `2d/color_grade.hlsl` | draw | image, tolerance 1 |
| `pbr_forward` | `3d/pbr_forward.hlsl` | draw, indexed, depth, back-face culling, HDR target | float image, relative tolerance 0.001 |
| `clustered_lights` | `3d/clustered_lights.hlsl` | draw, indexed sphere and floor, depth, HDR target | float image, relative tolerance 0.001 |
| `shadow_depth` | `3d/shadow_depth.hlsl` | depth-only draw, 5 instances | depth buffer read back, relative tolerance 1e-5 |
| `shadow_cascades` | `3d/shadow_cascades.hlsl` | draw, indexed, depth texture array with comparison sampler | image, tolerance 1 |
| `ssao` | `3d/ssao.hlsl` | fullscreen draw into an `R32F` target | float image, relative tolerance 1e-4 |
| `bloom_down_first`, `bloom_down`, `bloom_up` | `3d/bloom_downsample.hlsl`, `3d/bloom_upsample.hlsl` | fullscreen draws into `RGBA16F` | float image, relative tolerance 0.001 |
| `tonemap_aces`, `tonemap_reinhard` | `3d/tonemap.hlsl` | fullscreen draw | image, tolerance 1 |
| `ibl_gold`, `ibl_plastic`, `ibl_sky` | `3d/ibl.hlsl` | draws with cube maps (6 faces, 5 mips) and a 2D table, trilinear sampling | float image, relative tolerance 0.001 |
| `skinned` | `3d/skinned.hlsl` | draw, indexed, two vertex buffers, depth | image, tolerance 1 |
| `lang_resource_arrays` | `lang/resource_arrays.hlsl` | dispatch, descriptor arrays (4 textures, 2 samplers, 2 buffers, 2 constant buffers) | buffer exact vs DXC |
| `lang_statics_and_literals` | `lang/statics_and_literals.hlsl` | dispatch | buffer exact vs DXC |
| `lang_switch_flow` | `lang/switch_flow.hlsl` | dispatch, 64 threads | buffers exact vs DXC |
| `lang_precise_math` | `lang/precise_math.hlsl` | dispatch, one thread | buffer exact vs DXC (the WGSL targets skip it: OSH5205) |
| `append_consume` | `compute/append_consume.hlsl` | dispatch, 56 items, 48 consumed and appended when even | buffers exact vs DXC (result list as a sorted set), counters |
| `cull_instances` | `compute/cull_instances.hlsl` | dispatch, 1000 instances | buffers exact (index list as a sorted set), CPU result |
| `cluster_build` | `compute/cluster_build.hlsl` | dispatch, 3456 clusters | buffer, CPU result within 1e-4 relative |
| `shadow_cascades_poisson` | `3d/shadow_cascades.hlsl` (`PSSpiral`) | same scene, eight rotated taps from a `static const` array | image, tolerance 2 |
| `ibl_prefilter` | `compute/ibl_prefilter.hlsl` | dispatch, 6 faces, cube texture with mips in, layered storage image out | float image, relative tolerance 0.001 |
| `light_cull` | `compute/light_cull.hlsl` | dispatch, 512 clusters, 40 lights | buffers exact, CPU result |
| `depth_bounds` | `compute/depth_bounds.hlsl` | dispatch, 64 tiles | buffer exact, CPU result |
| `prefix_sum` | `compute/prefix_sum.hlsl` | dispatch | buffers exact, CPU result |
| `wave_scan_compact` | `compute/wave_scan_compact.hlsl` | dispatch, 4 groups of 256 | buffers exact, CPU result (any subgroup size) |
| `wave_reduce` | `compute/wave_reduce.hlsl` | dispatch, 4 groups of 256 | buffer exact, CPU result (any subgroup size) |
| `wave_ballot` | `compute/wave_ballot.hlsl` | dispatch, 4 groups of 256 | totals buffer exact vs CPU (any subgroup size up to 128 lanes); per-lane buffer exact vs DXC on the same device |
| `quad_ops` | `2d/quad_ops.hlsl` | fullscreen triangle, 64x64 | image, tolerance 2 |
| `histogram` | `compute/histogram.hlsl` | dispatch | buffer exact, CPU result |
| `image_filter` | `compute/image_filter.hlsl` | dispatch | image, CPU result within one 8-bit step |
| `particles` | `compute/particles.hlsl` | 3 dispatches | buffer bit-exact between compilers, CPU result within 1e-4 relative |
| `push_lit` | `3d/push_lit.hlsl` | draw, indexed, depth, back-face culling; push constants via `vkCmdPushConstants`, a Metal `setBytes` call and a uniform buffer in WebGPU | image, tolerance 1 |
| `push_kernel` | `compute/push_kernel.hlsl` | dispatch, 100 elements; push constants as above | buffer within 1e-5 relative of the CPU result, exact between compilers |

Tunable constants such as `UI_AA_WIDTH` and `PARTICLE_GRAVITY` have defaults in the shaders; the
self-check tests recompile with another value to prove the comparison notices a difference.

The 3D cases build their inputs from generators described in the harness (`MeshGen`, `SceneGen`): spheres, cubes
and planes with normals and tangents, camera, light and joint matrices in either memory layout, record patterns,
procedural material maps, cube-map skies and analytic depth and normal buffers. Float colour targets are written
as `RGBA16F`, `RGBA32F` or `R32F` and compared with the case's relative tolerance.

## Probes

`probes/*.hlsl` are single-construct shaders, each a construct DXC compiles that real shaders use. They have no
parity case; the probe tests require DXC to compile each and pass spirv-val, then the same of the candidate (or,
for `expect-error` probes, a diagnostic). Each carries the same SPDX header and was written for this repository.

| Probe | Construct |
|---|---|
| `probes/append_consume.hlsl` | AppendStructuredBuffer.Append and ConsumeStructuredBuffer.Consume with their hidden counters. |
| `probes/array_interpolant.hlsl` | An array of float4 as a vertex output (per-cascade shadow coordinates); one location per element. |
| `probes/cbuffer_bool.hlsl` | A bool member of a constant buffer, stored as a 32-bit integer. |
| `probes/cbuffer_member_name.hlsl` | A constant buffer and one of its members share a name. |
| `probes/clip_distance.hlsl` | SV_ClipDistance outputs with two semantic indices. |
| `probes/clip_distance_array.hlsl` | SV_ClipDistance as an array member. |
| `probes/cull_distance.hlsl` | An SV_CullDistance output. |
| `probes/faceforward.hlsl` | The faceforward intrinsic. |
| `probes/gather_cmp.hlsl` | Texture2D.GatherCmp on a depth texture. |
| `probes/half_float_conversions.hlsl` | f16tof32 and f32tof16 (packed vertex attributes). |
| `probes/lod_query.hlsl` | Texture2D.CalculateLevelOfDetail. |
| `probes/matrix_interpolant.hlsl` | A float3x3 tangent frame as a pixel input; one location per row. |
| `probes/packoffset_overlap.hlsl` | Two members packed at the same offset: DXC reports an error, so a diagnostic is expected. |
| `probes/resource_parameters.hlsl` | A texture and a sampler passed to a helper function; the candidate asks for [forceinline]. |
| `probes/structured_float3.hlsl` | StructuredBuffer<float3>: a 16-byte element stride. |

## Known gaps

`known-gaps.txt` lists the cases and probes the compiler does not pass yet, one `NAME: what is wrong` line each. The
tests report a listed entry that fails as skipped with that reason, and fail a listed entry that now passes until its
line is deleted, so the list only shrinks. Failures of unlisted entries fail the run.
