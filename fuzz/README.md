# Fuzzing

`OneShader.Fuzz` attacks every compile path with hostile text. It is test tooling: it references SharpFuzz and nothing from it ships.

## Targets

| Target | What an input must satisfy |
|---|---|
| `pp` | The preprocessor, with up to eight include files per input, never throws, gives diagnostics with positions of 1 or more, and gives the same text twice. |
| `frontend` | Preprocess, parse, check and lower never throw; no internal-error diagnostic (OSH0399, OSH7000, OSH6001); a failed lowering has an error diagnostic. |
| `optimizer` | If the input lowers, the IR verifier accepts it before and after the optimizer, the entry points keep their interface, and two runs print the same IR. |
| `backends` | `ShaderCompiler.Compile` for SPIR-V, MSL and WGSL, with and without the optimizer, never throws and returns no internal-error diagnostic (a caught exception counts as a finding, so the safety net cannot hide a crash); two compiles give identical diagnostics and bytes; no blobs come with errors. |
| `validity` | `backends`, and every accepted SPIR-V blob passes `spirv-val --target-env vulkan1.2` and every WGSL blob passes `naga`, run as black-box processes. MSL blobs are collected under `<out>/msl/` for `tools/validate-msl.sh` on a Mac. |

An input is the main file's text; a U+0001 character starts the next include file (`f1.h`, `f2.h`, ...). Seeds are the shaders under `corpus/` (their `.hlsli` headers resolve by name) plus generated preprocessor soup for `pp`.

## Running

```
dotnet publish fuzz/OneShader.Fuzz -c Release -o <dir>
dotnet <dir>/OneShader.Fuzz.dll --run <target> --seconds 600 --seed 1 --corpus corpus --out <out> [--tools <dir>] [--sample N]
```

`fuzz/run.sh <seconds> <out-dir> [targets...]` does this for several targets in parallel: it publishes, instruments `OneShader.dll` with `sharpfuzz` (dotnet tool `SharpFuzz.CommandLine`) when that is on `PATH`, and starts one process per target with its own seed (`FUZZ_SEED` is the first). `--tools` (or `ONESHADER_TOOLS`) is the directory whose `bin/` holds `spirv-val` and `naga`, needed by `validity`; `--sample N` validates every N-th accepted input.

The runner is a mutation fuzzer in one process. When `OneShader.dll` is instrumented it keeps the inputs that reach new coverage (the `features` count is the number of distinct edge slots seen); without instrumentation it still runs, blind. Findings are written to `<out>/findings/<target>-<kind>-<hash>.hlsl` with the message in a `.txt` beside it. The file `<out>/current-<target>-<seed>.bin` always holds the input being run, which is how a stack overflow or an out-of-memory kill is reproduced (those end the process; the next `--file` run on that input shows the crash). An input that takes longer than 5 s of compiler time is a finding (`slow`); one that takes 60 s ends the run (exit 3).

`--file <path> [target]` replays one input through a target (default `all`) and exits 1 on a finding. `--diagnostics <path>` prints what the compiler says. `--time <path>` prints compile time per target and optimizer setting, to find the stage that is slow on an input. `--libfuzzer <target>` is the SharpFuzz/libFuzzer loop for use with `libfuzzer-dotnet`; it has not been run.

## Regressions

`fuzz/regressions/` holds one small input per defect the fuzzer found, named for what it exercises. `tests/OneShader.Tests/FuzzRegressionTests.cs` compiles each for every target with and without the optimizer, asserts no internal error and byte-identical output on a second compile, and runs `spirv-val` and `naga` over the output when they are installed. It also generates the size-based cases that need no file (an exponential macro, 100 000 array dimensions, a 20 000-function call chain, 20 000 errors). Replay all of them:

```
for f in fuzz/regressions/*.hlsl; do dotnet <dir>/OneShader.Fuzz.dll --file $f all; done
```

## Campaign results

Final run, one batched job on a 32-core AMD Ryzen 9 7950X3D (.NET SDK 10.0.402, load 0.07 before), all five targets in parallel, 600 s each, instrumented `OneShader.dll`, seeded from `corpus/` (push-constant shaders included), lock held:

| Target | Seed | Inputs run | Execs/s | Coverage features | Findings |
|---|---|---|---|---|---|
| `pp` | 301 | 2 911 533 | 4 852 | 2 502 | 0 |
| `frontend` | 302 | 2 042 591 | 3 404 | 15 617 | 0 |
| `optimizer` | 303 | 1 472 279 | 2 454 | 18 415 | 0 |
| `backends` | 304 | 1 199 438 | 1 999 | 23 922 | 0 |
| `validity` | 305 | 1 156 570 | 1 928 | 23 795 | 0 (7 859 SPIR-V through `spirv-val`, 7 859 WGSL through `naga`) |

`features` counts distinct coverage-map slots reached, not source lines. The run before it (seed 201, previous commit) found three more defects, now fixed and in `fuzz/regressions/`; each earlier local round (up to 25 minutes per target) ended clean after its fixes.

MSL: 300 accepted `validity` mutants compiled with `metal -c` and packed with `metallib` under `tools/validate-msl.sh` (layout checks on): 300 passed, 0 failed.

Execution parity (`tools/run-parity.sh`, validation layer on) after the fixes: 375 tests, 320 passed, 55 skipped (Metal-only and known-gap cases), 0 failed, 0 validation messages, on an NVIDIA RTX 4070 and on RADV.

What the fuzzer cannot see: it checks that output is valid and deterministic, not that it computes the right values; the parity suite covers that for the corpus only. Mutations are mostly token-level edits of corpus shaders, so deep semantic features reached only by a chain of several correct edits are covered less well. The `--libfuzzer` entry point has not been run.
