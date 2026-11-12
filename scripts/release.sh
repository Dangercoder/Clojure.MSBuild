#!/bin/bash
set -euo pipefail

DOTNET="${DOTNET:-dotnet}"

if [ -z "${NUGET_API_KEY:-}" ]; then
  echo "Error: NUGET_API_KEY not set"
  echo "Usage: NUGET_API_KEY=your-key ./release.sh"
  exit 1
fi

DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$DIR/.."

# Unit tests of the tooling, then pack
"$DIR/test.sh"
"$DIR/pack.sh"

echo ""
echo "=== Publishing to nuget.org ==="

for pkg in packages/*.nupkg; do
  echo "Pushing $pkg..."
  "$DOTNET" nuget push "$pkg" --api-key "$NUGET_API_KEY" --source https://api.nuget.org/v3/index.json --skip-duplicate
done

echo ""
echo "Done! Packages published to nuget.org"
