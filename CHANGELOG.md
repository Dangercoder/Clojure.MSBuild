# Changelog

## 0.0.11

### .NET 11

- Built and tested with the .NET 11.0.100 SDK (GA). The templates and examples use the .NET 11
  packages (Microsoft.Data.Sqlite, Microsoft.AspNetCore.TestHost 11.0.0), Microsoft.NET.Test.Sdk
  and Microsoft.TestPlatform.ObjectModel 18.12, OpenTelemetry 1.21 and Orleans 10.4.
- Clojure.MSBuild turns on `EnablePreviewFeatures` (ClojureCLR's runtime for net11.0 needs it),
  so projects no longer set it themselves.

### Build

- Compile errors are reported in MSBuild's format at their source line
  (`src/app.cljr(4,3): error CLJ0001: ...`), so `dotnet build` and IDEs list them as errors.
- `PublishSingleFile`, `PublishTrimmed` and `PublishAot` fail the build early with an
  explanation instead of producing an application that cannot start.
- A build that has no `DOTNET_HOST_PATH` (e.g. started by Visual Studio's MSBuild) runs the
  tooling with the SDK's own `dotnet` host.
- `ClojureSource` and `ClojureCompile` items, as alternatives to `ClojureExtraSourceDirs` and
  `ClojureNamespacesToCompile`; `ClojureExtraSourceDirs` also accepts `;`.
- Multi-targeted projects: the `clj-*` targets run for the first framework, or
  `ClojureTargetFramework`.
- `ClojureAutoLoadAssemblies=false` stops the application from loading every assembly next to
  it at startup. The unused `ClojureReplEnabled` property is gone.

### Runtime

- A compiled application no longer puts the source directory under the current directory
  (or the build's generated and package directories) on its load path. Before, an
  application started from a directory with a `src/` folder could load those sources instead
  of its compiled code.

### Dependencies

- Git dependencies work in paths with spaces (e.g. a Windows user profile).
- Dependencies of dependencies are resolved; `:deps/root` (`Root` metadata), `:local/root`
  (`LocalRoot` metadata) and `ClojureDepsAliases` are supported; Maven coordinates are
  skipped with a warning.
- With `Tag` and `Sha`, the tag must point at the commit; the SHA is what is checked out.
- A dependency that cannot be resolved fails the build (`error CLJ0002`) instead of a missing
  namespace later.
- Builds that share `~/.gitlibs` no longer clone or check out the same repository at once.

### Tests

- Test discovery uses the Clojure reader: tests in `comment` forms are not listed, `t/deftest`
  is found, and line numbers are computed once per file.
- `ClojureTestDir` and `ClojureTestSuffix`.
- Cancelling a test run stops the test process.
- "Debug Test" starts the test process under the IDE's debugger.
- A timeout from the run settings (`<ClojureTest><TimeoutSeconds>`) or
  `CLOJURE_MSBUILD_TEST_TIMEOUT`.
- Results are read from a file the test runner writes, so a test that prints without a final
  newline no longer hides its result.

### REPL and development

- `ClojureNReplInApp`: the application starts an nREPL server next to its `-main` (the minimal
  API template does in Debug).
- `ClojureNReplMiddleware` adds nREPL middleware.
- `clj-dev` / `--clj-mode dev` runs the application and reloads changed namespaces into it.
- `clj-run` takes `ClojureScript` and `ClojureScriptArgs` (`*command-line-args*`).

### Repository

- Unit tests of the tooling (`tests/Clojure.MSBuild.Tests`, `scripts/test.sh`).
- One version for the three packages in `version.props`, set with `scripts/set-version.sh`.
- The scripts use `dotnet` from the `PATH` (or `DOTNET`); `scripts/lint.sh` runs clj-kondo;
  `scripts/release.sh` runs the unit tests first.
- Docs: [how the build works](docs/how-it-works.md), [troubleshooting](docs/troubleshooting.md).

## 0.0.10

- Incremental builds, a ReadyToRun runtime, a richer test runner and a working nREPL.
