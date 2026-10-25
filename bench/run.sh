#!/usr/bin/env bash
# Reproduces every number in docs/performance.md.
#
#   bench/run.sh [--warm N] [--skip-inproc] [--skip-process] [--out DIR]
#
# 1. builds the benchmark and publishes the command-line compiler three ways (normal JIT build, ReadyToRun, NativeAOT);
# 2. fetches the pinned DXC release (tools/fetch-oracles.sh) if it is not in $ONESHADER_TOOLS;
# 3. process timing: DXC and the three builds compile every corpus entry point, cold once, then --warm times (default 10);
# 4. in-process timing with BenchmarkDotNet: every corpus file, SPIR-V (optimized and not), MSL and WGSL;
# 5. bench/make-report.py turns the results into the tables in docs/performance.md and the charts in docs/img.
#
# Results land in bench/results (default). Run it on an otherwise idle machine; the run records the load average
# while it runs, and the numbers are not comparable if something else was busy.
#
# Environment: ONESHADER_TOOLS (default .tools in the repo), ONESHADER_ARTIFACTS (build output, default .artifacts).
# Needs the .NET SDK (see global.json), python3 and, for the NativeAOT build, a C toolchain (clang or gcc and zlib).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/.." && pwd)"
export ONESHADER_TOOLS="${ONESHADER_TOOLS:-$root/.tools}"
export ONESHADER_ARTIFACTS="${ONESHADER_ARTIFACTS:-$root/.artifacts}"
out="$here/results"
warm=10
do_inproc=1
do_process=1
while [ $# -gt 0 ]; do
  case "$1" in
    --warm) warm="$2"; shift 2 ;;
    --out) out="$2"; shift 2 ;;
    --skip-inproc) do_inproc=0; shift ;;
    --skip-process) do_process=0; shift ;;
    *) echo "unknown argument $1" >&2; exit 2 ;;
  esac
done
mkdir -p "$out/inproc"
out="$(cd "$out" && pwd)"

"$root/tools/fetch-oracles.sh" "$ONESHADER_TOOLS"
dxc="$ONESHADER_TOOLS/bin/dxc"

pub="$ONESHADER_ARTIFACTS/bench-publish"
rm -rf "$pub"
cli="$root/bench/OneShader.Bench.Cli"
dotnet build "$root/bench/OneShader.Bench" -c Release -v quiet -clp:NoSummary
dotnet publish "$cli" -c Release -r linux-x64 --self-contained false -p:PublishAot=false -o "$pub/jit" -v quiet -clp:NoSummary
dotnet publish "$cli" -c Release -r linux-x64 --self-contained false -p:PublishAot=false -p:PublishReadyToRun=true -o "$pub/r2r" -v quiet -clp:NoSummary
dotnet publish "$cli" -c Release -r linux-x64 -p:PublishAot=true -o "$pub/aot" -v quiet -clp:NoSummary

bench_dll="$(find "$ONESHADER_ARTIFACTS/bin/OneShader.Bench" -name OneShader.Bench.dll -path '*release*' | head -1)"
cd "$root"

if [ "$do_process" = 1 ]; then
  dotnet "$bench_dll" process --dxc "$dxc" --jit "$pub/jit/oshc" --r2r "$pub/r2r/oshc" --aot "$pub/aot/oshc" \
    --warm "$warm" --out "$out/process.json" --work "$ONESHADER_ARTIFACTS/bench-work"
fi

if [ "$do_inproc" = 1 ]; then
  rm -rf "$ONESHADER_ARTIFACTS/bench-bdn"
  ONESHADER_BENCH_OUT="$out/inproc" dotnet "$bench_dll" inproc --filter '*' --artifacts "$ONESHADER_ARTIFACTS/bench-bdn"
  cp "$ONESHADER_ARTIFACTS"/bench-bdn/results/*-report-full.json "$out/inproc/"
fi

python3 "$here/make-report.py" "$out"
