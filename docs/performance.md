# Performance

OneShader compiles a shader about 2.4 times faster than DXC when both run as native processes, and about 14 times slower when it runs as an ordinary .NET process that has to start the runtime and JIT-compile the compiler first. Both are measured below, on the same 68 entry points, with the compiler's SPIR-V compared against DXC's SPIR-V. This page prints all of it: the tables, the losses and the method.

Every number comes from the files in [`bench/results/`](../bench/results), measured on version 0.3.0-dev (commit f0f3274, shortly before 0.3.0-preview.1; not re-measured since) on one workstation (see [Method](#method)). Treat them as local measurements, not promises for your hardware.

## Results at a glance

One shader compile per process, for each of the 68 (file, entry point) pairs the repository's execution-parity cases compile (25 2D-renderer, 24 3D, 11 compute, 8 language-feature entry points). DXC is the released binary run with `-spirv`; its default optimization. OneShader is a small command-line wrapper around `ShaderCompiler.CompileFile`, built three ways: as a NativeAOT binary, as a framework-dependent ReadyToRun build, and as a plain framework-dependent (JIT) build. Time is wall-clock from creating the process to its exit, with the blob written to disk. The ratio is DXC's time divided by ours per entry point, summarized by the geometric mean; a time within 5% of DXC's is a tie.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/recursive-ai-oss/oneshader-csharp/main/docs/img/oneshader-hero-dark.svg">
  <img alt="Bar chart: how many times faster or slower OneShader's compile is than DXC's, per task, log scale. NativeAOT is about 2.4 times faster warm and 2.7 times faster cold. ReadyToRun is about 5 to 6 times slower and the JIT build 13 to 14 times slower." src="https://raw.githubusercontent.com/recursive-ai-oss/oneshader-csharp/main/docs/img/oneshader-hero-light.svg">
</picture>

The chart is drawn from the results file by [`bench/make-report.py`](../bench/make-report.py).

<!-- begin:summary -->
| Task (one process per entry point) | Entry points | Time vs DXC (geometric mean) | Fastest | Slowest | Entry points slower than DXC |
|---|--:|---|---|---|--:|
| NativeAOT, warm: 2D renderer | 25 | 2.46× faster | 2.82× faster | 2.09× faster | 0 |
| NativeAOT, warm: 3D (PBR set) | 24 | 2.41× faster | 3.14× faster | 2.13× faster | 0 |
| NativeAOT, warm: compute | 11 | 2.49× faster | 2.81× faster | 2.21× faster | 0 |
| NativeAOT, warm: language features | 8 | 2.30× faster | 2.72× faster | 2.08× faster | 0 |
| NativeAOT, warm: all | 68 | 2.43× faster | 3.14× faster | 2.08× faster | 0 |
| NativeAOT, cold: all | 68 | 2.68× faster | 3.57× faster | 1.51× faster | 0 |
| ReadyToRun, warm: all | 68 | 6.10× slower | 3.21× slower | 7.74× slower | 68 |
| ReadyToRun, cold: all | 68 | 5.30× slower | 2.78× slower | 7.00× slower | 68 |
| JIT, warm: all | 68 | 14.3× slower | 7.28× slower | 17.7× slower | 68 |
| JIT, cold: all | 68 | 12.9× slower | 6.73× slower | 16.3× slower | 68 |
<!-- end:summary -->

"Warm" is the median of 15 repeated runs per entry point; "cold" is the first run of each entry point right after the corpus was written to a fresh directory.

### The losses

- **Started as a .NET process, OneShader is slower than DXC on every entry point.** The JIT build is 14.3× slower warm (113 ms median per entry point against DXC's 7.6 ms) and 12.9× slower cold; ReadyToRun is 6.1× and 5.3× slower (48 ms median). Almost all of that is starting the runtime and compiling or loading the compiler's code, not compiling the shader: the same compile inside a running process takes 0.34 ms at the median ([in-process](#in-process-compile-time)). The NativeAOT build is the one that wins, so a command-line tool that compiles shaders should ship NativeAOT; the MSBuild task runs inside the build's own long-lived .NET process, where the runtime start-up is paid once per build, not once per shader (that case, the first compile in a fresh process, is not measured here).
- **The SPIR-V is larger than DXC's with debug names on, which is the default.** 57 of 68 entry points are larger, 46,620 words against DXC's 44,126 (+5.7%); the worst are `lang/sizeof_static.hlsl:CSMain` (+90%), `3d/skinned.hlsl:VSMain` (+60%) and `3d/stage_io.hlsl:VSMain` (+36%). With names off (`CompileOptions.KeepNames = false`) the total is 40,258 words, 8.8% below DXC, with 61 entry points smaller and 7 larger. DXC emits no names unless asked, so the second comparison is the like-for-like one; the first is what the compiler gives you by default ([table](#output-size)).
- **The optimizer is the largest cost of a compile.** With it off, SPIR-V takes 0.13 ms at the median instead of 0.34 ms (2.5× less); the worst file, `3d/bloom_downsample.hlsl`, takes 1.18 ms with the optimizer and 0.22 ms without (5.5×). That is the price of the smaller output; see the in-process table.
- **MSL and WGSL are not on the chart.** DXC has no such outputs, so there is nothing to compare; their in-process times are in the table. They also refuse some corpus shaders, which are left out of those columns: MSL refuses `compute/append_consume.hlsl`; WGSL refuses that file, `3d/stage_io.hlsl` and `lang/resource_arrays.hlsl` (the diagnostics are in [`bench/results/inproc/`](../bench/results/inproc)).
- **No in-process comparison with DXC.** DXC is timed as a process only. Its in-process cost is not measured, so the in-process numbers below stand alone.

## In-process compile time

`ShaderCompiler.Compile` called on the source text in a warm process, every entry point of a file in one call, SPIR-V with the default optimizer unless the column says otherwise. BenchmarkDotNet 0.15.8 with the memory diagnoser, in-process toolchain, 3 warm-up and 12 measured iterations of at least 150 ms each (10 to 12 kept after outlier removal); the mean in milliseconds, then the standard deviation in brackets, and the managed allocation per call in KiB. Includes (`3d/brdf.hlsli`) are read from disk during the compile; the shader text is read once beforehand.

Over the 41 files: the median file compiles in 0.34 ms and allocates 0.85 MiB; the slowest is `2d/renderer/ui.hlsl` at 2.0 ms and 4.2 MiB. MSL and WGSL cost about 3 to 5% more than SPIR-V. The first row, `2d/blit.hlsl` with SPIR-V, was the first benchmark the process ran and shows it (a standard deviation of 34% of its mean; the same file with MSL, run later, takes 0.12 ms): read that one cell as a warm-up artifact.

<!-- begin:inproc -->
| File | SPIR-V ms | alloc KiB | SPIR-V, optimizer off ms | alloc KiB | MSL ms | alloc KiB | WGSL ms | alloc KiB |
|---|--:|--:|--:|--:|--:|--:|--:|--:|
| `2d/blit.hlsl` | 0.224 (±0.076) | 335 | 0.055 (±0.001) | 195 | 0.124 (±0.000) | 353 | 0.125 (±0.001) | 360 |
| `2d/blur.hlsl` | 0.264 (±0.000) | 689 | 0.106 (±0.000) | 346 | 0.281 (±0.000) | 721 | 0.280 (±0.000) | 733 |
| `2d/color_grade.hlsl` | 0.278 (±0.003) | 728 | 0.116 (±0.000) | 383 | 0.292 (±0.001) | 763 | 0.294 (±0.001) | 789 |
| `2d/msdf_text.hlsl` | 0.313 (±0.000) | 822 | 0.130 (±0.000) | 429 | 0.335 (±0.001) | 873 | 0.334 (±0.001) | 893 |
| `2d/renderer/quad.hlsl` | 0.654 (±0.001) | 1704 | 0.256 (±0.001) | 859 | 0.692 (±0.001) | 1789 | 0.695 (±0.003) | 1835 |
| `2d/renderer/tile.hlsl` | 0.551 (±0.001) | 1437 | 0.227 (±0.003) | 744 | 0.586 (±0.001) | 1508 | 0.586 (±0.001) | 1543 |
| `2d/renderer/ui.hlsl` | 2.018 (±0.007) | 4268 | 0.666 (±0.004) | 1684 | 2.131 (±0.020) | 4591 | 2.194 (±0.022) | 4668 |
| `2d/sdf_text.hlsl` | 0.248 (±0.000) | 682 | 0.107 (±0.000) | 368 | 0.269 (±0.001) | 728 | 0.266 (±0.000) | 747 |
| `2d/sprite.hlsl` | 0.163 (±0.000) | 446 | 0.077 (±0.001) | 248 | 0.179 (±0.000) | 478 | 0.178 (±0.000) | 502 |
| `2d/tilemap.hlsl` | 0.269 (±0.000) | 697 | 0.116 (±0.001) | 374 | 0.288 (±0.001) | 738 | 0.288 (±0.000) | 760 |
| `2d/ui_rect.hlsl` | 0.666 (±0.005) | 1707 | 0.242 (±0.001) | 785 | 0.707 (±0.001) | 1821 | 0.710 (±0.001) | 1909 |
| `3d/bloom_downsample.hlsl` | 1.181 (±0.002) | 2941 | 0.215 (±0.001) | 741 | 1.187 (±0.003) | 3101 | 1.191 (±0.004) | 3135 |
| `3d/bloom_upsample.hlsl` | 0.343 (±0.006) | 868 | 0.133 (±0.000) | 446 | 0.351 (±0.000) | 915 | 0.350 (±0.000) | 939 |
| `3d/clustered_lights.hlsl` | 0.953 (±0.011) | 2332 | 0.355 (±0.002) | 1060 | 1.002 (±0.001) | 2467 | 0.997 (±0.004) | 2478 |
| `3d/ibl.hlsl` | 0.649 (±0.001) | 1662 | 0.318 (±0.003) | 1014 | 0.660 (±0.001) | 1793 | 0.646 (±0.001) | 1760 |
| `3d/pbr_forward.hlsl` | 1.403 (±0.029) | 3143 | 0.561 (±0.007) | 1394 | 1.423 (±0.005) | 3345 | 1.402 (±0.002) | 3361 |
| `3d/shadow_cascades.hlsl` | 1.104 (±0.003) | 2679 | 0.363 (±0.002) | 1080 | 1.175 (±0.003) | 2833 | 1.162 (±0.002) | 2862 |
| `3d/shadow_depth.hlsl` | 0.100 (±0.000) | 254 | 0.048 (±0.000) | 143 | 0.109 (±0.000) | 274 | 0.110 (±0.000) | 300 |
| `3d/skinned.hlsl` | 0.334 (±0.001) | 888 | 0.133 (±0.001) | 454 | 0.345 (±0.001) | 947 | 0.359 (±0.001) | 1083 |
| `3d/ssao.hlsl` | 0.528 (±0.009) | 1299 | 0.198 (±0.003) | 649 | 0.542 (±0.001) | 1370 | 0.540 (±0.001) | 1407 |
| `3d/stage_io.hlsl` | 0.367 (±0.001) | 962 | 0.147 (±0.001) | 484 | 0.387 (±0.001) | 1006 | refused | – |
| `3d/tonemap.hlsl` | 0.502 (±0.001) | 1179 | 0.189 (±0.001) | 556 | 0.522 (±0.001) | 1218 | 0.516 (±0.001) | 1228 |
| `compute/append_consume.hlsl` | 0.162 (±0.002) | 417 | 0.073 (±0.001) | 228 | refused | – | refused | – |
| `compute/cluster_build.hlsl` | 0.419 (±0.001) | 1107 | 0.129 (±0.001) | 412 | 0.440 (±0.001) | 1155 | 0.441 (±0.002) | 1205 |
| `compute/cull_instances.hlsl` | 0.353 (±0.007) | 836 | 0.125 (±0.002) | 373 | 0.368 (±0.000) | 876 | 0.362 (±0.001) | 871 |
| `compute/depth_bounds.hlsl` | 0.209 (±0.000) | 562 | 0.093 (±0.001) | 307 | 0.223 (±0.002) | 594 | 0.220 (±0.001) | 599 |
| `compute/histogram.hlsl` | 0.152 (±0.000) | 409 | 0.072 (±0.001) | 228 | 0.161 (±0.001) | 426 | 0.156 (±0.000) | 423 |
| `compute/ibl_prefilter.hlsl` | 0.655 (±0.006) | 1632 | 0.232 (±0.002) | 753 | 0.697 (±0.001) | 1723 | 0.687 (±0.001) | 1734 |
| `compute/image_filter.hlsl` | 0.314 (±0.000) | 803 | 0.126 (±0.002) | 404 | 0.323 (±0.000) | 823 | 0.329 (±0.001) | 853 |
| `compute/light_cull.hlsl` | 0.264 (±0.000) | 690 | 0.108 (±0.002) | 355 | 0.278 (±0.000) | 724 | 0.272 (±0.001) | 731 |
| `compute/particles.hlsl` | 0.291 (±0.000) | 741 | 0.108 (±0.002) | 370 | 0.312 (±0.000) | 803 | 0.313 (±0.001) | 831 |
| `compute/prefix_sum.hlsl` | 0.164 (±0.000) | 433 | 0.068 (±0.001) | 221 | 0.169 (±0.001) | 442 | 0.171 (±0.000) | 457 |
| `compute/stage_layouts.hlsl` | 0.210 (±0.000) | 592 | 0.095 (±0.000) | 344 | 0.213 (±0.000) | 606 | 0.217 (±0.001) | 631 |
| `lang/inheritance.hlsl` | 0.358 (±0.001) | 920 | 0.125 (±0.002) | 409 | 0.367 (±0.000) | 937 | 0.364 (±0.000) | 944 |
| `lang/members.hlsl` | 0.397 (±0.003) | 1035 | 0.124 (±0.000) | 411 | 0.405 (±0.001) | 1050 | 0.405 (±0.003) | 1071 |
| `lang/operators.hlsl` | 0.657 (±0.002) | 1874 | 0.224 (±0.003) | 778 | 0.659 (±0.002) | 1872 | 0.660 (±0.001) | 1882 |
| `lang/resource_arrays.hlsl` | 0.269 (±0.002) | 697 | 0.123 (±0.001) | 399 | 0.274 (±0.001) | 711 | refused | – |
| `lang/resource_locals.hlsl` | 0.278 (±0.001) | 759 | 0.105 (±0.002) | 357 | 0.285 (±0.000) | 768 | 0.287 (±0.001) | 794 |
| `lang/sizeof_static.hlsl` | 0.227 (±0.000) | 646 | 0.083 (±0.001) | 300 | 0.230 (±0.000) | 653 | 0.234 (±0.000) | 680 |
| `lang/statics_and_literals.hlsl` | 0.431 (±0.000) | 1184 | 0.216 (±0.001) | 725 | 0.442 (±0.001) | 1206 | 0.435 (±0.001) | 1210 |
| `lang/templates.hlsl` | 0.586 (±0.012) | 1518 | 0.232 (±0.002) | 776 | 0.581 (±0.001) | 1526 | 0.585 (±0.001) | 1559 |
<!-- end:inproc -->

"refused" marks a file the target does not support; it is left out rather than timed as a failure.

## Process time per entry point

Median of 15 warm runs in milliseconds, with the fastest and slowest run in brackets, for each entry point. "vs DXC" is DXC's median divided by the OneShader median.

<!-- begin:process-warm -->
| Entry point | DXC ms | NativeAOT ms | vs DXC | ReadyToRun ms | vs DXC | JIT ms | vs DXC |
|---|--:|--:|---|--:|---|--:|---|
| `2d/blit.hlsl:PSMain(ps)` | 6.40 (5.98–7.61) | 2.57 (2.33–3.88) | 2.49× faster | 42.2 (38.5–46.6) | 6.59× slower | 99.8 (98.2–106) | 15.6× slower |
| `2d/blit.hlsl:VSMain(vs)` | 6.49 (6.02–7.83) | 2.56 (2.39–3.90) | 2.53× faster | 42.4 (40.0–47.2) | 6.52× slower | 101 (99.1–107) | 15.5× slower |
| `2d/blur.hlsl:PSMain(ps)` | 7.32 (6.96–8.60) | 2.99 (2.52–5.00) | 2.45× faster | 49.0 (44.9–51.8) | 6.70× slower | 115 (113–123) | 15.8× slower |
| `2d/blur.hlsl:VSMain(vs)` | 6.43 (6.13–7.75) | 2.72 (2.51–4.94) | 2.37× faster | 46.4 (41.0–48.7) | 7.22× slower | 108 (107–115) | 16.9× slower |
| `2d/color_grade.hlsl:PSMain(ps)` | 7.43 (6.97–8.46) | 2.95 (2.54–5.47) | 2.52× faster | 48.8 (44.2–50.4) | 6.56× slower | 117 (113–127) | 15.7× slower |
| `2d/color_grade.hlsl:VSMain(vs)` | 6.95 (6.23–9.36) | 2.71 (2.51–4.40) | 2.57× faster | 48.2 (42.6–50.5) | 6.94× slower | 112 (106–121) | 16.1× slower |
| `2d/msdf_text.hlsl:PSMain(ps)` | 7.47 (7.04–8.57) | 3.12 (2.97–4.83) | 2.39× faster | 48.6 (43.9–51.3) | 6.51× slower | 114 (109–126) | 15.2× slower |
| `2d/msdf_text.hlsl:VSMain(vs)` | 6.75 (6.22–8.14) | 2.78 (2.54–4.56) | 2.43× faster | 46.9 (42.5–49.3) | 6.94× slower | 109 (107–122) | 16.1× slower |
| `2d/renderer/quad.hlsl:PSBlur(ps)` | 7.34 (6.85–8.52) | 3.27 (3.02–3.54) | 2.25× faster | 48.2 (44.6–52.9) | 6.56× slower | 116 (112–120) | 15.8× slower |
| `2d/renderer/quad.hlsl:PSGrade(ps)` | 7.95 (7.55–8.95) | 3.42 (3.25–5.33) | 2.32× faster | 49.6 (43.2–52.8) | 6.23× slower | 117 (113–129) | 14.7× slower |
| `2d/renderer/quad.hlsl:PSSprite(ps)` | 6.74 (6.30–8.99) | 3.04 (2.68–5.46) | 2.22× faster | 47.5 (44.3–50.8) | 7.05× slower | 111 (108–116) | 16.4× slower |
| `2d/renderer/quad.hlsl:VSMain(vs)` | 7.76 (7.41–10.2) | 3.42 (3.03–5.55) | 2.27× faster | 49.0 (45.4–51.3) | 6.32× slower | 115 (114–121) | 14.9× slower |
| `2d/renderer/tile.hlsl:PSFlat(ps)` | 8.15 (7.61–9.41) | 3.00 (2.71–3.76) | 2.72× faster | 48.5 (44.7–52.3) | 5.95× slower | 114 (110–118) | 13.9× slower |
| `2d/renderer/tile.hlsl:PSIds(ps)` | 9.14 (8.47–10.4) | 3.25 (2.82–3.56) | 2.82× faster | 49.8 (45.2–52.0) | 5.45× slower | 118 (114–126) | 12.9× slower |
| `2d/renderer/tile.hlsl:VSMain(vs)` | 9.53 (8.51–10.6) | 3.39 (3.12–4.66) | 2.81× faster | 48.5 (43.3–50.2) | 5.09× slower | 118 (115–126) | 12.3× slower |
| `2d/renderer/ui.hlsl:PSMain(ps)` | 17.2 (15.7–18.7) | 6.45 (6.06–8.82) | 2.67× faster | 55.3 (50.2–57.1) | 3.21× slower | 125 (124–127) | 7.28× slower |
| `2d/renderer/ui.hlsl:VSMain(vs)` | 9.30 (8.75–10.7) | 4.41 (3.89–6.69) | 2.11× faster | 50.2 (45.6–53.6) | 5.40× slower | 119 (116–121) | 12.8× slower |
| `2d/sdf_text.hlsl:PSMain(ps)` | 7.49 (6.69–8.48) | 2.87 (2.67–4.68) | 2.61× faster | 48.6 (45.0–50.4) | 6.49× slower | 113 (110–116) | 15.1× slower |
| `2d/sdf_text.hlsl:VSMain(vs)` | 6.80 (6.23–8.94) | 2.73 (2.52–3.49) | 2.49× faster | 46.5 (44.8–50.6) | 6.84× slower | 108 (106–114) | 15.9× slower |
| `2d/sprite.hlsl:PSMain(ps)` | 6.36 (6.15–7.43) | 2.65 (2.41–4.38) | 2.40× faster | 47.3 (43.1–51.3) | 7.44× slower | 106 (105–117) | 16.7× slower |
| `2d/sprite.hlsl:VSMain(vs)` | 6.48 (6.12–7.84) | 2.61 (2.31–5.55) | 2.48× faster | 45.7 (43.6–49.0) | 7.05× slower | 106 (105–116) | 16.4× slower |
| `2d/tilemap.hlsl:PSMain(ps)` | 7.02 (6.74–9.96) | 2.87 (2.59–3.11) | 2.45× faster | 48.2 (45.5–51.3) | 6.86× slower | 112 (109–118) | 16.0× slower |
| `2d/tilemap.hlsl:VSMain(vs)` | 7.62 (6.81–9.16) | 2.85 (2.71–4.82) | 2.67× faster | 47.1 (43.9–51.2) | 6.18× slower | 112 (109–119) | 14.6× slower |
| `2d/ui_rect.hlsl:PSMain(ps)` | 9.95 (9.32–11.1) | 3.73 (3.36–5.78) | 2.67× faster | 50.9 (44.5–53.2) | 5.11× slower | 119 (114–124) | 12.0× slower |
| `2d/ui_rect.hlsl:VSMain(vs)` | 7.03 (6.46–8.40) | 3.36 (3.02–3.65) | 2.09× faster | 49.0 (44.4–52.2) | 6.97× slower | 118 (111–127) | 16.8× slower |
| `3d/bloom_downsample.hlsl:PSMain(ps)` | 11.0 (10.5–12.3) | 5.14 (4.43–6.03) | 2.14× faster | 52.3 (47.7–55.4) | 4.75× slower | 119 (113–131) | 10.8× slower |
| `3d/bloom_downsample.hlsl:VSMain(vs)` | 7.17 (6.38–8.15) | 2.99 (2.62–4.94) | 2.40× faster | 47.8 (40.0–49.4) | 6.67× slower | 112 (106–119) | 15.6× slower |
| `3d/bloom_upsample.hlsl:PSMain(ps)` | 7.70 (7.21–11.3) | 3.15 (2.83–5.36) | 2.44× faster | 46.4 (41.4–49.5) | 6.03× slower | 109 (107–116) | 14.2× slower |
| `3d/bloom_upsample.hlsl:VSMain(vs)` | 6.51 (6.12–7.91) | 2.75 (2.54–4.39) | 2.37× faster | 45.2 (41.5–46.0) | 6.94× slower | 104 (103–114) | 16.0× slower |
| `3d/clustered_lights.hlsl:PSMain(ps)` | 10.9 (9.93–13.1) | 4.64 (4.28–5.79) | 2.35× faster | 51.7 (47.9–53.8) | 4.73× slower | 122 (119–129) | 11.2× slower |
| `3d/clustered_lights.hlsl:VSMain(vs)` | 7.39 (6.41–8.20) | 3.47 (3.13–3.85) | 2.13× faster | 48.7 (44.7–50.9) | 6.59× slower | 117 (112–128) | 15.8× slower |
| `3d/ibl.hlsl:PSMain(ps)` | 8.31 (7.94–9.83) | 3.57 (3.27–5.80) | 2.33× faster | 49.8 (45.3–52.8) | 6.00× slower | 118 (116–129) | 14.2× slower |
| `3d/ibl.hlsl:PSSky(ps)` | 8.44 (7.44–9.07) | 3.42 (3.13–6.33) | 2.46× faster | 48.8 (44.4–52.1) | 5.79× slower | 119 (115–123) | 14.1× slower |
| `3d/ibl.hlsl:VSMain(vs)` | 7.77 (7.17–8.80) | 3.29 (2.92–3.75) | 2.36× faster | 48.6 (45.0–51.9) | 6.26× slower | 113 (111–119) | 14.6× slower |
| `3d/ibl.hlsl:VSSky(vs)` | 7.61 (7.12–8.97) | 3.39 (3.03–6.16) | 2.24× faster | 48.4 (44.4–50.9) | 6.36× slower | 113 (111–117) | 14.9× slower |
| `3d/pbr_forward.hlsl:PSMain(ps)` | 12.9 (12.2–14.1) | 5.47 (5.00–6.28) | 2.36× faster | 54.3 (51.7–56.7) | 4.22× slower | 127 (124–133) | 9.90× slower |
| `3d/pbr_forward.hlsl:VSMain(vs)` | 8.32 (7.67–9.40) | 3.75 (3.54–6.05) | 2.22× faster | 49.9 (43.7–53.3) | 6.00× slower | 119 (116–130) | 14.3× slower |
| `3d/shadow_cascades.hlsl:PSMain(ps)` | 12.5 (11.3–14.9) | 4.00 (3.83–4.81) | 3.14× faster | 51.4 (49.2–55.1) | 4.10× slower | 125 (122–131) | 9.98× slower |
| `3d/shadow_cascades.hlsl:PSSpiral(ps)` | 12.9 (12.0–13.9) | 4.11 (3.61–4.79) | 3.13× faster | 51.6 (46.2–54.3) | 4.01× slower | 126 (123–132) | 9.76× slower |
| `3d/shadow_cascades.hlsl:VSMain(vs)` | 8.19 (6.96–8.99) | 3.43 (3.08–5.52) | 2.39× faster | 47.7 (44.3–53.4) | 5.82× slower | 119 (115–125) | 14.5× slower |
| `3d/shadow_depth.hlsl:VSMain(vs)` | 6.23 (5.63–8.00) | 2.61 (2.21–3.61) | 2.39× faster | 46.3 (43.1–48.4) | 7.44× slower | 107 (105–116) | 17.2× slower |
| `3d/skinned.hlsl:PSMain(ps)` | 6.18 (5.85–8.11) | 2.82 (2.51–4.72) | 2.19× faster | 47.8 (43.0–50.8) | 7.74× slower | 109 (108–111) | 17.7× slower |
| `3d/skinned.hlsl:VSMain(vs)` | 6.80 (6.59–9.80) | 3.03 (2.87–4.23) | 2.25× faster | 48.1 (45.7–49.2) | 7.07× slower | 110 (107–115) | 16.1× slower |
| `3d/ssao.hlsl:PSMain(ps)` | 9.53 (9.11–10.8) | 3.64 (3.12–4.01) | 2.62× faster | 51.3 (48.9–53.9) | 5.38× slower | 121 (119–125) | 12.7× slower |
| `3d/ssao.hlsl:VSMain(vs)` | 7.21 (6.78–8.53) | 3.04 (2.64–4.54) | 2.37× faster | 48.1 (44.3–50.7) | 6.67× slower | 115 (112–124) | 16.0× slower |
| `3d/stage_io.hlsl:PSMain(ps)` | 7.93 (7.52–9.05) | 2.93 (2.68–3.59) | 2.70× faster | 48.4 (44.2–50.0) | 6.10× slower | 109 (108–133) | 13.8× slower |
| `3d/stage_io.hlsl:VSMain(vs)` | 7.62 (7.19–8.79) | 3.03 (2.74–3.41) | 2.52× faster | 48.4 (44.2–51.9) | 6.36× slower | 108 (106–117) | 14.2× slower |
| `3d/tonemap.hlsl:PSMain(ps)` | 8.24 (8.08–10.0) | 3.60 (3.09–4.41) | 2.29× faster | 50.0 (47.1–51.8) | 6.07× slower | 120 (119–129) | 14.6× slower |
| `3d/tonemap.hlsl:VSMain(vs)` | 6.75 (6.34–7.49) | 2.93 (2.66–3.06) | 2.30× faster | 48.0 (44.7–50.2) | 7.11× slower | 114 (112–118) | 16.8× slower |
| `compute/append_consume.hlsl:CSMain(cs)` | 6.60 (6.07–7.72) | 2.70 (2.44–2.96) | 2.44× faster | 45.9 (43.5–49.1) | 6.96× slower | 105 (103–115) | 15.9× slower |
| `compute/cluster_build.hlsl:CSMain(cs)` | 7.68 (7.46–8.84) | 3.47 (3.11–4.12) | 2.21× faster | 49.6 (45.7–51.9) | 6.47× slower | 116 (114–125) | 15.1× slower |
| `compute/cull_instances.hlsl:CSMain(cs)` | 8.74 (7.90–9.77) | 3.39 (3.05–5.06) | 2.58× faster | 49.7 (44.5–52.7) | 5.69× slower | 122 (116–125) | 13.9× slower |
| `compute/depth_bounds.hlsl:CSMain(cs)` | 8.08 (7.16–8.64) | 3.03 (2.67–3.31) | 2.67× faster | 48.5 (46.4–52.2) | 6.00× slower | 118 (114–125) | 14.6× slower |
| `compute/histogram.hlsl:CSMain(cs)` | 6.54 (6.01–7.13) | 2.80 (2.46–4.71) | 2.34× faster | 47.1 (44.8–48.6) | 7.20× slower | 107 (104–112) | 16.3× slower |
| `compute/ibl_prefilter.hlsl:CSMain(cs)` | 11.1 (10.6–12.2) | 4.30 (4.03–5.57) | 2.59× faster | 51.9 (48.5–54.1) | 4.66× slower | 121 (119–129) | 10.9× slower |
| `compute/image_filter.hlsl:CSMain(cs)` | 9.30 (8.48–10.2) | 3.31 (2.71–5.50) | 2.81× faster | 47.4 (44.9–49.2) | 5.10× slower | 113 (111–118) | 12.1× slower |
| `compute/light_cull.hlsl:CSMain(cs)` | 7.66 (7.19–8.47) | 3.09 (2.76–5.33) | 2.48× faster | 48.2 (44.9–51.0) | 6.30× slower | 113 (111–119) | 14.8× slower |
| `compute/particles.hlsl:CSMain(cs)` | 7.71 (7.37–9.05) | 3.12 (2.75–4.00) | 2.47× faster | 49.6 (45.1–53.1) | 6.43× slower | 114 (111–119) | 14.8× slower |
| `compute/prefix_sum.hlsl:CSMain(cs)` | 6.58 (6.05–7.69) | 2.77 (2.57–3.11) | 2.38× faster | 46.4 (44.2–48.8) | 7.05× slower | 105 (102–111) | 15.9× slower |
| `compute/stage_layouts.hlsl:CSMain(cs)` | 7.58 (7.25–9.14) | 3.03 (2.79–5.30) | 2.50× faster | 48.4 (44.0–50.8) | 6.39× slower | 109 (107–112) | 14.3× slower |
| `lang/inheritance.hlsl:CSMain(cs)` | 7.42 (6.99–8.21) | 3.26 (3.03–4.24) | 2.28× faster | 48.6 (45.2–51.8) | 6.55× slower | 113 (109–118) | 15.3× slower |
| `lang/members.hlsl:CSMain(cs)` | 7.25 (6.92–8.76) | 3.48 (2.85–3.94) | 2.08× faster | 48.0 (45.5–51.8) | 6.61× slower | 112 (111–121) | 15.5× slower |
| `lang/operators.hlsl:CSMain(cs)` | 9.05 (8.54–10.3) | 4.32 (4.05–5.04) | 2.10× faster | 49.4 (46.6–53.2) | 5.46× slower | 112 (110–121) | 12.4× slower |
| `lang/resource_arrays.hlsl:CSMain(cs)` | 8.24 (7.79–9.33) | 3.16 (2.82–3.98) | 2.61× faster | 46.8 (45.0–49.2) | 5.68× slower | 116 (112–127) | 14.0× slower |
| `lang/resource_locals.hlsl:CSMain(cs)` | 8.36 (7.97–9.40) | 3.07 (2.92–5.03) | 2.72× faster | 47.6 (42.3–51.3) | 5.70× slower | 113 (110–116) | 13.5× slower |
| `lang/sizeof_static.hlsl:CSMain(cs)` | 6.71 (6.38–8.42) | 3.05 (2.94–3.25) | 2.20× faster | 46.9 (43.0–47.9) | 7.00× slower | 110 (106–118) | 16.3× slower |
| `lang/statics_and_literals.hlsl:CSMain(cs)` | 7.84 (7.58–9.43) | 3.55 (3.39–6.07) | 2.21× faster | 50.0 (46.6–53.5) | 6.37× slower | 119 (118–128) | 15.2× slower |
| `lang/templates.hlsl:CSMain(cs)` | 8.95 (7.86–10.0) | 3.93 (3.77–5.75) | 2.28× faster | 49.6 (47.1–52.1) | 5.54× slower | 121 (119–133) | 13.5× slower |
<!-- end:process-warm -->

### Cold runs

The first run of each entry point after the corpus was copied to a fresh directory, one sample each (no spread to show). The page cache was not dropped (that needs privileges the benchmark does not use), so "cold" here means first run, not a cold disk: the JIT and ReadyToRun builds pay for loading and compiling code on every process start, and every process, warm or cold, is a new process.

<!-- begin:process-cold -->
| Entry point | DXC ms | NativeAOT ms | vs DXC | ReadyToRun ms | vs DXC | JIT ms | vs DXC |
|---|--:|--:|---|--:|---|--:|---|
| `2d/blit.hlsl:PSMain(ps)` | 7.97 | 2.51 | 3.18× faster | 44.2 | 5.55× slower | 103 | 13.0× slower |
| `2d/blit.hlsl:VSMain(vs)` | 7.78 | 2.72 | 2.86× faster | 42.9 | 5.52× slower | 99.5 | 12.8× slower |
| `2d/blur.hlsl:PSMain(ps)` | 8.23 | 2.89 | 2.85× faster | 49.7 | 6.04× slower | 112 | 13.6× slower |
| `2d/blur.hlsl:VSMain(vs)` | 6.68 | 2.72 | 2.46× faster | 45.8 | 6.85× slower | 109 | 16.3× slower |
| `2d/color_grade.hlsl:PSMain(ps)` | 8.44 | 2.92 | 2.90× faster | 49.1 | 5.81× slower | 119 | 14.1× slower |
| `2d/color_grade.hlsl:VSMain(vs)` | 7.29 | 2.77 | 2.63× faster | 45.1 | 6.19× slower | 114 | 15.7× slower |
| `2d/msdf_text.hlsl:PSMain(ps)` | 9.07 | 3.06 | 2.97× faster | 48.0 | 5.29× slower | 121 | 13.3× slower |
| `2d/msdf_text.hlsl:VSMain(vs)` | 8.54 | 2.67 | 3.19× faster | 46.5 | 5.45× slower | 110 | 12.9× slower |
| `2d/renderer/quad.hlsl:PSBlur(ps)` | 8.30 | 2.93 | 2.83× faster | 50.4 | 6.07× slower | 112 | 13.5× slower |
| `2d/renderer/quad.hlsl:PSGrade(ps)` | 8.27 | 3.99 | 2.07× faster | 46.0 | 5.57× slower | 124 | 15.0× slower |
| `2d/renderer/quad.hlsl:PSSprite(ps)` | 7.97 | 2.88 | 2.76× faster | 45.5 | 5.71× slower | 113 | 14.2× slower |
| `2d/renderer/quad.hlsl:VSMain(vs)` | 8.81 | 3.86 | 2.28× faster | 49.3 | 5.60× slower | 114 | 12.9× slower |
| `2d/renderer/tile.hlsl:PSFlat(ps)` | 9.77 | 3.92 | 2.49× faster | 44.8 | 4.59× slower | 111 | 11.3× slower |
| `2d/renderer/tile.hlsl:PSIds(ps)` | 10.6 | 4.14 | 2.56× faster | 48.1 | 4.53× slower | 115 | 10.9× slower |
| `2d/renderer/tile.hlsl:VSMain(vs)` | 11.0 | 3.29 | 3.35× faster | 49.6 | 4.51× slower | 116 | 10.5× slower |
| `2d/renderer/ui.hlsl:PSMain(ps)` | 18.8 | 6.56 | 2.87× faster | 52.4 | 2.78× slower | 127 | 6.73× slower |
| `2d/renderer/ui.hlsl:VSMain(vs)` | 9.43 | 3.64 | 2.59× faster | 47.7 | 5.06× slower | 116 | 12.3× slower |
| `2d/sdf_text.hlsl:PSMain(ps)` | 8.07 | 2.90 | 2.78× faster | 47.7 | 5.91× slower | 115 | 14.3× slower |
| `2d/sdf_text.hlsl:VSMain(vs)` | 7.79 | 2.48 | 3.14× faster | 47.4 | 6.08× slower | 112 | 14.3× slower |
| `2d/sprite.hlsl:PSMain(ps)` | 7.91 | 2.38 | 3.32× faster | 48.1 | 6.08× slower | 107 | 13.6× slower |
| `2d/sprite.hlsl:VSMain(vs)` | 7.60 | 2.56 | 2.97× faster | 45.8 | 6.02× slower | 108 | 14.2× slower |
| `2d/tilemap.hlsl:PSMain(ps)` | 8.41 | 2.58 | 3.26× faster | 48.6 | 5.79× slower | 110 | 13.1× slower |
| `2d/tilemap.hlsl:VSMain(vs)` | 8.46 | 5.62 | 1.51× faster | 42.0 | 4.96× slower | 112 | 13.2× slower |
| `2d/ui_rect.hlsl:PSMain(ps)` | 11.3 | 3.51 | 3.23× faster | 53.0 | 4.68× slower | 124 | 11.0× slower |
| `2d/ui_rect.hlsl:VSMain(vs)` | 8.25 | 3.16 | 2.61× faster | 47.6 | 5.77× slower | 123 | 14.9× slower |
| `3d/bloom_downsample.hlsl:PSMain(ps)` | 12.8 | 5.20 | 2.47× faster | 51.8 | 4.03× slower | 120 | 9.36× slower |
| `3d/bloom_downsample.hlsl:VSMain(vs)` | 6.78 | 2.97 | 2.28× faster | 44.3 | 6.54× slower | 110 | 16.3× slower |
| `3d/bloom_upsample.hlsl:PSMain(ps)` | 8.24 | 4.68 | 1.76× faster | 43.9 | 5.33× slower | 113 | 13.7× slower |
| `3d/bloom_upsample.hlsl:VSMain(vs)` | 6.90 | 2.28 | 3.02× faster | 44.3 | 6.42× slower | 108 | 15.6× slower |
| `3d/clustered_lights.hlsl:PSMain(ps)` | 12.8 | 4.34 | 2.95× faster | 50.1 | 3.91× slower | 122 | 9.54× slower |
| `3d/clustered_lights.hlsl:VSMain(vs)` | 8.25 | 3.19 | 2.59× faster | 48.3 | 5.86× slower | 118 | 14.3× slower |
| `3d/ibl.hlsl:PSMain(ps)` | 8.80 | 3.41 | 2.58× faster | 49.2 | 5.60× slower | 122 | 13.9× slower |
| `3d/ibl.hlsl:PSSky(ps)` | 8.22 | 3.10 | 2.65× faster | 48.1 | 5.86× slower | 127 | 15.5× slower |
| `3d/ibl.hlsl:VSMain(vs)` | 8.20 | 3.40 | 2.41× faster | 47.4 | 5.78× slower | 128 | 15.6× slower |
| `3d/ibl.hlsl:VSSky(vs)` | 9.01 | 3.27 | 2.76× faster | 50.1 | 5.56× slower | 122 | 13.5× slower |
| `3d/pbr_forward.hlsl:PSMain(ps)` | 16.1 | 7.26 | 2.22× faster | 50.0 | 3.10× slower | 131 | 8.14× slower |
| `3d/pbr_forward.hlsl:VSMain(vs)` | 9.63 | 3.79 | 2.54× faster | 46.6 | 4.84× slower | 130 | 13.5× slower |
| `3d/shadow_cascades.hlsl:PSMain(ps)` | 14.3 | 4.00 | 3.57× faster | 48.9 | 3.43× slower | 120 | 8.41× slower |
| `3d/shadow_cascades.hlsl:PSSpiral(ps)` | 14.6 | 4.38 | 3.33× faster | 50.0 | 3.43× slower | 125 | 8.61× slower |
| `3d/shadow_cascades.hlsl:VSMain(vs)` | 9.68 | 3.36 | 2.88× faster | 51.0 | 5.26× slower | 125 | 12.9× slower |
| `3d/shadow_depth.hlsl:VSMain(vs)` | 7.89 | 2.51 | 3.14× faster | 43.5 | 5.50× slower | 107 | 13.6× slower |
| `3d/skinned.hlsl:PSMain(ps)` | 6.73 | 2.62 | 2.57× faster | 47.1 | 7.00× slower | 107 | 15.9× slower |
| `3d/skinned.hlsl:VSMain(vs)` | 7.47 | 2.93 | 2.54× faster | 46.0 | 6.16× slower | 109 | 14.6× slower |
| `3d/ssao.hlsl:PSMain(ps)` | 10.2 | 4.36 | 2.34× faster | 46.9 | 4.59× slower | 126 | 12.3× slower |
| `3d/ssao.hlsl:VSMain(vs)` | 9.45 | 2.88 | 3.28× faster | 47.6 | 5.04× slower | 114 | 12.1× slower |
| `3d/stage_io.hlsl:PSMain(ps)` | 8.44 | 3.14 | 2.69× faster | 48.1 | 5.71× slower | 108 | 12.8× slower |
| `3d/stage_io.hlsl:VSMain(vs)` | 7.77 | 2.79 | 2.78× faster | 47.3 | 6.09× slower | 113 | 14.6× slower |
| `3d/tonemap.hlsl:PSMain(ps)` | 9.09 | 3.44 | 2.64× faster | 48.8 | 5.37× slower | 123 | 13.5× slower |
| `3d/tonemap.hlsl:VSMain(vs)` | 8.03 | 2.90 | 2.77× faster | 45.6 | 5.67× slower | 113 | 14.1× slower |
| `compute/append_consume.hlsl:CSMain(cs)` | 7.00 | 2.35 | 2.98× faster | 44.9 | 6.42× slower | 108 | 15.4× slower |
| `compute/cluster_build.hlsl:CSMain(cs)` | 8.19 | 3.80 | 2.16× faster | 47.8 | 5.83× slower | 119 | 14.5× slower |
| `compute/cull_instances.hlsl:CSMain(cs)` | 9.41 | 3.23 | 2.92× faster | 46.8 | 4.98× slower | 122 | 12.9× slower |
| `compute/depth_bounds.hlsl:CSMain(cs)` | 7.61 | 2.93 | 2.60× faster | 47.6 | 6.25× slower | 115 | 15.1× slower |
| `compute/histogram.hlsl:CSMain(cs)` | 6.47 | 3.04 | 2.13× faster | 44.9 | 6.95× slower | 104 | 16.1× slower |
| `compute/ibl_prefilter.hlsl:CSMain(cs)` | 13.9 | 5.66 | 2.46× faster | 48.8 | 3.51× slower | 123 | 8.87× slower |
| `compute/image_filter.hlsl:CSMain(cs)` | 10.6 | 3.14 | 3.36× faster | 47.4 | 4.49× slower | 114 | 10.8× slower |
| `compute/light_cull.hlsl:CSMain(cs)` | 7.71 | 3.05 | 2.53× faster | 48.4 | 6.27× slower | 114 | 14.8× slower |
| `compute/particles.hlsl:CSMain(cs)` | 9.64 | 3.26 | 2.96× faster | 45.6 | 4.73× slower | 126 | 13.0× slower |
| `compute/prefix_sum.hlsl:CSMain(cs)` | 7.92 | 2.61 | 3.03× faster | 43.2 | 5.46× slower | 108 | 13.7× slower |
| `compute/stage_layouts.hlsl:CSMain(cs)` | 7.79 | 2.76 | 2.82× faster | 47.7 | 6.12× slower | 110 | 14.1× slower |
| `lang/inheritance.hlsl:CSMain(cs)` | 7.17 | 3.24 | 2.22× faster | 47.0 | 6.55× slower | 110 | 15.3× slower |
| `lang/members.hlsl:CSMain(cs)` | 8.74 | 3.65 | 2.39× faster | 45.4 | 5.20× slower | 113 | 12.9× slower |
| `lang/operators.hlsl:CSMain(cs)` | 10.1 | 4.34 | 2.32× faster | 48.2 | 4.79× slower | 116 | 11.5× slower |
| `lang/resource_arrays.hlsl:CSMain(cs)` | 8.20 | 3.16 | 2.60× faster | 49.1 | 5.99× slower | 112 | 13.7× slower |
| `lang/resource_locals.hlsl:CSMain(cs)` | 10.2 | 4.21 | 2.43× faster | 45.3 | 4.42× slower | 112 | 10.9× slower |
| `lang/sizeof_static.hlsl:CSMain(cs)` | 7.56 | 2.99 | 2.53× faster | 47.9 | 6.34× slower | 108 | 14.3× slower |
| `lang/statics_and_literals.hlsl:CSMain(cs)` | 8.90 | 3.36 | 2.65× faster | 48.7 | 5.47× slower | 132 | 14.8× slower |
| `lang/templates.hlsl:CSMain(cs)` | 11.1 | 3.97 | 2.81× faster | 46.5 | 4.17× slower | 118 | 10.6× slower |
<!-- end:process-cold -->

## Output size

SPIR-V words (a word is 4 bytes) for the same entry points, from the NativeAOT build (the other two builds write identical bytes). "Without debug names" is the same compile with `KeepNames = false`. DXC is run with its defaults, which emit no names.

<!-- begin:size -->
| Entry point | DXC words | OneShader words | OneShader vs DXC | Without debug names | vs DXC |
|---|--:|--:|--:|--:|--:|
| `2d/blit.hlsl:PSMain(ps)` | 193 | 192 | -0.5% | 155 | -19.7% |
| `2d/blit.hlsl:VSMain(vs)` | 187 | 202 | +8.0% | 178 | -4.8% |
| `2d/blur.hlsl:PSMain(ps)` | 526 | 565 | +7.4% | 488 | -7.2% |
| `2d/blur.hlsl:VSMain(vs)` | 187 | 202 | +8.0% | 178 | -4.8% |
| `2d/color_grade.hlsl:PSMain(ps)` | 651 | 678 | +4.1% | 596 | -8.4% |
| `2d/color_grade.hlsl:VSMain(vs)` | 187 | 202 | +8.0% | 178 | -4.8% |
| `2d/msdf_text.hlsl:PSMain(ps)` | 650 | 676 | +4.0% | 594 | -8.6% |
| `2d/msdf_text.hlsl:VSMain(vs)` | 237 | 246 | +3.8% | 194 | -18.1% |
| `2d/renderer/quad.hlsl:PSBlur(ps)` | 466 | 496 | +6.4% | 442 | -5.2% |
| `2d/renderer/quad.hlsl:PSGrade(ps)` | 680 | 673 | -1.0% | 618 | -9.1% |
| `2d/renderer/quad.hlsl:PSSprite(ps)` | 221 | 214 | -3.2% | 174 | -21.3% |
| `2d/renderer/quad.hlsl:VSMain(vs)` | 635 | 659 | +3.8% | 575 | -9.4% |
| `2d/renderer/tile.hlsl:PSFlat(ps)` | 292 | 282 | -3.4% | 242 | -17.1% |
| `2d/renderer/tile.hlsl:PSIds(ps)` | 622 | 599 | -3.7% | 529 | -15.0% |
| `2d/renderer/tile.hlsl:VSMain(vs)` | 680 | 703 | +3.4% | 607 | -10.7% |
| `2d/renderer/ui.hlsl:PSMain(ps)` | 2961 | 2770 | -6.5% | 2641 | -10.8% |
| `2d/renderer/ui.hlsl:VSMain(vs)` | 846 | 838 | -0.9% | 690 | -18.4% |
| `2d/sdf_text.hlsl:PSMain(ps)` | 555 | 581 | +4.7% | 498 | -10.3% |
| `2d/sdf_text.hlsl:VSMain(vs)` | 237 | 246 | +3.8% | 194 | -18.1% |
| `2d/sprite.hlsl:PSMain(ps)` | 265 | 267 | +0.8% | 223 | -15.8% |
| `2d/sprite.hlsl:VSMain(vs)` | 278 | 287 | +3.2% | 223 | -19.8% |
| `2d/tilemap.hlsl:PSMain(ps)` | 347 | 359 | +3.5% | 311 | -10.4% |
| `2d/tilemap.hlsl:VSMain(vs)` | 517 | 549 | +6.2% | 454 | -12.2% |
| `2d/ui_rect.hlsl:PSMain(ps)` | 1076 | 1118 | +3.9% | 947 | -12.0% |
| `2d/ui_rect.hlsl:VSMain(vs)` | 612 | 747 | +22.1% | 554 | -9.5% |
| `3d/bloom_downsample.hlsl:PSMain(ps)` | 1621 | 1746 | +7.7% | 1672 | +3.1% |
| `3d/bloom_downsample.hlsl:VSMain(vs)` | 187 | 202 | +8.0% | 178 | -4.8% |
| `3d/bloom_upsample.hlsl:PSMain(ps)` | 815 | 853 | +4.7% | 779 | -4.4% |
| `3d/bloom_upsample.hlsl:VSMain(vs)` | 187 | 202 | +8.0% | 178 | -4.8% |
| `3d/clustered_lights.hlsl:PSMain(ps)` | 1608 | 1720 | +7.0% | 1462 | -9.1% |
| `3d/clustered_lights.hlsl:VSMain(vs)` | 456 | 470 | +3.1% | 333 | -27.0% |
| `3d/ibl.hlsl:PSMain(ps)` | 827 | 853 | +3.1% | 710 | -14.1% |
| `3d/ibl.hlsl:PSSky(ps)` | 543 | 553 | +1.8% | 432 | -20.4% |
| `3d/ibl.hlsl:VSMain(vs)` | 382 | 396 | +3.7% | 270 | -29.3% |
| `3d/ibl.hlsl:VSSky(vs)` | 187 | 202 | +8.0% | 178 | -4.8% |
| `3d/pbr_forward.hlsl:PSMain(ps)` | 1953 | 2215 | +13.4% | 1881 | -3.7% |
| `3d/pbr_forward.hlsl:VSMain(vs)` | 609 | 659 | +8.2% | 519 | -14.8% |
| `3d/shadow_cascades.hlsl:PSMain(ps)` | 1428 | 1168 | -18.2% | 1009 | -29.3% |
| `3d/shadow_cascades.hlsl:PSSpiral(ps)` | 1758 | 1385 | -21.2% | 1224 | -30.4% |
| `3d/shadow_cascades.hlsl:VSMain(vs)` | 442 | 456 | +3.2% | 332 | -24.9% |
| `3d/shadow_depth.hlsl:VSMain(vs)` | 348 | 380 | +9.2% | 301 | -13.5% |
| `3d/skinned.hlsl:PSMain(ps)` | 396 | 425 | +7.3% | 359 | -9.3% |
| `3d/skinned.hlsl:VSMain(vs)` | 728 | 1168 | +60.4% | 1055 | +44.9% |
| `3d/ssao.hlsl:PSMain(ps)` | 1066 | 1159 | +8.7% | 1040 | -2.4% |
| `3d/ssao.hlsl:VSMain(vs)` | 187 | 202 | +8.0% | 178 | -4.8% |
| `3d/stage_io.hlsl:PSMain(ps)` | 587 | 620 | +5.6% | 539 | -8.2% |
| `3d/stage_io.hlsl:VSMain(vs)` | 346 | 471 | +36.1% | 389 | +12.4% |
| `3d/tonemap.hlsl:PSMain(ps)` | 926 | 1017 | +9.8% | 922 | -0.4% |
| `3d/tonemap.hlsl:VSMain(vs)` | 187 | 202 | +8.0% | 178 | -4.8% |
| `compute/append_consume.hlsl:CSMain(cs)` | 588 | 634 | +7.8% | 503 | -14.5% |
| `compute/cluster_build.hlsl:CSMain(cs)` | 897 | 970 | +8.1% | 857 | -4.5% |
| `compute/cull_instances.hlsl:CSMain(cs)` | 893 | 979 | +9.6% | 804 | -10.0% |
| `compute/depth_bounds.hlsl:CSMain(cs)` | 631 | 627 | -0.6% | 547 | -13.3% |
| `compute/histogram.hlsl:CSMain(cs)` | 478 | 514 | +7.5% | 425 | -11.1% |
| `compute/ibl_prefilter.hlsl:CSMain(cs)` | 1249 | 1247 | -0.2% | 1160 | -7.1% |
| `compute/image_filter.hlsl:CSMain(cs)` | 706 | 734 | +4.0% | 693 | -1.8% |
| `compute/light_cull.hlsl:CSMain(cs)` | 808 | 923 | +14.2% | 697 | -13.7% |
| `compute/particles.hlsl:CSMain(cs)` | 808 | 910 | +12.6% | 753 | -6.8% |
| `compute/prefix_sum.hlsl:CSMain(cs)` | 451 | 475 | +5.3% | 398 | -11.8% |
| `compute/stage_layouts.hlsl:CSMain(cs)` | 858 | 910 | +6.1% | 789 | -8.0% |
| `lang/inheritance.hlsl:CSMain(cs)` | 406 | 486 | +19.7% | 379 | -6.7% |
| `lang/members.hlsl:CSMain(cs)` | 487 | 609 | +25.1% | 525 | +7.8% |
| `lang/operators.hlsl:CSMain(cs)` | 207 | 234 | +13.0% | 203 | -1.9% |
| `lang/resource_arrays.hlsl:CSMain(cs)` | 806 | 855 | +6.1% | 775 | -3.8% |
| `lang/resource_locals.hlsl:CSMain(cs)` | 551 | 609 | +10.5% | 540 | -2.0% |
| `lang/sizeof_static.hlsl:CSMain(cs)` | 233 | 443 | +90.1% | 399 | +71.2% |
| `lang/statics_and_literals.hlsl:CSMain(cs)` | 556 | 677 | +21.8% | 561 | +0.9% |
| `lang/templates.hlsl:CSMain(cs)` | 631 | 709 | +12.4% | 649 | +2.9% |
| **All 68 entry points** | **44126** | **46620** | **+5.7%** | **40258** | **-8.8%** |
<!-- end:size -->

## Method

- **Entry points.** Every (file, entry point, stage) that the execution-parity cases in `corpus/cases/*.case` compile, without duplicates: 68 entry points in 41 files (a file's entry points are compiled together in the in-process benchmark and one per process in the process benchmark). Includes and the cases' preprocessor definitions are passed to both compilers.
- **DXC.** The released Linux x64 binary v1.9.2609 (`libdxcompiler` 1.9.2609.5), pinned with its checksum in `tools/oracles.env`, run as `dxc -spirv -fspv-target-env=vulkan1.2 -HV 2021 -T <vs|ps|cs>_6_0 -E <entry> -I <dir> -Fo <out> <file>`; no optimization flags (so its default). It is used only as a black-box binary.
- **OneShader.** `bench/OneShader.Bench.Cli` (`oshc`): `ShaderCompiler.CompileFile` with one entry point, SPIR-V, default options (optimizer on, debug names kept), writes the blob. Published three ways: `dotnet publish -r linux-x64 -p:PublishAot=true`; `--self-contained false -p:PublishReadyToRun=true`; and `--self-contained false` (JIT, with the runtime's default tiered compilation). The framework-dependent builds run through their `oshc` launcher on the machine's .NET runtime.
- **Process timing.** The driver creates the process, waits for it to exit and takes the elapsed time on a monotonic clock; standard output and error are captured and discarded. Warm rounds run the tools interleaved (DXC, then each OneShader build) for each entry point, so a drift in machine speed affects all of them alike. Each round visits all 68 entry points; there are 15 rounds.
- **In-process timing.** BenchmarkDotNet as described above. The in-process toolchain is used because the repository's build output layout does not suit BenchmarkDotNet's generated projects; the compiler runs in the benchmark's own process.
- **Size.** Bytes written by each compiler on the cold pass, divided by 4.
- **Reproduce.** `bench/run.sh` (optionally `--warm N`, `--skip-inproc`, `--skip-process`, `--out DIR`) builds, publishes the three builds, fetches the pinned DXC with `tools/fetch-oracles.sh`, runs both benchmarks, writes `bench/results/` and regenerates the tables on this page and the charts in `docs/img/`. It needs the .NET SDK from `global.json`, Python 3, and a C toolchain with zlib for NativeAOT. `bench/OneShader.Bench list` prints the 68 entry points.

### Machine and load

- **Hardware and software.** AMD Ryzen 9 7950X3D (16 cores, 32 logical), 125 GiB RAM, Linux Mint 22.3, .NET SDK 10.0.402 with runtime 10.0.13, DXC v1.9.2609, BenchmarkDotNet 0.15.8. Default runtime settings (no AVX-512 switches, tiered compilation on, workstation non-concurrent GC in the benchmark process).
- **A shared machine, run under a lock.** The run was the only benchmark on the machine: it held the machine's benchmark lock for the whole run and no other project's heavy job overlapped. Load averages (1 minute) were sampled every 5 seconds by the benchmark itself: at the start 1.24 (1-minute), 1.44 (5-minute), 1.49 (15-minute); during the process timing 1.03 to 1.24 over 41 samples; during the in-process benchmark 1.02 to 1.42 over 95 samples; at the end of the process timing 1.13, 1.26 and 1.41, and 1.09, 1.14 and 1.28 when the whole run finished. The remaining load of about 1 is background work on the machine that the benchmark did not control.
- **One run.** The published numbers are from a single full run (2026-10-25). The spread between repeated warm runs is shown in the process table: a NativeAOT process lasts about 3 ms, so its slowest run is typically 1.5× its entry point's median (at most 2.1×), while DXC's slowest run is typically 1.15× its median (at most 1.5×). The ratios use medians. In-process results are kept as BenchmarkDotNet's `*-report-full.json` in `bench/results/inproc/`, with the file list per target (`params-*.tsv`) the report uses to restore the file names BenchmarkDotNet shortens.
- **Not measured.** Memory use of either compiler (the NativeAOT binary is 2.9 MB; DXC is a 0.76 MB launcher plus a 31.8 MB compiler library); Windows or macOS; ARM64; multi-threaded or batch compiles (DXC and OneShader are each run one entry point at a time); compile time against other shader compilers.
