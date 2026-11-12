#!/bin/bash
# Sets the version of the three packages: version.props, and the Clojure.MSBuild and
# Clojure.MSBuild.TestAdapter package references in the templates, the examples and the
# README. Usage: scripts/set-version.sh 0.1.0
set -euo pipefail

if [ $# -ne 1 ]; then
  echo "Usage: $0 <version>"
  exit 1
fi
VERSION="$1"
DIR="$(cd "$(dirname "$0")/.." && pwd)"
cd "$DIR"

sed -i.bak -E "s#<Version>[^<]+</Version>#<Version>$VERSION</Version>#" version.props
for f in README.md $(git ls-files '*.csproj' 'templates/**/*.md' 'examples/**/*.md'); do
  sed -i.bak -E \
    -e "s#(Include=\"Clojure\\.MSBuild(\\.TestAdapter)?\" Version=\")[^\"]+\"#\\1$VERSION\"#g" \
    "$f"
done
find . -name '*.bak' -not -path './.git/*' -delete
git grep -n "Clojure.MSBuild\(.TestAdapter\)\?\" Version=" -- '*.csproj' README.md | grep -v "$VERSION" || true
echo "Version set to $VERSION"
