# WebGPU execution parity

`tools/run-webgpu-parity.sh` runs the WGSL output of every corpus case in headless Chromium on a real WebGPU
adapter and compares the results with DXC's SPIR-V running on Vulkan.

```
ONESHADER_TOOLS=<tools dir> ONESHADER_ARTIFACTS=<scratch dir> tools/run-webgpu-parity.sh
```

What it does:

1. `tests/OneShader.ParityTests/WebGpuParityTests.cs` compiles each case's entry points with `CompileTarget.Wgsl`,
   runs the DXC-compiled SPIR-V on Vulkan (validation layer on) as the reference, and writes one JSON job per case
   (WGSL, buffer and texture bytes, pipeline description).
2. `tools/webgpu/run.py` starts Chromium through Playwright (a `uv` environment under `ONESHADER_WEBGPU_HOME`,
   default `~/.cache/oneshader-webgpu`; Playwright downloads its own Chromium) and serves `tools/webgpu/index.html`.
   `runner.js` creates the buffers, textures and samplers, builds bind group layouts from the WGSL's own declarations,
   runs the compute dispatches or the offscreen draw, and reads the outputs back. Every job runs inside validation,
   out-of-memory and internal error scopes plus an uncaptured-error listener, and shader compilation messages are
   checked; any of these fails the case.
3. The test compares outputs like the other parity runs: integer and other buffers byte for byte, 8-bit images within
   the case's `tolerance`, float images within `ftolerance`. Output buffers marked `float=X` in a case file
   (32-bit floats whose math WebGPU only specifies loosely, such as logarithms) compare within `|a-b| <= X * (1 + |a|)`.
   Cases the backend deliberately rejects (diagnostic OSH52xx) are skipped with the diagnostic.

The browser must get a hardware adapter: the driver exits with an error when Chromium offers only a software one.
For debugging the tooling without a GPU, set `ONESHADER_ALLOW_SOFTWARE_ADAPTER=1` and
`ONESHADER_BROWSER_ARGS_ONLY="--enable-unsafe-webgpu --no-sandbox"` (SwiftShader), and `ONESHADER_VK_DEVICE=llvmpipe`
for the reference; rasterization and filtering then differ between the two software implementations, so image
mismatches are expected.

`tools/webgpu/probe.py` prints the adapter a launch gets. Chromium flags that give a hardware adapter on Linux: `--enable-unsafe-webgpu --enable-features=Vulkan
--use-angle=vulkan --ignore-gpu-blocklist` (see `tools/webgpu/browser.py`).

## Orientation

Clip-space y points up in WebGPU, like D3D and Metal. The Vulkan reference draws with a negative-height viewport and
clockwise front faces (`VkRunner.Run(..., yUp: true)`), so both sides see the same clip coordinates, framebuffer rows
(`SV_Position`) and front faces. A shader written for Vulkan's y-down clip space draws upside down on WebGPU unless its
projection accounts for it; the backend does not flip.

## Options and limits

The device is requested with default limits and no optional features, which is what a portable web build gets.
`ONESHADER_WEBGPU_CASES=a,b` runs a subset.
