# Clojure.MSBuild (Experimental)

MSBuild integration for Clojure CLR projects. Build, test, and run Clojure code using standard `dotnet` commands, with full REPL support.

## What It Does

- **Build** Clojure projects with `dotnet build`
- **Run** Clojure applications with `dotnet run`
- **Test** with `clojure.test` using `dotnet test`
- **REPL** support (socket REPL and nREPL via MSBuild targets)
- **C# Interop** - seamlessly use C# classes from Clojure
- **Automatic entry point** generation via source generators

## Quick Start

### Option 1: Using the Project Template (Recommended)

#### Install the template

```bash
# From NuGet (when published)
dotnet new install Clojure.MSBuild.Templates

# Or install from local source during development
dotnet new install /path/to/clojure-msbuild/templates/clojure-app
```

#### Create a new project

```bash
# Create a new Clojure CLR app with default settings
dotnet new clojure-app -n MyClojureApp
cd MyClojureApp

# Run your app
dotnet run

# Run tests
dotnet test
```

#### Template Options

```bash
# Create with .NET 8.0 instead of 9.0
dotnet new clojure-app -n MyApp --framework net8.0

# Use a specific Clojure version
dotnet new clojure-app -n MyApp --clojureVersion 1.12.0

# Create without test setup
dotnet new clojure-app -n MyApp --enableTests false

# See all options
dotnet new clojure-app -h
```

#### What's Included

The template creates a complete project structure:
- **src/main.cljr** - Main application entry point with JSON and .NET interop examples
- **test/main_test.cljr** - Sample test file using clojure.test
- **.csproj** - Pre-configured with all required packages
- **README.md** - Project-specific documentation
- **.gitignore** - Common build artifacts excluded

### Option 2: Manual Setup

#### 1. Create a new .NET project

```bash
dotnet new console -n MyClojureApp
cd MyClojureApp
```

#### 2. Update your .csproj file

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
    <Content Include="src/**/*.cljr">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
  </ItemGroup>

</Project>
```

#### 3. Create your Clojure code

Create `src/main.cljr`:

```clojure
(ns main)

(defn -main 
  [& args]
  (println "Hello from Clojure CLR!")
  (when (seq args)
    (println "Arguments:" (vec args))))
```

#### 4. Run your application

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

#### Using dotnet test (Recommended)

Clojure.MSBuild includes a VSTest adapter that integrates seamlessly with `dotnet test`:

```bash
# Run all tests
dotnet test

# Run with detailed output
dotnet test --logger:"console;verbosity=detailed"

# Run specific test files
dotnet test --filter "FullyQualifiedName~main_test"
```

The test adapter automatically discovers and runs all Clojure tests in `*_test.clj` files that use `clojure.test`.

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

## Creating Libraries

You can create Clojure libraries that can be referenced by other .NET projects:

### Library Project Setup

1. Create a new project with `OutputType` set to `Library`:
```xml
<PropertyGroup>
  <OutputType>Library</OutputType>
  <TargetFramework>net9.0</TargetFramework>
  <!-- No ClojureMainNamespace for libraries -->
</PropertyGroup>
```

2. Add your Clojure namespaces in the `src/` directory
3. Build with `dotnet build` to create a DLL

### Using Libraries

Reference the library from another project:
```xml
<ItemGroup>
  <ProjectReference Include="../MyLibrary/MyLibrary.csproj" />
</ItemGroup>
```

Or as a NuGet package:
```xml
<ItemGroup>
  <PackageReference Include="MyClojureLibrary" Version="1.0.0" />
</ItemGroup>
```

### Important Notes
- Library projects don't need `ClojureMainNamespace` since they don't have an entry point
- The source generator won't create a Program.cs for library projects
- Consumer projects must ensure Clojure runtime is initialized before using library code
- Clojure source files (*.cljr) are included in the output directory for runtime loading

## Example Projects

See the `/examples` folder for working examples:
- `/examples/simple` - Basic executable application demonstrating:
  - Integration with `dotnet test`
  - C# interop capabilities
  - JSON handling with clojure.data.json
- `/examples/simple_library` - Library project demonstrating:
  - Creating reusable Clojure libraries
  - Math and string utility functions
  - Library testing with clojure.test

## Testing Support

### Writing Tests

Create test files with the `_test.clj` suffix in your project:

```clojure
(ns my-namespace-test
  (:require [clojure.test :refer :all]
            [my-namespace :refer [my-function]]))

(deftest test-my-function
  (testing "My function works correctly"
    (is (= expected (my-function input)))))
```

### C# Interop in Tests

You can test C# classes from your Clojure tests:

```clojure
(ns csharp-interop-test
  (:require [clojure.test :refer :all])
  (:import [MyApp.Services MyService]))

(deftest test-csharp-service
  (testing "C# service integration"
    (let [service (MyService.)]
      (is (= "expected" (.ProcessData service "input"))))))
```

## Requirements

- .NET 9.0 SDK or later
- Clojure CLR 1.12.2 or later
- For testing: Microsoft.NET.Test.Sdk (automatically included)

## Project Templates

### Installing Templates

The project includes a `dotnet new` template for quick project scaffolding:

```bash
# Install from the local template directory
dotnet new install ./templates/clojure-app

# Uninstall if needed
dotnet new uninstall clojure-app
```

### Creating Projects

Once installed, create new Clojure CLR projects easily:

```bash
# Basic project
dotnet new clojure-app -n MyProject

# With options
dotnet new clojure-app -n MyProject \
  --framework net8.0 \
  --clojureVersion 1.12.0 \
  --enableTests true
```

### Template Development

To package the template for distribution:

```bash
# Package as NuGet (when nuget CLI is available)
cd templates
nuget pack Clojure.MSBuild.Templates.nuspec

# Or create a template package manually
cd templates/clojure-app
dotnet pack
```

## License

MIT
