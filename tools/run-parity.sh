#!/usr/bin/env bash
# Runs the execution-parity suite (tests/OneShader.ParityTests): fetches the reference tools
# (DXC, spirv-val, Vulkan validation layer) if needed, then compiles every corpus case with the
# reference and candidate compilers, runs both on the GPU with validation on, and compares.
#
#   tools/run-parity.sh [test runner arguments...]
#
# Environment:
#   ONESHADER_TOOLS       where the tools live (default: .tools in the repo)
#   ONESHADER_ARTIFACTS   build output and mismatching images (default: .artifacts in the repo)
#   ONESHADER_VK_DEVICE   substring of the Vulkan device name to use (default: first discrete GPU)
#   ONESHADER_VK_DEVICES  space-separated list; the suite runs once per entry, e.g. "NVIDIA RADV"
#   ONESHADER_PARITY_DUMP set to 1 to also write every rendered image as PPM
# Uses the system Vulkan loader and driver; nothing is installed outside the tool directory.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/.." && pwd)"
export ONESHADER_TOOLS="${ONESHADER_TOOLS:-$root/.tools}"
export ONESHADER_ARTIFACTS="${ONESHADER_ARTIFACTS:-$root/.artifacts}"

"$here/fetch-oracles.sh" "$ONESHADER_TOOLS"

status=0
devices="${ONESHADER_VK_DEVICES:-}"
if [ -z "$devices" ]; then
  devices="${ONESHADER_VK_DEVICE:-}"
fi
if [ -z "$devices" ]; then
  devices="-"
fi
for device in $devices; do
  if [ "$device" = "-" ]; then
    unset ONESHADER_VK_DEVICE
    echo "parity: default device"
  else
    export ONESHADER_VK_DEVICE="$device"
    echo "parity: device matching '$device'"
  fi
  dotnet run --project "$root/tests/OneShader.ParityTests" -c Release -- --no-progress --xunit-diagnostics on "$@" || status=$?
done
exit $status
