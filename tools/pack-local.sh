#!/usr/bin/env bash
# Packs RecursiveAI.OneShader and RecursiveAI.OneShader.Build into a local NuGet feed folder.
#   pack-local.sh [FEED_DIR]     FEED_DIR defaults to .feed in the repo (ignored by git).
# The version is <base>-dev.<n>, where <base> is OneShaderVersion's number from
# Directory.Build.props and <n> is the commit count, so one commit always packs to one version.
# Set ONESHADER_BUILD_NUMBER to pack another number. NuGet caches packages by version: when you
# repack uncommitted changes, pick a new number or clear the consumer's package cache.
# Needs the .NET SDK on PATH; installs nothing.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/.." && pwd)"
feed="${1:-$root/.feed}"
mkdir -p "$feed"
feed="$(cd "$feed" && pwd)"

base="$(sed -n 's|.*<OneShaderVersion>\([0-9][0-9.]*\)-dev</OneShaderVersion>.*|\1|p' "$root/Directory.Build.props")"
if [ -z "$base" ]; then
  echo "pack-local: cannot read OneShaderVersion (expected <number>-dev) from Directory.Build.props" >&2
  exit 1
fi
n="${ONESHADER_BUILD_NUMBER:-$(git -C "$root" rev-list --count HEAD 2>/dev/null || echo 0)}"
version="$base-dev.$n"

# Older packs of the same version would be silently kept by NuGet; replace them.
rm -f "$feed"/RecursiveAI.OneShader."$version".nupkg "$feed"/RecursiveAI.OneShader.Build."$version".nupkg

dotnet pack "$root/src/OneShader/OneShader.csproj" -c Release -o "$feed" -p:OneShaderVersion="$version" -v quiet -clp:NoSummary
dotnet pack "$root/src/OneShader.Build/OneShader.Build.csproj" -c Release -o "$feed" -p:OneShaderVersion="$version" -v quiet -clp:NoSummary
echo "packed $version into $feed"
