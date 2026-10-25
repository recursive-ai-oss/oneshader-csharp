#!/usr/bin/env bash
# Builds the fuzz harness, instruments a copy of OneShader.dll for coverage feedback, and runs targets in parallel.
#
#   fuzz/run.sh <seconds> <out-dir> [target...]        (default targets: pp frontend optimizer backends validity)
#
# Needs the .NET 10 SDK on PATH, `sharpfuzz` (dotnet tool SharpFuzz.CommandLine) on PATH, and for the validity
# target spirv-val and naga under $ONESHADER_TOOLS/bin. Seed shaders come from corpus/. Each target runs in its
# own process with its own seed; findings are written to <out-dir>/<target>/findings/.
set -eu
secs="${1:?seconds}"; out="${2:?out dir}"; shift 2
targets=("$@"); [ ${#targets[@]} -gt 0 ] || targets=(pp frontend optimizer backends validity)
root="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$out"
dotnet publish "$root/fuzz/OneShader.Fuzz" -c Release -o "$out/bin" -v quiet -clp:NoSummary
if command -v sharpfuzz >/dev/null; then sharpfuzz "$out/bin/OneShader.dll" >/dev/null; else echo "sharpfuzz not found: running without coverage feedback" >&2; fi
pids=()
seed=${FUZZ_SEED:-1}
for t in "${targets[@]}"; do
  mkdir -p "$out/$t"
  dotnet "$out/bin/OneShader.Fuzz.dll" --run "$t" --seconds "$secs" --seed "$seed" --corpus "$root/corpus" --out "$out/$t" \
    --tools "${ONESHADER_TOOLS:-$root/.tools}" > "$out/$t.log" 2>&1 &
  pids+=($!); seed=$((seed+1))
done
rc=0
for p in "${pids[@]}"; do wait "$p" || rc=1; done
for t in "${targets[@]}"; do grep -E '^(DONE|FINDING|HANG)' "$out/$t.log" || tail -3 "$out/$t.log"; done
exit $rc
