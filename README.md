# Clojure.MSBuild

MSBuild integration for Clojure CLR projects, enabling seamless development with .NET tooling and full NuGet package support.

## Features

- 🚀 **Run Clojure REPL** with all NuGet dependencies automatically loaded
- 📦 **NuGet Package Integration** - Use any .NET package from Clojure without manual assembly loading  
- 🔧 **MSBuild Targets** for common Clojure development tasks
- 🏗️ **AOT Compilation** support (experimental)
- 🧪 **Test Runner** integration
- 📜 **File Execution** with full dependency context

## Installation

Add to your `.csproj`:

```xml
<PackageReference Include="Clojure.MSBuild" Version="0.1.4" />
```

## Usage

After adding the package, you get these MSBuild targets:

### Start a REPL
```bash
dotnet msbuild -t:ClojureRepl
```

### Start nREPL server
```bash
dotnet msbuild -t:ClojureNRepl
```

### Run a Clojure file
```bash
dotnet msbuild -t:ClojureRun -p:File=my-file.clj
```

### Run tests
```bash
dotnet msbuild -t:ClojureTest
```

### AOT Compilation (Experimental)
```bash
dotnet msbuild -t:ClojureCompile -p:ClojureNamespacesToCompile=my.namespace
```

## Complete Project Setup

### 1. Create a new project folder and `.csproj` file:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <!-- Important: Ensures all dependencies are copied to output -->
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>

  <ItemGroup>
    <!-- Clojure.MSBuild provides the tooling -->
    <PackageReference Include="Clojure.MSBuild" Version="0.1.4" />
    
    <!-- Clojure runtime -->
    <PackageReference Include="Clojure" Version="1.12.2" />
    
    <!-- Add any NuGet packages you want to use from Clojure -->
    <PackageReference Include="Npgsql" Version="8.0.6" />
    <PackageReference Include="clojure.data.json" Version="2.4.1" />
    <PackageReference Include="Serilog" Version="4.1.0" />
  </ItemGroup>

</Project>
```

### 2. Create a minimal `Program.cs` (required by .NET but not used):

```csharp
public class Program 
{
    public static void Main() 
    {
        System.Console.WriteLine("Use MSBuild targets to run Clojure code:");
        System.Console.WriteLine("  dotnet msbuild -t:ClojureRepl");
    }
}
```

### 3. Create your Clojure code in `src/` folder:

**src/my_app/core.clj:**
```clojure
(ns my-app.core
  (:require [clojure.data.json :as json])
  (:import [Npgsql NpgsqlConnection]
           [Serilog Log LoggerConfiguration]))

(defn setup-logger []
  (let [logger (-> (LoggerConfiguration.)
                   (.WriteTo.Console)
                   (.CreateLogger))]
    (Log/set_Logger logger)
    logger))

(defn query-db [conn-string]
  (with-open [conn (NpgsqlConnection. conn-string)]
    (.Open conn)
    ;; Your database code here
    ))

(defn -main [& args]
  (setup-logger)
  (Log/Information "Application started")
  (println (json/write-str {:status "ready" :args args})))
```

### 4. Build and run:

```bash
# Build the project
dotnet build

# Start a REPL with all dependencies loaded
dotnet msbuild -t:ClojureRepl

# Or run a file
dotnet msbuild -t:ClojureRun -p:File=src/my_app/core.clj
```

## How it works

Clojure.MSBuild solves a fundamental problem in Clojure CLR: NuGet packages aren't automatically available to Clojure code.

**The Problem:** When you add a NuGet package to your project, Clojure's `RT.classForName` can't find the types because .NET doesn't automatically load assemblies from disk (unlike the JVM's ClassLoader).

**The Solution:** Clojure.MSBuild pre-loads all package assemblies from your project's `deps.json` file before initializing Clojure, making them immediately available for `import` statements.

This means you can:
- Use any NuGet package without manual assembly loading
- Import types naturally: `(:import [Npgsql NpgsqlConnection])`
- Leverage the entire .NET ecosystem from Clojure

## Configuration Options

You can configure Clojure.MSBuild behavior in your `.csproj`:

```xml
<PropertyGroup>
  <!-- Enable AOT compilation during build -->
  <ClojureCompileOnBuild>true</ClojureCompileOnBuild>
  
  <!-- Set compilation output path -->
  <ClojureCompilePath>$(OutputPath)compiled</ClojureCompilePath>
  
  <!-- Specify namespaces to compile (semicolon-separated) -->
  <ClojureNamespacesToCompile>my.app;my.lib</ClojureNamespacesToCompile>
</PropertyGroup>
```

## Troubleshooting

**Issue:** Types from NuGet packages not found
- **Solution:** Ensure `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>` is set in your `.csproj`

**Issue:** REPL doesn't start
- **Solution:** Run `dotnet build` first to ensure all dependencies are in the output directory

**Issue:** AOT compilation creates DLLs but they don't run standalone
- **Note:** This is a current limitation of Clojure CLR - compiled DLLs still require source files for initialization

## License

MIT License (or your chosen license)

## Contributing

Contributions welcome! The package is structured as:
- `src/Clojure.MSBuild.Tool/` - Core tool that handles assembly loading
- `build/` - MSBuild targets and props
- `tools/` - Compiled tool binaries for packaging