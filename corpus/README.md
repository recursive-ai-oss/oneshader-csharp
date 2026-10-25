# Corpus

Shaders and case descriptions used by the execution-parity tests in `tests/OneShader.ParityTests`.
Every file here was written for this repository and is licensed `MIT OR Apache-2.0`; each shader
starts with an `SPDX-License-Identifier` line. No third-party shader is included. A third-party
sample may only be added with a permissive licence (MIT, Apache-2.0, BSD or CC0) recorded in this file.

The shaders are modern HLSL in the subset every target can express (no wave operations), compile
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
| `compute/prefix_sum.hlsl` | Block-wise exclusive scan in group-shared memory with barriers | written for this repository | MIT OR Apache-2.0 |
| `compute/histogram.hlsl` | Luminance histogram with group-shared atomics into a read-write buffer | written for this repository | MIT OR Apache-2.0 |
| `compute/image_filter.hlsl` | Tiled 3x3 sharpen on a `RWTexture2D` through group-shared memory | written for this repository | MIT OR Apache-2.0 |
| `compute/particles.hlsl` | Per-particle integration over a `RWStructuredBuffer` of structs | written for this repository | MIT OR Apache-2.0 |

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
| `cull_instances` | `compute/cull_instances.hlsl` | dispatch, 1000 instances | buffers exact (index list as a sorted set), CPU result |
| `cluster_build` | `compute/cluster_build.hlsl` | dispatch, 3456 clusters | buffer, CPU result within 1e-4 relative |
| `shadow_cascades_poisson` | `3d/shadow_cascades.hlsl` (`PSSpiral`) | same scene, eight rotated taps from a `static const` array | image, tolerance 2 |
| `ibl_prefilter` | `compute/ibl_prefilter.hlsl` | dispatch, 6 faces, cube texture with mips in, layered storage image out | float image, relative tolerance 0.001 |
| `light_cull` | `compute/light_cull.hlsl` | dispatch, 512 clusters, 40 lights | buffers exact, CPU result |
| `depth_bounds` | `compute/depth_bounds.hlsl` | dispatch, 64 tiles | buffer exact, CPU result |
| `prefix_sum` | `compute/prefix_sum.hlsl` | dispatch | buffers exact, CPU result |
| `histogram` | `compute/histogram.hlsl` | dispatch | buffer exact, CPU result |
| `image_filter` | `compute/image_filter.hlsl` | dispatch | image, CPU result within one 8-bit step |
| `particles` | `compute/particles.hlsl` | 3 dispatches | buffer bit-exact between compilers, CPU result within 1e-4 relative |

Tunable constants such as `UI_AA_WIDTH` and `PARTICLE_GRAVITY` have defaults in the shaders; the
self-check tests recompile with another value to prove the comparison notices a difference.

The 3D cases build their inputs from generators described in the harness (`MeshGen`, `SceneGen`): spheres, cubes
and planes with normals and tangents, camera, light and joint matrices in either memory layout, record patterns,
procedural material maps, cube-map skies and analytic depth and normal buffers. Float colour targets are written
as `RGBA16F`, `RGBA32F` or `R32F` and compared with the case's relative tolerance.
