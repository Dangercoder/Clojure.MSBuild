# Simple Clojure.MSBuild Example

This example demonstrates how to use Clojure.MSBuild to integrate Clojure CLR with .NET projects and NuGet packages.

## Features Demonstrated

- Using .NET System libraries from Clojure
- Using NuGet packages (Newtonsoft.Json) from Clojure
- Using Clojure libraries (clojure.data.json)
- Running Clojure code with MSBuild targets

## Setup

This project uses a local `nuget.config` to reference the locally-built Clojure.MSBuild package from `../../bin/Release`.

## Usage

First, build the project to restore all dependencies:

```bash
dotnet build
```

Then you can use any of the following commands:

### Run the main.clj file
```bash
dotnet msbuild -t:ClojureRun -p:File=src/main.clj
```

### Start a REPL
```bash
dotnet msbuild -t:ClojureRepl
```

In the REPL, you can load the namespace and test functions:
```clojure
(load-file "src/main.clj")
(main/run)
(main/demonstrate-nuget-integration)
```

### Start an nREPL server
```bash
dotnet msbuild -t:ClojureNRepl
```

## Project Structure

- `Simple.csproj` - The project file that references Clojure.MSBuild and other dependencies
- `nuget.config` - Configuration to use the local package source
- `Program.cs` - Minimal C# entry point (required by .NET)
- `src/main.clj` - The Clojure code demonstrating NuGet integration

## What This Example Shows

The example demonstrates that with Clojure.MSBuild, you can:

1. **Use any NuGet package** - The example uses Newtonsoft.Json without any manual assembly loading
2. **Mix .NET and Clojure** - Seamlessly use both .NET types and Clojure libraries
3. **Standard .NET tooling** - Use familiar `dotnet` commands and MSBuild targets
4. **Zero configuration** - Dependencies are automatically loaded when Clojure starts