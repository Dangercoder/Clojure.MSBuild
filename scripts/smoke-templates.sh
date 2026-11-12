#!/bin/bash
# Packs the templates package, installs it, generates a project from each template
# with --root-namespace, then builds, tests and runs them against freshly packed
# Clojure.MSBuild packages. Everything lives in .smoke/ inside the repo.
set -euo pipefail

DIR="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET="${DOTNET:-dotnet}"
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
echo "--- rebuild time (console, one namespace changed)"
( cd My-App && touch src/my_app/core.cljr && time "$DOTNET" build -nologo -v q && "$DOTNET" run --no-build | grep -q "Hello from ClojureCLR" )

echo "--- a compile error is reported in MSBuild's format, at its line"
( cd My-App
  cp src/my_app/core.cljr core.cljr.orig
  printf '\n(defn broken [] (no-such-function))\n' >> src/my_app/core.cljr
  line="$(wc -l < src/my_app/core.cljr)"
  if out="$("$DOTNET" build -nologo -v q 2>&1)"; then echo "the build should have failed"; exit 1; fi
  echo "$out" | grep -E "core.cljr\($line,[0-9]+\): error CLJ0001: compiling my-app.core: .*no-such-function"
  mv core.cljr.orig src/my_app/core.cljr
  "$DOTNET" build -nologo -v q > /dev/null )

echo "--- the compiled application does not load sources from the current directory"
( cd My-App
  "$DOTNET" build -nologo -v q > /dev/null
  mkdir -p elsewhere/src/my_app
  sed 's/Hello from ClojureCLR!/Hello from a stray source file/' src/my_app/core.cljr > elsewhere/src/my_app/core.cljr
  touch elsewhere/src/my_app/core.cljr
  out="$(cd elsewhere && "$DOTNET" ../bin/Debug/net11.0/My_App.dll)"
  echo "$out"
  [[ "$out" == *"Hello from ClojureCLR!"* ]]
  rm -rf elsewhere )

echo "--- publish modes Clojure cannot support fail early"
( cd My-App
  if out="$("$DOTNET" publish -nologo -v q -p:PublishSingleFile=true -r linux-x64 2>&1)"; then echo "publish should have failed"; exit 1; fi
  echo "$out" | grep -q "PublishSingleFile is not supported" )

echo "--- clj-run passes arguments to the script"
( cd My-App
  printf '(println "args:" *command-line-args*)\n' > script.cljr
  "$DOTNET" msbuild /t:clj-run -p:ClojureScript=script.cljr '-p:ClojureScriptArgs=a b' -nologo -v m | grep -q 'args: (a b)' )

echo "=== clojure-clr-minimal-api"
"$DOTNET" new clojure-clr-minimal-api -n orders --root-namespace constructly.se > /dev/null
( cd orders
  test -f src/constructly/se/server.cljr
  test -f src/constructly/se/todo/model.cljr
  test -f test/constructly/se/todo_test.cljr
  grep -q "constructly.se.server" orders.csproj
  "$DOTNET" build -nologo -v q
  "$DOTNET" test -nologo -v q
  "$DOTNET" msbuild /t:clj-test -nologo -v q
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
  [[ "$created" == *'"title":"smoke"'* ]]
  echo "--- the in-app nREPL server (Debug)"
  grep "nREPL server started" run.log
  test ! -f .nrepl-port   # removed when the application stops
  echo "--- dotnet test --filter"
  "$DOTNET" test --no-build -nologo -v q --filter "Namespace~todo-test" | grep -E "Passed!|Failed!" )

echo ""
echo "Templates OK"
