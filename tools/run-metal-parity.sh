#!/usr/bin/env bash
# Metal execution parity: every corpus case runs as MSL on a Metal GPU and as DXC-compiled SPIR-V on Vulkan, and the outputs
# are compared. The two GPUs are on different machines, so the suite has three steps that share one exchange directory:
#
#   tools/run-metal-parity.sh export  BUNDLE   # Linux/Windows with Vulkan + DXC: MSL, inputs and reference outputs -> BUNDLE
#   tools/run-metal-parity.sh run     BUNDLE   # Mac: builds tools/metal-runner and runs every case in BUNDLE
#   tools/run-metal-parity.sh compare BUNDLE   # anywhere: compares, prints the per-case table (also BUNDLE/table.txt)
#
# `run` is the Mac's one command. It needs Xcode (swiftc) and nothing else; no .NET. Metal API validation and shader
# validation are on (MTL_DEBUG_LAYER, MTL_SHADER_VALIDATION); anything the Metal runtime writes to stderr counts as a
# validation message and fails the case.
#
# Environment:
#   ONESHADER_METAL_CASES   export only these cases (comma-separated names)
#   ONESHADER_METAL_MATH    run: `fast` (default, Metal's own default) or `safe` (no fast-math)
#   ONESHADER_METAL_WORK    run: scratch directory for the runner binary (default ~/oneshader/metal-runner)
# plus the variables of tools/run-parity.sh for export and compare.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/.." && pwd)"
mode="${1:-}"
bundle="${2:-}"
if [ -z "$mode" ] || [ -z "$bundle" ]; then
  sed -n '2,12p' "${BASH_SOURCE[0]}" >&2
  exit 2
fi
mkdir -p "$bundle"
bundle="$(cd "$bundle" && pwd)"

case "$mode" in
  export|compare)
    export ONESHADER_TOOLS="${ONESHADER_TOOLS:-$root/.tools}"
    export ONESHADER_ARTIFACTS="${ONESHADER_ARTIFACTS:-$root/.artifacts}"
    export ONESHADER_METAL_BUNDLE="$bundle"
    [ "$mode" = export ] && "$here/fetch-oracles.sh" "$ONESHADER_TOOLS"
    method=$([ "$mode" = export ] && echo Export || echo Compare)
    status=0
    dotnet run --project "$root/tests/OneShader.ParityTests" -c Release -- --no-progress \
      --filter-method "*MetalParityTests.$method" || status=$?
    [ "$mode" = compare ] && [ -f "$bundle/table.txt" ] && cat "$bundle/table.txt"
    exit $status
    ;;
  run)
    work="${ONESHADER_METAL_WORK:-$HOME/oneshader/metal-runner}"
    mkdir -p "$work"
    # -swift-version 5: the runner is a single-threaded script and does not need Swift 6 concurrency checking.
    xcrun swiftc -O -swift-version 5 "$here/metal-runner/main.swift" -o "$work/metal-runner"
    count=0
    for dir in "$bundle"/*/; do
      [ -f "$dir/case.txt" ] || continue
      rm -rf "$dir/metal" "$dir/exit.txt"
      code=0
      # macOS has no timeout(1): a watchdog kills a case that hangs.
      MTL_DEBUG_LAYER=1 MTL_SHADER_VALIDATION=1 ONESHADER_METAL_MATH="${ONESHADER_METAL_MATH:-fast}" \
        "$work/metal-runner" "$dir" >"$dir/stdout.log" 2>"$dir/stderr.log" &
      pid=$!
      ( sleep 120 && kill -9 "$pid" 2>/dev/null ) &
      watchdog=$!
      wait "$pid" || code=$?
      kill "$watchdog" 2>/dev/null || true
      wait "$watchdog" 2>/dev/null || true
      echo "$code" >"$dir/exit.txt"
      echo "$(basename "$dir"): exit $code $(head -c 160 "$dir/stdout.log" | head -1)"
      count=$((count + 1))
    done
    echo "ran $count cases"
    ;;
  *)
    echo "unknown mode '$mode' (export, run or compare)" >&2
    exit 2
    ;;
esac
