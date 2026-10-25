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

Results are appended below by the campaign runs.
