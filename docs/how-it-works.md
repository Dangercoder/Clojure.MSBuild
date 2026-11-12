# How the build works

`dotnet build` runs the normal .NET SDK build plus one extra target, `ClojureBuild`:

1. `GenerateClojureEntryPoint` (before `CoreCompile`) writes two files into `obj/`:
   a tiny C# host, and the Clojure namespace `clojure.msbuild.entry`, which contains
   `(:gen-class :name MyApp :main true)` and a config map (main namespace, source
   directories, `ClojureGitDep` items). Its `-main` hands over to the packaged
   `clojure.msbuild.bootstrap` namespace, which sets `CLOJURE_LOAD_PATH`, pre-loads the
   assemblies next to the application, resolves git dependencies for tooling runs, and
   then either runs the `--clj-mode` tool or requires your main namespace and calls its
   `-main`.
2. The SDK compiles the C# host into `MyApp.dll` and copies it, the NuGet assemblies,
   `MyApp.deps.json` and `MyApp.runtimeconfig.json` to `bin/`. The host is a complete
   runnable application with all references, which is what makes step 3 possible.
3. `ClojureBuild` (after `Build`) AOT-compiles the Clojure code in two layers, using the
   host: `MyApp.dll --clj-mode build-libs` and `MyApp.dll --clj-mode build`.
   - The **library layer** (`obj/.../clj-lib/`) holds everything that is not project code:
     `clojure.core` and the standard library, NuGet Clojure libraries, git dependencies and
     the Clojure.MSBuild tooling. ClojureCLR ships all of these as source and compiles them
     at every process start, which costs about 12 seconds before the first line of your
     code runs, so the build compiles them once. The layer is rebuilt only when the
     dependencies (`project.assets.json`), the generated entry namespace, `deps.edn` or
     Clojure.MSBuild itself change, or after `dotnet clean`. It is filled by loading your
     main namespace and your test namespaces, so it also covers test-only libraries.
   - The **build layer** (`obj/.../clj-out/`) holds your own namespaces:
     `clojure.msbuild.entry`, the main namespace with everything it requires that is not
     already in the library layer, and `ClojureNamespacesToCompile`. It is incremental:
     when no Clojure source, the project file or the library layer changed, the step is
     skipped, and otherwise only the namespaces whose source changed are compiled again,
     together with the namespaces that require them (read from their `ns` forms, since
     macros, inlined functions, protocols and record types are compiled into the caller).
     Everything else loads from the previous build's assemblies. A dependency that is
     loaded from source here is moved to the library layer afterwards.
   Each namespace becomes a `<namespace>.cljr.dll`; `gen-class` emits `MyApp.exe`, an
   assembly named after the project with the entry point `MyApp.Main`. A compile error in
   project code fails the build; a library that cannot be precompiled is reported as a
   warning and loaded from source at runtime instead.
4. Both layers are copied to `bin/` and `MyApp.exe` is copied over `MyApp.dll`.
   From then on `dotnet run`, `dotnet MyApp.dll`, the `MyApp` apphost and `dotnet publish`
   start at the gen-class `Main` from precompiled code in a couple of seconds, and `.cljr`
   sources are not needed at runtime.

The part of the library layer that depends only on the Clojure and Clojure.MSBuild versions
(`clojure.core`, the standard library, the tooling) is shared between projects through
`~/.clojure-msbuild/lib-cache/`, so the first build of a new project only compiles its own
dependencies. Set `ClojureLibCacheDir` to move the cache or `ClojureLibCache` to `false` to
keep everything inside the project. The minimal API template's Dockerfile mounts the cache.

The library layer and the Clojure runtime (`Clojure.dll`) are also compiled to native code
with crossgen2 (ReadyToRun). ClojureCLR emits IL, and loading `clojure.core` alone makes the
runtime JIT-compile about 5000 methods, so without it every process (the build, the tests,
the REPL, the application) spends about two seconds in the JIT before it starts; with it the
runtime is up in about 0.6 s. The images still contain the IL, so they run anywhere, and the
native code is only used on the platform that built it. crossgen2 is downloaded from NuGet
(`microsoft.netcore.app.crossgen2.<rid>`, matching the target framework) the first time the
library layer is built; without it (offline) the layer stays IL and a warning says so. An
assembly crossgen2 cannot compile stays IL (crossgen2 11.0 crashes on `proxy` classes of
`TextWriter`, so `clojure.core-print`, `clojure.repl` and parts of `clojure.pprint` are
JIT-compiled). Set
`ClojureReadyToRun` to `false` to turn it off.

A build with no changes only copies the layers into `bin/` (the SDK copies the C# host there
on every build). `dotnet clean` removes both layers, and `dotnet publish` publishes the
compiled files, and the ReadyToRun `Clojure.dll`, instead of the C# host.

## Load path

Tooling runs (`--clj-mode`: the build, tests, the REPL) put the project's source directories,
the git dependencies and the Clojure.MSBuild namespaces on `CLOJURE_LOAD_PATH`. The compiled
application does not: it loads its namespaces from the assemblies next to it, and only
source directories copied next to it (`Content` items) are on its load path, for
`require :reload` from a REPL. A `src/` directory under the current directory is deliberately
left out, because `RT.load` prefers a source file newer than its assembly, so an application
started from such a directory could otherwise run that code instead of its own. With the
in-app nREPL on (`ClojureNReplInApp`), the project's sources are on the load path so that
edited files can be reloaded.

## Diagnostics

Compile errors are printed in MSBuild's canonical format,
`src/my_app/core.cljr(12,3): error CLJ0001: compiling my-app.core: Unable to resolve symbol: x`,
which `dotnet build` and IDEs show as errors at that location. The codes are `CLJ0001`
(compiling project code), `CLJ0002` (resolving git dependencies), `CLJ0003` (configuration)
and `CLJ0004` (a library namespace that could not be precompiled; a warning).

## The tooling protocol

`MyApp.dll --clj-mode <mode>` is the tooling protocol used by the MSBuild targets and the
test adapter: `repl`, `nrepl [port] [middleware]`, `script <file> [args]`,
`test [ns ns/name ...]`, `test-vars <ns/name ...>`, `test-watch [dirs]`,
`dev [nrepl-port] [args]`,
`build-libs <lib-dir> <main-ns> <project-dirs> <shared-dir> <crossgen2> <Clojure.dll>`,
`build <namespaces> <dir> <lib-dir> <project-dirs>`. `dotnet run -- --clj-mode test-watch`
works as well as the MSBuild targets.

The test adapter runs `test`/`test-vars` with `CLOJURE_MSBUILD_TEST_FORMAT=edn` and
`CLOJURE_MSBUILD_TEST_RESULTS=<file>`; the test runner then writes one
`CLJTEST-EDN<tab>{...}` line per test var to stdout and to that file.

Because the project assembly is replaced, the project cannot contain C# sources of its own
and `AssemblyName` must be a valid .NET type name (letters, digits, `_` and `.`; the
templates take care of that). Put C# in a class library and reference it, or set
`ClojureEntryPoint` to `csharp` to keep a generated C# `Main` as the entry point; Clojure
is still AOT-compiled and the tooling works the same way.

