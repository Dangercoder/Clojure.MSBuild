# Clojure.MSBuild (Experimental)

MSBuild integration for Clojure CLR projects with automatic entry point generation.

## Quick Start

### 1. Create a new .NET project

```bash
dotnet new console -n MyClojureApp
cd MyClojureApp
```

### 2. Update your .csproj file

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <ClojureMainNamespace>main</ClojureMainNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Clojure.MSBuild" Version="0.3.0" />
    <PackageReference Include="Clojure" Version="1.12.2" />
  </ItemGroup>

  <ItemGroup>
    <Content Include="src/**/*.clj">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
  </ItemGroup>

</Project>
```

### 3. Create your Clojure code

Create `src/main.clj`:

```clojure
(ns main)

(defn -main 
  [& args]
  (println "Hello from Clojure CLR!")
  (when (seq args)
    (println "Arguments:" (vec args))))
```

### 4. Run your application

```bash
# Using dotnet run
dotnet run
dotnet run -- arg1 arg2

# Or build and run the DLL directly
dotnet build
dotnet bin/Debug/net9.0/MyClojureApp.dll
dotnet bin/Debug/net9.0/MyClojureApp.dll arg1 arg2
```

## How It Works

Clojure.MSBuild uses a source generator to automatically create the .NET entry point for your Clojure application. You just need to:

1. Set `ClojureMainNamespace` to your main namespace
2. Define a `-main` function in that namespace
3. Build and run like any .NET application

No manual Program.cs needed - the source generator handles everything behind the scenes.

## Development Tools

### REPL Support

Start an interactive REPL with all project dependencies:

```bash
dotnet msbuild /t:clj-repl
```

### nREPL Server

Start an nREPL server for editor integration (port 1667):

```bash
# First, add the nREPL package to your project
dotnet add package clojure.tools.nrepl --version 0.1.0-alpha1

# Then start the nREPL server
dotnet msbuild /t:clj-nrepl
```

Connect from your editor:
- VS Code with Calva: Connect to Generic -> localhost:1667
- Emacs/Vim: Connect to localhost:1667

### Run Scripts

Run a Clojure script file:

```bash
dotnet msbuild /t:clj-run -p:File=src/script.clj
```

### Run Tests

Run all Clojure tests in the project:

```bash
dotnet msbuild /t:clj-test
```

### Build/Compile Namespaces

Compile Clojure namespaces to .NET DLLs:

```bash
dotnet msbuild /t:clj-build
```

## Configuration Options

| Property | Description | Default |
|----------|-------------|---------|
| `ClojureMainNamespace` | Namespace containing your -main function | Required for executables |
| `ClojureAutoLoadAssemblies` | Auto-load NuGet assemblies | true |
| `ClojureCompileOnBuild` | Compile namespaces during build | false |
| `ClojureNamespacesToCompile` | Namespaces to compile (semicolon-separated) | main namespace |

## Example Project

See the `/examples/simple` folder for a minimal working example.

## Requirements

- .NET 9.0 SDK or later
- Clojure CLR 1.12.2 or later

## License

MIT
