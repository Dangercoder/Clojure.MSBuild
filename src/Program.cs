using System.Reflection;
using System.Text.Json;

namespace Clojure.MSBuild.Tool;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length < 2 || args[0] != "--assembly")
        {
            Console.WriteLine("Usage: Clojure.MSBuild.Tool --assembly <path> --mode <repl|nrepl|script|test|compile> [--script <path>] [--namespaces <ns1;ns2>] [--compile-path <path>]");
            Environment.Exit(1);
        }

        var assemblyPath = args[1];
        var mode = "";
        string? scriptPath = null;
        string? namespaces = null;
        string? compilePath = null;

        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--mode" && i + 1 < args.Length)
            {
                mode = args[++i];
            }
            else if (args[i] == "--script" && i + 1 < args.Length)
            {
                scriptPath = args[++i];
            }
            else if (args[i] == "--namespaces" && i + 1 < args.Length)
            {
                namespaces = args[++i];
            }
            else if (args[i] == "--compile-path" && i + 1 < args.Length)
            {
                compilePath = args[++i];
            }
        }

        RunClojure(assemblyPath, mode, scriptPath, namespaces, compilePath);
    }

    static string? _outputDir = null;
    
    static void RunClojure(string assemblyPath, string mode, string? scriptPath, string? namespaces = null, string? compilePath = null)
    {
        _outputDir = Path.GetDirectoryName(assemblyPath) ?? ".";
        
        // Set up assembly resolver BEFORE initializing Clojure
        // This will load assemblies on-demand when Clojure needs them
        AppDomain.CurrentDomain.AssemblyResolve += AssemblyResolver;
        
        Console.WriteLine($"Assembly resolver configured for: {_outputDir}");
        
        // Set CLOJURE_LOAD_PATH to include src directory BEFORE RT.Init()
        var srcDir = Path.Combine(Environment.CurrentDirectory, "src");
        Console.WriteLine($"Checking for src directory at: {srcDir}");
        if (Directory.Exists(srcDir))
        {
            Environment.SetEnvironmentVariable("CLOJURE_LOAD_PATH", srcDir);
            Console.WriteLine($"Set CLOJURE_LOAD_PATH to: {srcDir}");
        }
        else
        {
            Console.WriteLine($"src directory not found at: {srcDir}");
        }

        // Pre-load package assemblies using deps.json if available
        var depsFile = Path.ChangeExtension(assemblyPath, ".deps.json");
        PreloadPackageAssemblies(_outputDir, depsFile);
        
        // Load Clojure assemblies - both the runtime and the source assemblies
        var clojureAssembly = Assembly.LoadFrom(Path.Combine(_outputDir, "Clojure.dll"));
        var clojureSourceAssembly = Assembly.LoadFrom(Path.Combine(_outputDir, "Clojure.Source.dll"));
        
        if (clojureAssembly == null || clojureSourceAssembly == null)
        {
            Console.WriteLine("Error: Clojure.dll or Clojure.Source.dll not found in output directory");
            Console.WriteLine("Make sure your project references the Clojure NuGet package");
            Environment.Exit(1);
        }

        // Get Clojure types via reflection
        var rtType = clojureAssembly.GetType("clojure.lang.RT");
        var symbolType = clojureAssembly.GetType("clojure.lang.Symbol");
        var keywordType = clojureAssembly.GetType("clojure.lang.Keyword");
        
        // Initialize Clojure runtime - it will use our assembly resolver when it needs types
        var initMethod = rtType!.GetMethod("Init", BindingFlags.Public | BindingFlags.Static);
        initMethod!.Invoke(null, null);
        
        // Get core Clojure functions we need
        var varMethod = rtType.GetMethod("var", new[] { typeof(string), typeof(string) });
        var internSymbolMethod = symbolType!.GetMethod("intern", new[] { typeof(string) });
        
        // loadFile is actually called "loadFile" with capital F
        var loadFileMethod = rtType.GetMethod("loadFile", new[] { typeof(string) });
        if (loadFileMethod == null)
        {
            // Try alternative method names
            loadFileMethod = rtType.GetMethod("load_file", new[] { typeof(string) });
        }
        
        // Based on mode, run appropriate Clojure code
        switch (mode)
        {
            case "repl":
                StartRepl(rtType, varMethod!, internSymbolMethod!);
                break;
                
            case "nrepl":
                StartNRepl(rtType, varMethod!, internSymbolMethod!, keywordType!);
                break;
                
            case "script":
                if (string.IsNullOrEmpty(scriptPath))
                {
                    Console.WriteLine("Error: Script path required for script mode");
                    Environment.Exit(1);
                }
                RunScript(loadFileMethod!, scriptPath);
                break;
                
            case "test":
                RunTests(rtType, varMethod!, internSymbolMethod!);
                break;
                
            case "compile":
                if (string.IsNullOrEmpty(namespaces))
                {
                    Console.WriteLine("Error: --namespaces required for compile mode");
                    Environment.Exit(1);
                }
                CompileNamespaces(clojureAssembly, rtType, varMethod!, internSymbolMethod!, namespaces, compilePath ?? Path.Combine(_outputDir!, "compiled"));
                break;
                
            default:
                Console.WriteLine($"Error: Unknown mode '{mode}'");
                Console.WriteLine("Valid modes: repl, nrepl, script, test, compile");
                Environment.Exit(1);
                break;
        }
    }

    static void PreloadPackageAssemblies(string outputDir, string depsFile)
    {
        Console.WriteLine("Pre-loading package assemblies...");
        
        var packageAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        // Parse deps.json to find package assemblies
        if (File.Exists(depsFile))
        {
            try
            {
                var json = File.ReadAllText(depsFile);
                using var doc = JsonDocument.Parse(json);
                
                // Get libraries section which contains all dependencies
                if (doc.RootElement.TryGetProperty("libraries", out var libraries))
                {
                    foreach (var library in libraries.EnumerateObject())
                    {
                        // Skip Microsoft.NETCore and system libraries
                        if (library.Name.StartsWith("Microsoft.NETCore") || 
                            library.Name.StartsWith("runtime."))
                            continue;
                        
                        // Extract assembly names from library entries
                        var parts = library.Name.Split('/');
                        if (parts.Length > 0)
                        {
                            packageAssemblies.Add(parts[0]);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not parse deps.json: {ex.Message}");
            }
        }
        
        // Pre-load the identified package assemblies
        foreach (var dll in Directory.GetFiles(outputDir, "*.dll"))
        {
            var fileName = Path.GetFileNameWithoutExtension(dll);
            
            // Skip Microsoft.Dynamic and Microsoft.Scripting to avoid conflicts
            if (fileName.Equals("Microsoft.Dynamic", StringComparison.OrdinalIgnoreCase) || 
                fileName.Equals("Microsoft.Scripting", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("Microsoft.Scripting.Metadata", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            
            // Check if this assembly matches any package
            bool shouldLoad = false;
            foreach (var package in packageAssemblies)
            {
                if (fileName.StartsWith(package, StringComparison.OrdinalIgnoreCase))
                {
                    shouldLoad = true;
                    break;
                }
            }
            
            if (shouldLoad)
            {
                try
                {
                    Assembly.LoadFrom(dll);
                    Console.WriteLine($"Pre-loaded: {Path.GetFileName(dll)}");
                }
                catch
                {
                    // Skip if can't be loaded
                }
            }
        }
    }
    
    static Assembly? AssemblyResolver(object? sender, ResolveEventArgs args)
    {
        var assemblyName = new AssemblyName(args.Name);
        var dllName = assemblyName.Name + ".dll";
        var fullPath = Path.Combine(_outputDir!, dllName);
        
        if (File.Exists(fullPath))
        {
            Console.WriteLine($"Loading on-demand: {dllName}");
            return Assembly.LoadFrom(fullPath);
        }
        
        // Try without version info (sometimes assemblies request specific versions)
        var simpleName = args.Name.Split(',')[0] + ".dll";
        fullPath = Path.Combine(_outputDir!, simpleName);
        
        if (File.Exists(fullPath))
        {
            Console.WriteLine($"Loading on-demand: {simpleName}");
            return Assembly.LoadFrom(fullPath);
        }
        
        return null;
    }

    static void StartRepl(Type rtType, MethodInfo varMethod, MethodInfo internSymbolMethod)
    {
        Console.WriteLine("Starting Clojure REPL...");
        
        // (require 'clojure.main)
        var require = varMethod.Invoke(null, new[] { "clojure.core", "require" });
        var clojureMainSym = internSymbolMethod.Invoke(null, new[] { "clojure.main" });
        var invokeMethod = require!.GetType().GetMethod("invoke", new[] { typeof(object) });
        invokeMethod!.Invoke(require, new[] { clojureMainSym });
        
        // (clojure.main/main)
        var main = varMethod.Invoke(null, new[] { "clojure.main", "main" });
        var seqMethod = rtType.GetMethod("seq", new[] { typeof(object) });
        var emptyArgs = seqMethod!.Invoke(null, new object[] { new string[0] });
        var applyToMethod = main!.GetType().GetMethod("applyTo");
        applyToMethod!.Invoke(main, new[] { emptyArgs });
    }

    static void StartNRepl(Type rtType, MethodInfo varMethod, MethodInfo internSymbolMethod, Type keywordType)
    {
        Console.WriteLine("Starting nREPL server...");
        
        try
        {
            // (require 'clojure.tools.nrepl)
            var require = varMethod.Invoke(null, new[] { "clojure.core", "require" });
            var nreplSym = internSymbolMethod.Invoke(null, new[] { "clojure.tools.nrepl" });
            var invokeMethod = require!.GetType().GetMethod("invoke", new[] { typeof(object) });
            invokeMethod!.Invoke(require, new[] { nreplSym });
            
            // (clojure.tools.nrepl/start-server!)
            var startServer = varMethod.Invoke(null, new[] { "clojure.tools.nrepl", "start-server!" });
            var invoke0Method = startServer!.GetType().GetMethod("invoke", Type.EmptyTypes);
            invoke0Method!.Invoke(startServer, null);
            
            Console.WriteLine("nREPL started at port: 1667");
            Console.ReadLine();
        }
        catch (Exception ex)
        {
            var innerEx = ex.InnerException ?? ex;
            Console.WriteLine($"Error starting nREPL: {innerEx.Message}");
            Console.WriteLine("Make sure clojure.tools.nrepl version 0.1.0-alpha1 is in your project dependencies");
        }
    }

    static void RunScript(MethodInfo? loadFileMethod, string scriptPath)
    {
        Console.WriteLine($"Running script: {scriptPath}");
        
        if (!File.Exists(scriptPath))
        {
            Console.WriteLine($"Error: Script file not found: {scriptPath}");
            Environment.Exit(1);
        }
        
        if (loadFileMethod != null)
        {
            loadFileMethod.Invoke(null, new[] { scriptPath });
        }
        else
        {
            // Use Clojure's load-file function instead
            var rtType = AppDomain.CurrentDomain.GetAssemblies()
                .First(a => a.GetName().Name == "Clojure")
                .GetType("clojure.lang.RT");
            var varMethod = rtType!.GetMethod("var", new[] { typeof(string), typeof(string) });
            var loadFile = varMethod!.Invoke(null, new[] { "clojure.core", "load-file" });
            var invokeMethod = loadFile!.GetType().GetMethod("invoke", new[] { typeof(object) });
            invokeMethod!.Invoke(loadFile, new[] { scriptPath });
        }
    }

    static void RunTests(Type rtType, MethodInfo varMethod, MethodInfo internSymbolMethod)
    {
        Console.WriteLine("Running tests...");
        
        try
        {
            // (require 'clojure.test)
            var require = varMethod.Invoke(null, new[] { "clojure.core", "require" });
            var testSym = internSymbolMethod.Invoke(null, new[] { "clojure.test" });
            var invokeMethod = require!.GetType().GetMethod("invoke", new[] { typeof(object) });
            invokeMethod!.Invoke(require, new[] { testSym });
            
            // (clojure.test/run-all-tests)
            var runAllTests = varMethod.Invoke(null, new[] { "clojure.test", "run-all-tests" });
            var invoke0Method = runAllTests!.GetType().GetMethod("invoke", Type.EmptyTypes);
            invoke0Method!.Invoke(runAllTests, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error running tests: {ex.Message}");
        }
    }
    
    static void CompileNamespaces(Assembly clojureAssembly, Type rtType, MethodInfo varMethod, MethodInfo internSymbolMethod, string namespaces, string compilePath)
    {
        Console.WriteLine($"Compiling namespaces to: {compilePath}");
        
        // Create compile directory if it doesn't exist
        Directory.CreateDirectory(compilePath);
        
        try
        {
            // First, require clojure.core
            var require = varMethod.Invoke(null, new[] { "clojure.core", "require" });
            var clojureCoreSym = internSymbolMethod.Invoke(null, new[] { "clojure.core" });
            var requireInvokeMethod = require!.GetType().GetMethod("invoke", new[] { typeof(object) });
            requireInvokeMethod!.Invoke(require, new[] { clojureCoreSym });
            
            // Also load clojure.core.specs.alpha if available (required for some compilations)
            try
            {
                var specsSym = internSymbolMethod.Invoke(null, new[] { "clojure.core.specs.alpha" });
                requireInvokeMethod!.Invoke(require, new[] { specsSym });
            }
            catch
            {
                // Ignore if specs not available
            }
            
            // Get Var class for thread bindings
            var varType = clojureAssembly.GetType("clojure.lang.Var");
            
            // Get binding functions
            var pushThreadBindings = varType!.GetMethod("pushThreadBindings", BindingFlags.Public | BindingFlags.Static);
            var popThreadBindings = varType.GetMethod("popThreadBindings", BindingFlags.Public | BindingFlags.Static);
            var mapMethod = rtType.GetMethod("map", BindingFlags.Public | BindingFlags.Static);
            
            if (pushThreadBindings == null || popThreadBindings == null || mapMethod == null)
            {
                throw new Exception("Could not find required methods for thread bindings");
            }
            
            // Get compile-path and compile-files vars
            var compilePathVar = varMethod.Invoke(null, new[] { "clojure.core", "*compile-path*" });
            var compileFilesVar = varMethod.Invoke(null, new[] { "clojure.core", "*compile-files*" });
            
            if (compilePathVar == null || compileFilesVar == null)
            {
                throw new Exception("Could not find compile path or compile files vars");
            }
            
            // Create bindings map: {*compile-path* compilePath, *compile-files* true}
            var bindings = mapMethod.Invoke(null, new object[] { 
                new object[] { 
                    compilePathVar, compilePath,
                    compileFilesVar, true 
                }
            });
            
            // Push thread bindings
            pushThreadBindings!.Invoke(null, new[] { bindings });
            
            try
            {
                // Get the compile function
                var compileFn = varMethod.Invoke(null, new[] { "clojure.core", "compile" });
                if (compileFn == null)
                {
                    throw new Exception("Could not find compile function");
                }
                
                var compileInvokeMethod = compileFn.GetType().GetMethod("invoke", new[] { typeof(object) });
                if (compileInvokeMethod == null)
                {
                    throw new Exception("Could not find invoke method on compile function");
                }
                
                // Split namespaces and compile each one
                var namespaceList = namespaces.Split(';', StringSplitOptions.RemoveEmptyEntries);
                foreach (var ns in namespaceList)
                {
                    var trimmedNs = ns.Trim();
                    if (!string.IsNullOrEmpty(trimmedNs))
                    {
                        Console.WriteLine($"  Compiling {trimmedNs}...");
                        try
                        {
                            // First, require the namespace
                            var requireNsSym = internSymbolMethod.Invoke(null, new[] { trimmedNs });
                            requireInvokeMethod!.Invoke(require, new[] { requireNsSym });
                            
                            // Now compile it
                            var nsSym = internSymbolMethod.Invoke(null, new[] { trimmedNs });
                            compileInvokeMethod.Invoke(compileFn, new[] { nsSym });
                            Console.WriteLine($"  ✓ Compiled {trimmedNs}");
                        }
                        catch (Exception ex)
                        {
                            var innerEx = ex.InnerException ?? ex;
                            Console.WriteLine($"  ✗ Failed to compile {trimmedNs}: {innerEx.Message}");
                            if (innerEx.StackTrace != null)
                            {
                                Console.WriteLine($"     Stack trace: {innerEx.StackTrace}");
                            }
                        }
                    }
                }
            }
            finally
            {
                // Always pop thread bindings
                popThreadBindings!.Invoke(null, null);
            }
            
            Console.WriteLine("Compilation complete!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during compilation: {ex.Message}");
            Environment.Exit(1);
        }
    }
}