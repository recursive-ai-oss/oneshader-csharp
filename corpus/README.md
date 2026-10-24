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
| `prefix_sum` | `compute/prefix_sum.hlsl` | dispatch | buffers exact, CPU result |
| `histogram` | `compute/histogram.hlsl` | dispatch | buffer exact, CPU result |
| `image_filter` | `compute/image_filter.hlsl` | dispatch | image, CPU result within one 8-bit step |
| `particles` | `compute/particles.hlsl` | 3 dispatches | buffer bit-exact between compilers, CPU result within 1e-4 relative |

Tunable constants such as `UI_AA_WIDTH` and `PARTICLE_GRAVITY` have defaults in the shaders; the
self-check tests recompile with another value to prove the comparison notices a difference.
