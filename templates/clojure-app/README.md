# ClojureApp

A Clojure CLR application built with MSBuild.

## Commands

```bash
# Run the application
dotnet run

# Run tests
dotnet test

# Build the project
dotnet build

# Start a REPL
dotnet msbuild /t:clj-repl

# Start nREPL server (port 1667)
dotnet msbuild /t:clj-nrepl
```

## Project Structure

```
src/
  main.clj         # Main application entry point (main namespace)
test/
  main_test.clj    # Test file (main-test namespace)
```