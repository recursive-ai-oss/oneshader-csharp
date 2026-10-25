#!/usr/bin/env bash
# Runs every corpus case's WGSL in headless Chromium (WebGPU, hardware adapter) and compares the results with
# DXC's SPIR-V on Vulkan (tests/OneShader.ParityTests, WebGpuParityTests; browser side in tools/webgpu).
#
#   tools/run-webgpu-parity.sh [test runner arguments...]
#
# Environment:
#   ONESHADER_TOOLS          reference tools (default: .tools in the repo; fetched when missing)
#   ONESHADER_ARTIFACTS      build output and the run's working directory (default: .artifacts in the repo)
#   ONESHADER_WEBGPU_HOME    where the Python environment and the Chromium download live (default: ~/.cache/oneshader-webgpu)
#   ONESHADER_WEBGPU_CASES   comma-separated case names to run instead of all
#   ONESHADER_VK_DEVICE      substring of the Vulkan device name for the reference run
#   ONESHADER_GPU_LOCK       a lock file to claim for the duration (for shared GPUs); the run waits until it is free
# Needs uv (https://docs.astral.sh/uv/) to create the Python environment; nothing is installed outside the directories above.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/.." && pwd)"
export ONESHADER_TOOLS="${ONESHADER_TOOLS:-$root/.tools}"
export ONESHADER_ARTIFACTS="${ONESHADER_ARTIFACTS:-$root/.artifacts}"
home="${ONESHADER_WEBGPU_HOME:-$HOME/.cache/oneshader-webgpu}"

if [ -n "${ONESHADER_GPU_LOCK:-}" ]; then
  until (set -o noclobber; echo "oneshader webgpu parity $(date +%H:%M) expected +20min" > "$ONESHADER_GPU_LOCK") 2>/dev/null; do
    echo "waiting for $ONESHADER_GPU_LOCK: $(cat "$ONESHADER_GPU_LOCK" 2>/dev/null || true)"
    sleep 30
  done
  trap 'rm -f "$ONESHADER_GPU_LOCK"' EXIT
fi

"$here/fetch-oracles.sh" "$ONESHADER_TOOLS"

export PLAYWRIGHT_BROWSERS_PATH="$home/browsers"
if [ ! -x "$home/venv/bin/python" ]; then
  uv venv --python 3.13 "$home/venv"
  VIRTUAL_ENV="$home/venv" uv pip install playwright
fi
if [ ! -d "$home/browsers" ]; then
  "$home/venv/bin/python" -m playwright install chromium
fi

work="$ONESHADER_ARTIFACTS/webgpu"
mkdir -p "$work"
export ONESHADER_WEBGPU_DIR="$work"
export ONESHADER_WEBGPU_PYTHON="$home/venv/bin/python"

status=0
dotnet run --project "$root/tests/OneShader.ParityTests" -c Release -- --no-progress --filter-class '*WebGpuParityTests' "$@" || status=$?

echo
echo "adapter:"
cat "$work/results/adapter.json" 2>/dev/null || echo "(none)"
echo
echo "per-case results (case, status, worst error):"
sort "$work/webgpu-table.tsv" 2>/dev/null | column -t -s $'\t' || true
exit $status
