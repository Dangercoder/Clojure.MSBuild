#!/bin/bash
# Runs the unit tests of the Clojure.MSBuild tooling (tests/Clojure.MSBuild.Tests), which
# builds with the targets in this repository rather than a package.
set -euo pipefail

DOTNET="${DOTNET:-dotnet}"
cd "$(dirname "$0")/../tests/Clojure.MSBuild.Tests"
"$DOTNET" build-server shutdown > /dev/null 2>&1 || true
"$DOTNET" msbuild /t:clj-test -restore -nologo -v:m
