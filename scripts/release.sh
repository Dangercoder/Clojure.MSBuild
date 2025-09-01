#!/bin/bash
set -e

if [ -z "$NUGET_API_KEY" ]; then
  echo "Error: NUGET_API_KEY not set"
  echo "Usage: NUGET_API_KEY=your-key ./release.sh"
  exit 1
fi

# Pack first
DIR="$(cd "$(dirname "$0")" && pwd)"
"$DIR/pack.sh"

echo ""
echo "=== Publishing to nuget.org ==="

for pkg in packages/*.nupkg; do
  echo "Pushing $pkg..."
  ~/.dotnet/dotnet nuget push "$pkg" --api-key "$NUGET_API_KEY" --source https://api.nuget.org/v3/index.json --skip-duplicate
done

echo ""
echo "Done! Packages published to nuget.org"
