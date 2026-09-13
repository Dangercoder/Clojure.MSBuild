#!/bin/bash
# Packs the templates package, installs it, generates a project from each template
# with --root-namespace, then builds, tests and runs them against freshly packed
# Clojure.MSBuild packages. Everything lives in .smoke/ inside the repo.
set -euo pipefail

DIR="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
SMOKE="$DIR/.smoke"
PKG="$SMOKE/pkg"

rm -rf "$SMOKE"
mkdir -p "$PKG"

echo "=== Packing Clojure.MSBuild, Clojure.MSBuild.TestAdapter and Clojure.MSBuild.Templates into $PKG"
"$DOTNET" build-server shutdown > /dev/null 2>&1 || true
"$DOTNET" pack "$DIR/Clojure.MSBuild.csproj" -c Release -o "$PKG" -nologo -v q
( cd "$DIR/src/TestAdapter" && rm -rf bin obj && "$DOTNET" build -c Release -nologo -v q && "$DOTNET" pack -c Release -o "$PKG" --no-build -nologo -v q )
"$DOTNET" pack "$DIR/templates/Clojure.MSBuild.Templates.csproj" -c Release -o "$PKG" -nologo -v q
NUPKG="$(ls "$PKG"/Clojure.MSBuild.Templates.*.nupkg)"
VERSION="$(basename "$NUPKG" .nupkg | sed 's/Clojure.MSBuild.Templates.//')"

# Generated projects restore Clojure.MSBuild from this feed (fresh copy, not the NuGet cache).
rm -rf "$HOME/.nuget/packages/clojure.msbuild/$VERSION" "$HOME/.nuget/packages/clojure.msbuild.testadapter/$VERSION"
cat > "$SMOKE/nuget.config" <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="smoke" value="$PKG" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
XML

echo "=== Installing $NUPKG"
"$DOTNET" new uninstall Clojure.MSBuild.Templates > /dev/null 2>&1 || true
"$DOTNET" new install "$NUPKG" > /dev/null

cd "$SMOKE"

echo "=== clojure-clr-console"
"$DOTNET" new clojure-clr-console -n hello-clj --root-namespace acme.tools > /dev/null
( cd hello-clj
  "$DOTNET" build -nologo -v q
  test -f src/acme/tools/core.cljr
  out="$("$DOTNET" run --no-build -- one two)"
  echo "$out"
  [[ "$out" == *"Hello from ClojureCLR!"*"one"* ]] )

echo "=== clojure-clr-console with the default root namespace"
"$DOTNET" new clojure-clr-console -n My-App > /dev/null
test -f My-App/src/my_app/core.cljr
grep -q "^(ns my-app.core" My-App/src/my_app/core.cljr
grep -q "<AssemblyName>My_App</AssemblyName>" My-App/My-App.csproj
( cd My-App && "$DOTNET" build -nologo -v q && "$DOTNET" run --no-build | grep -q "Hello from ClojureCLR" )
echo "--- rebuild time (console, no changes)"
( cd My-App && time "$DOTNET" build -nologo -v q )

echo "=== clojure-clr-minimal-api"
"$DOTNET" new clojure-clr-minimal-api -n orders --root-namespace constructly.se > /dev/null
( cd orders
  test -f src/constructly/se/server.cljr
  test -f src/constructly/se/todo/model.cljr
  test -f test/constructly/se/todo_test.cljr
  grep -q "constructly.se.server" orders.csproj
  "$DOTNET" build -nologo -v q
  "$DOTNET" test -nologo -v q
  "$DOTNET" run --no-build -- --urls http://localhost:5981 > run.log 2>&1 &
  pid=$!
  for _ in $(seq 1 60); do
    sleep 2
    grep -q "Now listening" run.log && break
  done
  body="$(curl -s -m 10 http://localhost:5981/health)"
  created="$(curl -s -m 10 -X POST http://localhost:5981/todos -H 'content-type: application/json' -d '{"title":"smoke"}')"
  kill "$pid" 2>/dev/null || true
  wait "$pid" 2>/dev/null || true
  echo "health: $body"
  echo "created: $created"
  [[ "$body" == '{"status":"ok"}' ]]
  [[ "$created" == *'"title":"smoke"'* ]] )

echo ""
echo "Templates OK"
