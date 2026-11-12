#!/bin/bash
set -euo pipefail

DOTNET="${DOTNET:-dotnet}"
cd "$(dirname "$0")/.."

rm -rf packages
mkdir -p packages

# Long-lived MSBuild/compiler server processes cache imported .targets files,
# which makes builds pick up a stale copy of the package after re-packing.
"$DOTNET" build-server shutdown > /dev/null 2>&1 || true

echo "=== Packing Clojure.MSBuild ==="
"$DOTNET" pack Clojure.MSBuild.csproj -c Release -o packages/

echo ""
echo "=== Building TestAdapter (AOT compile) ==="
cd src/TestAdapter
rm -rf bin obj
"$DOTNET" build -c Release
cd ../..

echo ""
echo "=== Packing Clojure.MSBuild.TestAdapter ==="
"$DOTNET" pack src/TestAdapter/Clojure.MSBuild.TestAdapter.csproj -c Release -o packages/

echo ""
echo "=== Packing Clojure.MSBuild.Templates ==="
"$DOTNET" pack templates/Clojure.MSBuild.Templates.csproj -c Release -o packages/

echo ""
echo "=== Packages ==="
ls -la packages/*.nupkg
