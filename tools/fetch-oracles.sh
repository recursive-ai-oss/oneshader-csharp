#!/usr/bin/env bash
# Fetches the pinned reference tools (see oracles.env) into a directory:
#   fetch-oracles.sh [DIR]      DIR defaults to $ONESHADER_TOOLS, then the shared local cache.
# Result: DIR/bin/{dxc,spirv-val,spirv-dis,spirv-as,spirv-opt}, DIR/lib/ (DXC libraries).
# Idempotent (a stamp file records the pinned versions), verifies SHA-256, needs no sudo and
# installs nothing outside DIR. Linux x64 only.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=oracles.env
. "$here/oracles.env"

dir="${1:-${ONESHADER_TOOLS:-$PWD/.tools}}"
mkdir -p "$dir"
dir="$(cd "$dir" && pwd)"

if [ "$(uname -s)-$(uname -m)" != "Linux-x86_64" ]; then
  echo "fetch-oracles: only Linux x86_64 binaries are pinned; skipping on $(uname -s)-$(uname -m)" >&2
  exit 0
fi

stamp="$dir/.stamp"
want="dxc=$DXC_SHA256 sdk=$VULKAN_SDK_SHA256"
if [ -f "$stamp" ] && [ "$(cat "$stamp")" = "$want" ] && [ -x "$dir/bin/dxc" ] && [ -x "$dir/bin/spirv-val" ]; then
  echo "fetch-oracles: up to date in $dir (dxc $DXC_VERSION, SPIRV-Tools from Vulkan SDK $VULKAN_SDK_VERSION)"
  exit 0
fi

work="$(mktemp -d "$dir/.work.XXXXXX")"
trap 'rm -rf "$work"' EXIT

fetch() { # url sha256 file
  curl --fail --silent --show-error --location --retry 3 --output "$work/$3" "$1"
  echo "$2  $work/$3" | sha256sum --check --status \
    || { echo "fetch-oracles: checksum mismatch for $1" >&2; exit 1; }
}

echo "fetch-oracles: dxc $DXC_VERSION"
fetch "$DXC_URL" "$DXC_SHA256" dxc.tar.gz
mkdir "$work/dxc"
tar -xzf "$work/dxc.tar.gz" -C "$work/dxc"

echo "fetch-oracles: SPIRV-Tools from Vulkan SDK $VULKAN_SDK_VERSION (large download)"
fetch "$VULKAN_SDK_URL" "$VULKAN_SDK_SHA256" sdk.tar.xz
mkdir "$work/sdk"
tar -xJf "$work/sdk.tar.xz" -C "$work/sdk" --wildcards \
  '*/x86_64/bin/spirv-val' '*/x86_64/bin/spirv-dis' '*/x86_64/bin/spirv-as' '*/x86_64/bin/spirv-opt'

mkdir -p "$dir/bin" "$dir/lib"
cp -f "$work/dxc/bin/dxc" "$dir/bin/dxc"
cp -f "$work/dxc/lib/"*.so "$dir/lib/"
for t in spirv-val spirv-dis spirv-as spirv-opt; do
  cp -f "$work"/sdk/*/x86_64/bin/$t "$dir/bin/$t"
done
chmod +x "$dir/bin/"*
# dxc finds its libraries relative to its own location; keep them one level up from bin/.
echo "$want" > "$stamp"
echo "fetch-oracles: installed into $dir/bin"
