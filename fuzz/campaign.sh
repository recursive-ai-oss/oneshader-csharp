#!/usr/bin/env bash
# One batched campaign on a shared machine: claims ~/WORKER-BENCH.lock, runs every target for <seconds> in parallel, releases the lock.
#
#   fuzz/campaign.sh <seconds> <work-dir> [seed]
#
# Needs the .NET 10 SDK on PATH and DOTNET_ROOT set, spirv-val and naga under $ONESHADER_TOOLS/bin, and `sharpfuzz`
# (dotnet tool SharpFuzz.CommandLine) which is installed into <work-dir>/tool when missing. Exits 1 without running anything
# when the lock is held. Results are in <work-dir>/run/<target>.log and <work-dir>/run/<target>/findings.
set -eu
secs="${1:?seconds}"; work="${2:?work dir}"; seed="${3:-1}"
root="$(cd "$(dirname "$0")/.." && pwd)"
lock="$HOME/WORKER-BENCH.lock"
now=$(date -Iseconds)
end=$(date -Iseconds -d "+$(( secs + 600 )) seconds")
set -o noclobber
echo "oneshader fuzz campaign $now expected_end $end" > "$lock" || { echo "lock held: $(cat "$lock")" >&2; exit 1; }
set +o noclobber
trap 'rm -f "$lock"' EXIT
mkdir -p "$work"
if ! command -v sharpfuzz >/dev/null && [ ! -x "$work/tool/sharpfuzz" ]; then
  dotnet tool install SharpFuzz.CommandLine --tool-path "$work/tool" >/dev/null
fi
export PATH="$work/tool:$PATH"
export ONESHADER_TOOLS="${ONESHADER_TOOLS:-$HOME/oneshader/tools}"
echo "load before: $(cut -d' ' -f1-3 /proc/loadavg)"
FUZZ_SEED="$seed" "$root/fuzz/run.sh" "$secs" "$work/run"
rc=$?
echo "load after: $(cut -d' ' -f1-3 /proc/loadavg)"
exit $rc
