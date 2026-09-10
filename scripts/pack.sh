#!/bin/bash
set -e

rm -rf packages
mkdir -p packages

# Long-lived MSBuild/compiler server processes cache imported .targets files,
# which makes builds pick up a stale copy of the package after re-packing.
~/.dotnet/dotnet build-server shutdown > /dev/null 2>&1 || true

echo "=== Packing Clojure.MSBuild ==="
~/.dotnet/dotnet pack Clojure.MSBuild.csproj -c Release -o packages/

echo ""
echo "=== Building TestAdapter (AOT compile) ==="
cd src/TestAdapter
rm -rf bin obj
~/.dotnet/dotnet build -c Release
cd ../..

echo ""
echo "=== Packing Clojure.MSBuild.TestAdapter ==="
~/.dotnet/dotnet pack src/TestAdapter/Clojure.MSBuild.TestAdapter.csproj -c Release -o packages/

echo ""
echo "=== Packages ==="
ls -la packages/*.nupkg
