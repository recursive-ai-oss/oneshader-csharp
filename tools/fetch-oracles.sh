#!/usr/bin/env bash
# Fetches the pinned reference tools (see oracles.env) into a directory:
#   fetch-oracles.sh [DIR]      DIR defaults to $ONESHADER_TOOLS, then .tools in the repo.
# Result: DIR/bin/{dxc,spirv-val,spirv-dis,spirv-as,spirv-opt}, DIR/lib/ (DXC libraries),
# DIR/vklayers/ (Khronos validation layer; point VK_ADD_LAYER_PATH at it), and DIR/bin/naga when cargo is on PATH.
# The system Vulkan loader and GPU driver are used as they are; nothing here replaces them.
# Idempotent (a stamp file records the pinned versions), verifies SHA-256, needs no sudo and
# installs nothing outside DIR. Linux x64 only.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=oracles.env
. "$here/oracles.env"

dir="${1:-${ONESHADER_TOOLS:-$here/../.tools}}"
mkdir -p "$dir"
dir="$(cd "$dir" && pwd)"

platform="$(uname -s)-$(uname -m)"
case "$platform" in
  Linux-x86_64|Darwin-arm64) ;;
  *) echo "fetch-oracles: no binaries are pinned for $platform; skipping" >&2; exit 0 ;;
esac

sha256_of() { if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }

if [ "$platform" = Darwin-arm64 ]; then
  # SPIRV-Tools from the macOS Vulkan SDK; DXC has no macOS release, so it is not installed.
  stamp="$dir/.stamp"
  want="macos-sdk=$VULKAN_SDK_MACOS_SHA256"
  if [ -f "$stamp" ] && [ "$(cat "$stamp")" = "$want" ] && [ -x "$dir/bin/spirv-val" ]; then
    echo "fetch-oracles: up to date in $dir (SPIRV-Tools from Vulkan SDK $VULKAN_SDK_VERSION; no macOS DXC release, DXC-based tests skip)"
  else
    work="$(mktemp -d "$dir/.work.XXXXXX")"
    trap 'rm -rf "$work"' EXIT
    echo "fetch-oracles: SPIRV-Tools from Vulkan SDK $VULKAN_SDK_VERSION for macOS (large download)"
    if [ -n "${ONESHADER_FETCH_CACHE:-}" ] && [ -f "$ONESHADER_FETCH_CACHE/vulkan_sdk_macos.zip" ]; then
      cp "$ONESHADER_FETCH_CACHE/vulkan_sdk_macos.zip" "$work/sdk.zip"
    else
      curl --fail --silent --show-error --location --retry 3 --output "$work/sdk.zip" "$VULKAN_SDK_MACOS_URL"
    fi
    [ "$(sha256_of "$work/sdk.zip")" = "$VULKAN_SDK_MACOS_SHA256" ] \
      || { echo "fetch-oracles: checksum mismatch for $VULKAN_SDK_MACOS_URL" >&2; exit 1; }
    mkdir "$work/installer"
    unzip -q "$work/sdk.zip" -d "$work/installer"
    # The installer is a Qt application with a headless mode; it writes only under --root.
    "$work"/installer/*.app/Contents/MacOS/* --root "$work/sdk" --accept-licenses --default-answer --confirm-command install >"$work/install.log" 2>&1 \
      || { tail -20 "$work/install.log" >&2; echo "fetch-oracles: SDK installer failed" >&2; exit 1; }
    mkdir -p "$dir/bin"
    for t in spirv-val spirv-dis spirv-as spirv-opt; do
      cp -f "$work/sdk/macOS/bin/$t" "$dir/bin/$t"
    done
    chmod +x "$dir/bin/"*
    echo "$want" > "$stamp"
    echo "fetch-oracles: installed into $dir/bin"
  fi
else
stamp="$dir/.stamp"
want="dxc=$DXC_SHA256 sdk=$VULKAN_SDK_SHA256"
if [ -f "$stamp" ] && [ "$(cat "$stamp")" = "$want" ] && [ -x "$dir/bin/dxc" ] && [ -x "$dir/bin/spirv-val" ] \
   && [ -f "$dir/vklayers/libVkLayer_khronos_validation.so" ]; then
  echo "fetch-oracles: up to date in $dir (dxc $DXC_VERSION, SPIRV-Tools from Vulkan SDK $VULKAN_SDK_VERSION)"
  UP_TO_DATE=1
fi

if [ "${UP_TO_DATE:-0}" != 1 ]; then
work="$(mktemp -d "$dir/.work.XXXXXX")"
trap 'rm -rf "$work"' EXIT

fetch() { # url sha256 file
  if [ -n "${ONESHADER_FETCH_CACHE:-}" ] && [ -f "$ONESHADER_FETCH_CACHE/$3" ]; then cp "$ONESHADER_FETCH_CACHE/$3" "$work/$3"; else curl --fail --silent --show-error --location --retry 3 --output "$work/$3" "$1"; fi
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
  '*/x86_64/bin/spirv-val' '*/x86_64/bin/spirv-dis' '*/x86_64/bin/spirv-as' '*/x86_64/bin/spirv-opt' \
  '*/x86_64/lib/libVkLayer_khronos_validation.so' \
  '*/x86_64/share/vulkan/explicit_layer.d/VkLayer_khronos_validation.json'

mkdir -p "$dir/bin" "$dir/lib"
cp -f "$work/dxc/bin/dxc" "$dir/bin/dxc"
cp -f "$work/dxc/lib/"*.so "$dir/lib/"
for t in spirv-val spirv-dis spirv-as spirv-opt; do
  cp -f "$work"/sdk/*/x86_64/bin/$t "$dir/bin/$t"
done
chmod +x "$dir/bin/"*
# Layer manifest and library side by side, so the manifest resolves its library locally.
mkdir -p "$dir/vklayers"
cp -f "$work"/sdk/*/x86_64/lib/libVkLayer_khronos_validation.so "$dir/vklayers/"
sed 's#"library_path": *"[^"]*"#"library_path": "./libVkLayer_khronos_validation.so"#' \
  "$work"/sdk/*/x86_64/share/vulkan/explicit_layer.d/VkLayer_khronos_validation.json \
  > "$dir/vklayers/VkLayer_khronos_validation.json"
# dxc finds its libraries relative to its own location; keep them one level up from bin/.
echo "$want" > "$stamp"
echo "fetch-oracles: installed into $dir/bin"
fi
fi

# naga (WGSL validator): installed with cargo when available; skipped with a note otherwise.
if [ ! -x "$dir/bin/naga" ] || [ "$("$dir/bin/naga" --version 2>/dev/null || true)" != "$NAGA_VERSION" ]; then
  if command -v cargo >/dev/null 2>&1; then
    echo "fetch-oracles: naga $NAGA_VERSION (cargo install)"
    cargo install naga-cli --version "$NAGA_VERSION" --locked --root "$dir/naga" >/dev/null 2>&1 \
      && cp -f "$dir/naga/bin/naga" "$dir/bin/naga" || echo "fetch-oracles: naga install failed; WGSL validation tests will skip" >&2
  else
    echo "fetch-oracles: cargo not found; WGSL validation tests will skip" >&2
  fi
fi
