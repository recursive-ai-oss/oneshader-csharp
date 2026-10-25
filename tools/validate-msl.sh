#!/usr/bin/env bash
# Compiles every MSL file in a directory with Apple's Metal compiler (metal -c) and packs each object into a metallib.
# Needs a Mac with Xcode and its Metal Toolchain (xcrun -sdk macosx metal --version must work).
#
#   tools/validate-msl.sh [directory-with-.metal-files]
#
# The files come from the compiler tests:
#   ONESHADER_MSL_OUT=<dir> dotnet test tests/OneShader.Tests --filter-method '*MslBackendTests*'
#
# Every file is compiled with the layout checks switched on (ONESHADER_CHECK_LAYOUT), so the
# static_asserts that compare each buffer struct's size and member offsets with the reflected
# layout are part of the check. Prints one PASS or FAIL line per file and exits non-zero if any failed.
#
# Environment:
#   ONESHADER_MSL_STD    language version flag value (default macos-metal2.4)
#   ONESHADER_MSL_WORK   scratch directory for objects and logs (default ~/oneshader/msl-validate)
#   ONESHADER_MSL_EXTRA  extra compiler flags, for example -ffast-math or -fno-fast-math
set -u

dir="${1:-.}"
std="${ONESHADER_MSL_STD:-macos-metal2.4}"
work="${ONESHADER_MSL_WORK:-$HOME/oneshader/msl-validate}"
extra="${ONESHADER_MSL_EXTRA:-}"

if ! xcrun -sdk macosx metal --version >/dev/null 2>&1; then
  echo "error: 'xcrun -sdk macosx metal' does not work here; install Xcode and its Metal Toolchain (xcodebuild -downloadComponent MetalToolchain)." >&2
  xcrun -sdk macosx metal --version >&2
  exit 2
fi

mkdir -p "$work/air" "$work/log"
rm -f "$work"/air/*.air "$work"/log/*.log
xcrun -sdk macosx metal --version | head -1

pass=0
fail=0
for f in "$dir"/*.metal; do
  [ -e "$f" ] || { echo "error: no .metal files in $dir" >&2; exit 2; }
  name="$(basename "$f" .metal)"
  # shellcheck disable=SC2086
  if xcrun -sdk macosx metal -std="$std" -DONESHADER_CHECK_LAYOUT -Werror=return-type $extra -c "$f" -o "$work/air/$name.air" >"$work/log/$name.log" 2>&1 &&
     xcrun -sdk macosx metallib "$work/air/$name.air" -o "$work/air/$name.metallib" >>"$work/log/$name.log" 2>&1; then
    echo "PASS $name"
    pass=$((pass + 1))
  else
    echo "FAIL $name"
    sed 's/^/    /' "$work/log/$name.log" | head -12
    fail=$((fail + 1))
  fi
done

echo "metal -c + metallib: $pass passed, $fail failed"
[ "$fail" -eq 0 ]
