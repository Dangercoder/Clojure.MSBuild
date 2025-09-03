using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;

namespace Clojure.MSBuild.TestAdapter
{
    [ExtensionUri(ClojureTestDiscoverer.ExecutorUriString)]
    public class ClojureTestExecutor : ITestExecutor
    {
        private bool _cancelled;
        
        public void Cancel()
        {
            _cancelled = true;
        }
        
        public void RunTests(IEnumerable<TestCase> tests, IRunContext runContext, IFrameworkHandle frameworkHandle)
        {
            _cancelled = false;
            frameworkHandle?.SendMessage(TestMessageLevel.Informational, "Clojure Test Executor: Starting test execution");
            
            // Group tests by source assembly
            var testsBySource = tests.GroupBy(t => t.Source);
            
            foreach (var sourceGroup in testsBySource)
            {
                if (_cancelled) break;
                
                var source = sourceGroup.Key;
                var directory = Path.GetDirectoryName(source);
                var projectDir = Path.GetFullPath(Path.Combine(directory, "..", "..", ".."));
                
                // Find the Clojure.MSBuild.Tool
                var toolPath = FindClojureTool(projectDir);
                if (toolPath == null)
                {
                    frameworkHandle?.SendMessage(TestMessageLevel.Error, 
                        "Could not find Clojure.MSBuild.Tool.dll");
                    continue;
                }
                
                // Group tests by namespace
                var testsByNamespace = sourceGroup.GroupBy(t => t.Traits.FirstOrDefault(tr => tr.Name == "Namespace")?.Value ?? "");
                
                foreach (var nsGroup in testsByNamespace)
                {
                    if (_cancelled) break;
                    
                    var ns = nsGroup.Key;
                    if (string.IsNullOrEmpty(ns)) continue;
                    
                    // Run tests for this namespace
                    RunNamespaceTests(toolPath, source, ns, nsGroup.ToList(), projectDir, frameworkHandle);
                }
            }
            
            frameworkHandle?.SendMessage(TestMessageLevel.Informational, "Clojure Test Execution completed");
        }
        
        public void RunTests(IEnumerable<string> sources, IRunContext runContext, IFrameworkHandle frameworkHandle)
        {
            _cancelled = false;
            frameworkHandle?.SendMessage(TestMessageLevel.Informational, "Clojure Test Executor: Starting full test run");
            
            foreach (var source in sources)
            {
                if (_cancelled) break;
                
                var directory = Path.GetDirectoryName(source);
                var projectDir = Path.GetFullPath(Path.Combine(directory, "..", "..", ".."));
                
                // Find the tool
                var toolPath = FindClojureTool(projectDir);
                if (toolPath == null)
                {
                    frameworkHandle?.SendMessage(TestMessageLevel.Error, 
                        "Could not find Clojure.MSBuild.Tool.dll");
                    continue;
                }
                
                // Run all tests using the tool
                RunAllTests(toolPath, source, projectDir, frameworkHandle);
            }
        }
        
        private string? FindClojureTool(string projectDir)
        {
            // Look for the tool in various locations
            var possiblePaths = new[]
            {
                Path.Combine(projectDir, "tools", "net9.0", "Clojure.MSBuild.Tool.dll"),
                Path.Combine(projectDir, "..", "..", "tools", "net9.0", "Clojure.MSBuild.Tool.dll"),
                Path.Combine(projectDir, "..", "..", "..", "..", "tools", "net9.0", "Clojure.MSBuild.Tool.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 
                    ".nuget", "packages", "clojure.msbuild", "*", "tools", "net9.0", "Clojure.MSBuild.Tool.dll")
            };
            
            foreach (var pattern in possiblePaths)
            {
                if (pattern.Contains("*"))
                {
                    var dir = Path.GetDirectoryName(pattern);
                    var searchPattern = Path.GetFileName(pattern);
                    if (Directory.Exists(dir))
                    {
                        var files = Directory.GetFiles(dir, searchPattern, SearchOption.AllDirectories);
                        if (files.Length > 0)
                            return files.OrderByDescending(f => f).First(); // Get latest version
                    }
                }
                else if (File.Exists(pattern))
                {
                    return pattern;
                }
            }
            
            return null;
        }
        
        private void RunNamespaceTests(string toolPath, string assembly, string ns, 
            List<TestCase> tests, string projectDir, IFrameworkHandle frameworkHandle)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{toolPath}\" --assembly \"{assembly}\" --mode test-namespace --namespace {ns}",
                WorkingDirectory = projectDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            var output = new StringBuilder();
            var errors = new StringBuilder();
            
            using (var process = Process.Start(startInfo))
            {
                process.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) output.AppendLine(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) errors.AppendLine(e.Data); };
                
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                
                process.WaitForExit();
                
                // Parse test results from output
                ParseTestResults(output.ToString(), tests, frameworkHandle);
                
