#!/bin/bash
# Lints the Clojure sources with clj-kondo (https://github.com/clj-kondo/clj-kondo), using
# .clj-kondo/config.edn. Install clj-kondo first, e.g. `brew install borkdude/brew/clj-kondo`
# or a binary from its releases page.
set -euo pipefail

cd "$(dirname "$0")/.."
if ! command -v clj-kondo > /dev/null; then
  echo "clj-kondo is not installed: https://github.com/clj-kondo/clj-kondo/blob/master/doc/install.md"
  exit 1
fi
clj-kondo --lint src tests/Clojure.MSBuild.Tests/test templates/content examples --fail-level error
