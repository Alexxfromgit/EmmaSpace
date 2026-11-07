#!/usr/bin/env bash
# Builds Emma Space on macOS or Linux: the C++ engine (universal dylib on macOS), then the .NET solution.
#   ./build.sh            Release build
#   ./build.sh --test     ...and run tests
set -euo pipefail
cd "$(dirname "$0")"

config=Release
run_tests=false
for arg in "$@"; do
  case "$arg" in
    --debug) config=Debug ;;
    --test) run_tests=true ;;
  esac
done

case "$(uname)" in
  Darwin) preset=osx ;;
  Linux) preset=linux-x64 ;;
  *) preset="" ;;
esac
if [[ -n "$preset" ]]; then
  echo "==> Native engine ($preset)"
  cmake --preset "$preset"
  cmake --build --preset "$preset-release"
  $run_tests && ctest --preset "$preset"
else
  echo "==> No native preset for $(uname); the managed scanner will be used."
fi

echo "==> .NET ($config)"
dotnet build EmmaSpace.slnx -c "$config" -p:EmmaSkipNative=true
$run_tests && dotnet test --project tests/EmmaSpace.Core.Tests -c "$config" -p:EmmaSkipNative=true --no-build
echo "Done."
