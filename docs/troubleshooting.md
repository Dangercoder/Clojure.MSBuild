# Troubleshooting

### The build uses an old version of a library, or behaves as if a change was not made

The library layer (`obj/<config>/<tfm>/clj-lib/`) is rebuilt when the dependencies change,
and the precompiled Clojure runtime is shared between projects through
`~/.clojure-msbuild/lib-cache/`. If either gets into a bad state (for example after an
interrupted build), remove them:

```bash
dotnet clean
rm -rf ~/.clojure-msbuild/lib-cache
dotnet build
```

The next build compiles the runtime again (about 20 seconds) and stores it in the cache.
`-p:ClojureLibCache=false` keeps everything inside the project.

### `warning: crossgen2 was not found`

The library layer is compiled to native code with crossgen2, which is downloaded from NuGet
the first time (`microsoft.netcore.app.crossgen2.<rid>`). Offline, or behind a feed that does
not have it, the layer stays IL: everything works, processes just start about two seconds
slower. It is tried again the next time the layer is rebuilt. `-p:ClojureReadyToRun=false`
turns it off.

### `error CLJ0003: Clojure.MSBuild supports Clojure 1.13.0-alpha8 only`

This release of Clojure.MSBuild is built for one ClojureCLR version. Reference exactly that
version of the `Clojure` package:

```xml
<PackageReference Include="Clojure" Version="1.13.0-alpha8" />
```

### `C# sources (...) cannot be compiled into a Clojure project`

With the default `ClojureEntryPoint=gen-class`, the project assembly is replaced by the
compiled Clojure entry point, so a C# file in the project would be lost. Move the C# into a
class library and reference it, or set `<ClojureEntryPoint>csharp</ClojureEntryPoint>`.

### `AssemblyName '...' is not a valid .NET type name`

In gen-class mode the assembly name is the name of the generated entry class. Use letters,
digits, `_` and `.` (`<AssemblyName>My_App</AssemblyName>`), or `ClojureEntryPoint=csharp`.

### `error CLJ0002: could not resolve Clojure dependencies`

A git dependency could not be checked out: the message names the library and the reason (an
unknown SHA or tag, a tag that points at another commit than the SHA, no network for a
repository that is not in `~/.gitlibs` yet, git not on the `PATH`). Repositories that are
already cached are used without the network.

### `Could not locate my/ns ...` when the application runs

The compiled application contains the main namespace and everything it requires. A
namespace the application only loads by name at runtime (`requiring-resolve`, a plugin
list) has to be listed in `ClojureNamespacesToCompile` or `ClojureCompile` items.

### Types from a NuGet package are not found

Clojure.MSBuild loads every assembly next to the application at startup. With
`ClojureAutoLoadAssemblies=false`, load them yourself with `(assembly-load "Name")` before
the namespace that imports from them is loaded. `CopyLocalLockFileAssemblies` must be `true`
so that NuGet assemblies are copied next to the application.

### `PublishSingleFile` / `PublishTrimmed` / `PublishAot` is not supported

Clojure finds compiled namespaces as separate assemblies next to the application and resolves
types by name at runtime, which these publish modes cannot follow. Publish framework-dependent
or self-contained (`dotnet publish -r linux-x64 --self-contained`).

### Tests are not discovered

The test adapter needs `Clojure.MSBuild.TestAdapter` and `Microsoft.NET.Test.Sdk`, test
files named `*_test.cljr` (`.cljc`, `.clj`) under `test/` (see `ClojureTestDir` and
`ClojureTestSuffix`), and a build after adding the package (it writes
`<assembly>.clojure.edn` next to the test assembly). `dotnet msbuild /t:clj-test` runs the
tests without VSTest and shows load errors directly.

### The build hangs at "Waiting for another build to finish with ..."

Another build is cloning or checking out the same git repository into `~/.gitlibs`. If no
other build is running, a lock file was left by a process that was killed; delete the
`.lock` file named in the message.

### Builds started by Visual Studio fail to run the Clojure tooling

The tooling runs with the `dotnet` host of the SDK that builds the project. A build that is
not started by the dotnet CLI (and so has no `DOTNET_HOST_PATH`) uses the SDK's own host, then
`dotnet` on the `PATH`; make sure the .NET 11 SDK is installed there.