                if (process.ExitCode != 0)
                {
                    frameworkHandle?.SendMessage(TestMessageLevel.Error, 
                        $"Test execution failed with exit code {process.ExitCode}: {errors}");
                }
            }
        }
        
        private void RunAllTests(string toolPath, string assembly, string projectDir, IFrameworkHandle frameworkHandle)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{toolPath}\" --assembly \"{assembly}\" --mode test",
                WorkingDirectory = projectDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            var output = new StringBuilder();
            
            using (var process = Process.Start(startInfo))
            {
                process.OutputDataReceived += (s, e) => 
                { 
                    if (!string.IsNullOrEmpty(e.Data)) 
                    {
                        output.AppendLine(e.Data);
                        frameworkHandle?.SendMessage(TestMessageLevel.Informational, e.Data);
                    }
                };
                
                process.BeginOutputReadLine();
                process.WaitForExit();
                
                // Parse summary line
                var summaryMatch = Regex.Match(output.ToString(), 
                    @"Ran (\d+) tests containing (\d+) assertions\.\s*(\d+) failures, (\d+) errors");
                
                if (summaryMatch.Success)
                {
                    var testCount = int.Parse(summaryMatch.Groups[1].Value);
                    var failures = int.Parse(summaryMatch.Groups[3].Value);
                    var errors = int.Parse(summaryMatch.Groups[4].Value);
                    
                    // Create a synthetic test case for reporting to VSTest
                    var testCase = new TestCase($"ClojureTests.AllTests", ClojureTestDiscoverer.ExecutorUri, assembly)
                    {
                        DisplayName = $"Clojure Tests ({testCount} tests)",
                        CodeFilePath = projectDir
                    };
                    
                    frameworkHandle?.RecordStart(testCase);
                    
                    var result = new TestResult(testCase)
                    {
                        Outcome = (failures == 0 && errors == 0) ? TestOutcome.Passed : TestOutcome.Failed,
                        StartTime = DateTimeOffset.Now,
                        EndTime = DateTimeOffset.Now,
                        Duration = TimeSpan.FromMilliseconds(100)
                    };
                    
                    if (failures > 0 || errors > 0)
                    {
                        result.ErrorMessage = $"Tests failed: {failures} failures, {errors} errors";
                        frameworkHandle?.SendMessage(TestMessageLevel.Error, result.ErrorMessage);
                    }
                    else
                    {
                        frameworkHandle?.SendMessage(TestMessageLevel.Informational, 
                            $"All {testCount} Clojure tests passed successfully");
                    }
                    
                    frameworkHandle?.RecordEnd(testCase, result.Outcome);
                    frameworkHandle?.RecordResult(result);
                }
            }
        }
        
        private void ParseTestResults(string output, List<TestCase> tests, IFrameworkHandle frameworkHandle)
        {
            // Simple parsing - look for test results in output
            // Format: "Testing namespace/test-name"
            // Followed by either success (no output) or failure messages
            
            var lines = output.Split('\n');
            TestCase currentTest = null;
            var testStarted = false;
            var errorMessages = new List<string>();
            
            foreach (var line in lines)
            {
                // Check if this is a test start
                foreach (var test in tests)
                {
                    if (line.Contains($"Testing {test.FullyQualifiedName}") || 
                        line.Contains($"test-{test.DisplayName}"))
                    {
                        // Process previous test if any
                        if (currentTest != null && testStarted)
                        {
                            CompleteTest(currentTest, errorMessages, frameworkHandle);
                        }
                        
                        currentTest = test;
                        testStarted = true;
                        errorMessages.Clear();
                        
                        var result = new TestResult(test)
                        {
                            StartTime = DateTimeOffset.Now
                        };
                        frameworkHandle?.RecordStart(test);
                        break;
                    }
                }
                
                // Collect error messages
                if (testStarted && (line.Contains("FAIL") || line.Contains("ERROR") || line.Contains("expected:")))
                {
                    errorMessages.Add(line);
                }
            }
            
            // Process last test
            if (currentTest != null && testStarted)
            {
                CompleteTest(currentTest, errorMessages, frameworkHandle);
            }
            
            // Check for summary line to determine overall success
            var summaryMatch = Regex.Match(output, @"Ran (\d+) tests.*?(\d+) failures, (\d+) errors");
            if (summaryMatch.Success)
            {
                var failures = int.Parse(summaryMatch.Groups[2].Value);
                var errors = int.Parse(summaryMatch.Groups[3].Value);
                
                // If no specific test results were parsed but we have the summary,
                // mark all tests based on summary
                if (!testStarted && tests.Count > 0)
                {
                    var success = failures == 0 && errors == 0;
                    foreach (var test in tests)
                    {
                        var result = new TestResult(test)
                        {
                            Outcome = success ? TestOutcome.Passed : TestOutcome.Failed,
                            StartTime = DateTimeOffset.Now,
                            EndTime = DateTimeOffset.Now,
                            Duration = TimeSpan.Zero
                        };
                        
                        if (!success)
                        {
                            result.ErrorMessage = "Test failed - see output for details";
                        }
                        
                        frameworkHandle?.RecordResult(result);
                    }
                }
            }
        }
        
        private void CompleteTest(TestCase test, List<string> errorMessages, IFrameworkHandle frameworkHandle)
        {
            var result = new TestResult(test)
            {
                EndTime = DateTimeOffset.Now,
                Duration = TimeSpan.FromMilliseconds(100) // Approximate
            };
            
            if (errorMessages.Any())
            {
                result.Outcome = TestOutcome.Failed;
                result.ErrorMessage = string.Join("\n", errorMessages);
            }
            else
            {
                result.Outcome = TestOutcome.Passed;
            }
            
            frameworkHandle?.RecordEnd(test, result.Outcome);
            frameworkHandle?.RecordResult(result);
        }
    }
}