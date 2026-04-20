# Clojure.MSBuild

[![Clojure.MSBuild](https://img.shields.io/nuget/v/Clojure.MSBuild.svg?label=Clojure.MSBuild)](https://www.nuget.org/packages/Clojure.MSBuild/)
[![Clojure.MSBuild.TestAdapter](https://img.shields.io/nuget/v/Clojure.MSBuild.TestAdapter.svg?label=TestAdapter)](https://www.nuget.org/packages/Clojure.MSBuild.TestAdapter/)

MSBuild integration for ClojureCLR. Build, test, and run Clojure on .NET with standard `dotnet` commands.
No non-dotnet tools needed.

## Requirements

- .NET 11.0 SDK (preview) or later
- Clojure CLR 1.12.3-alpha5 or later

## Quick Start

### 1. Create a project

```bash
dotnet new console -n MyApp
cd MyApp
```

### 2. Add packages

```bash
dotnet add package Clojure.MSBuild
dotnet add package Clojure --version 1.12.3-alpha5
```

### 3. Update your .csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net11.0</TargetFramework>
    <ClojureMainNamespace>app</ClojureMainNamespace>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Clojure.MSBuild" Version="0.0.4" />
    <PackageReference Include="Clojure" Version="1.12.3-alpha5" />
  </ItemGroup>

  <ItemGroup>
    <Content Include="src/**/*.cljr" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

### 4. Write your app

Create `src/app.cljr`:

```clojure
(ns app)

(defn -main [& args]
  (println "Hello from ClojureCLR!"))
```

### 5. Run it

```bash
dotnet run
```

## Clojure Git Dependencies

Add Clojure libraries from git directly in your `.csproj` — no `deps.edn` needed:

```xml
<ItemGroup>
  <!-- .NET deps from NuGet -->
  <PackageReference Include="Clojure" Version="1.12.3-alpha5" />
  <PackageReference Include="Microsoft.Data.Sqlite" Version="9.0.3" />

  <!-- Clojure deps from git -->
  <ClojureGitDep Include="io.github.clojure/clr.data.json" Tag="v2.5.1" Sha="f84cb88" />
  <ClojureGitDep Include="com.github.seancorfield/honeysql" Tag="v2.7.1368" Sha="b8332fc" />
</ItemGroup>
```

Repos are cloned to `~/.gitlibs` (cached, compatible with `cljr`). Supports `io.github`, `com.github`, `io.gitlab`, and `ht.sr` prefixes for auto URL resolution. You can also specify a full URL:

```xml
<ClojureGitDep Include="my/lib" Tag="v1.0" Sha="abc123" Url="https://example.com/repo.git" />
```

A `deps.edn` file is also supported if you prefer the Clojure convention. Both sources merge.

## Testing

Add the test adapter package:

```bash
dotnet add package Clojure.MSBuild.TestAdapter
dotnet add package Microsoft.NET.Test.Sdk
```

Add test content to your .csproj:

```xml
<Content Include="test/**/*.cljr" CopyToOutputDirectory="PreserveNewest" />
```

Create test files with the `_test.cljr` suffix in a `test/` directory:

```clojure
(ns app-test
  (:require [clojure.test :refer [deftest is testing]]))

(deftest test-addition
  (testing "math works"
    (is (= 4 (+ 2 2)))))
```

Run tests:

```bash
dotnet test
```

## REPL

```bash
dotnet msbuild /t:clj-repl
```

## nREPL

```bash
dotnet add package clojure.tools.nrepl --prerelease
dotnet msbuild /t:clj-nrepl
```

Connects on port 1667. Use Calva, CIDER, or any nREPL client.

## Run a Script

```bash
dotnet msbuild /t:clj-run -p:File=path/to/script.clj
```

## AOT Compilation

Compile namespaces to .NET DLLs during build:

```xml
<PropertyGroup>
  <ClojureCompileOnBuild>true</ClojureCompileOnBuild>
  <ClojureNamespacesToCompile>my.lib;my.utils</ClojureNamespacesToCompile>
</PropertyGroup>
```

## Configuration

| Property | Description | Default |
|----------|-------------|---------|
| `ClojureMainNamespace` | Namespace with `-main` function | _(required)_ |
| `ClojureSourceDir` | Source directory | `src` |
| `ClojureExtraSourceDirs` | Additional source dirs (pipe-separated) | |
| `ClojureCompileOnBuild` | AOT compile on build | `false` |
| `ClojureNamespacesToCompile` | Namespaces to compile (semicolon-separated) | |

## Examples

- [`examples/minimal-api`](examples/minimal-api) — ASP.NET web API with HoneySQL, async ADO.NET, integration tests
- [`examples/csharp-interop`](examples/csharp-interop) — Bidirectional C#/Clojure interop in one project

## License

MIT
