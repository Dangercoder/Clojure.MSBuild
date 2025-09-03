using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;

namespace Clojure.MSBuild.TestAdapter
{
    [FileExtension(".dll")]
    [FileExtension(".exe")]
    [DefaultExecutorUri("executor://clojuretestexecutor/v1")]
    public class ClojureTestDiscoverer : ITestDiscoverer
    {
        public const string ExecutorUriString = "executor://clojuretestexecutor/v1";
        public static readonly Uri ExecutorUri = new Uri(ExecutorUriString);
        
        public void DiscoverTests(IEnumerable<string> sources, IDiscoveryContext discoveryContext, 
            IMessageLogger logger, ITestCaseDiscoverySink discoverySink)
        {
            logger?.SendMessage(TestMessageLevel.Informational, "Clojure Test Discoverer: Starting test discovery");
            
            foreach (var source in sources)
            {
                logger?.SendMessage(TestMessageLevel.Informational, $"Discovering tests in: {source}");
                
                var directory = Path.GetDirectoryName(source);
                var testDir = Path.Combine(directory, "..", "..", "..", "test");
                
                if (!Directory.Exists(testDir))
                {
                    logger?.SendMessage(TestMessageLevel.Informational, $"No test directory found at: {testDir}");
                    continue;
                }
                
                // Find all test files
                var testFiles = Directory.GetFiles(testDir, "*_test.cljr", SearchOption.AllDirectories);
                logger?.SendMessage(TestMessageLevel.Informational, $"Found {testFiles.Length} test files");
                
                foreach (var testFile in testFiles)
                {
                    var relativePath = Path.GetRelativePath(testDir, testFile);
                    var ns = Path.GetFileNameWithoutExtension(relativePath).Replace('_', '-');
                    
                    // Read the file to find deftest declarations
                    var content = File.ReadAllText(testFile);
                    var testPattern = @"^\s*\(deftest\s+([a-zA-Z0-9-_]+)";
                    var matches = Regex.Matches(content, testPattern, RegexOptions.Multiline);
                    
                    foreach (Match match in matches)
                    {
                        var testName = match.Groups[1].Value;
                        var fullyQualifiedName = $"{ns}/{testName}";
                        
                        var testCase = new TestCase(fullyQualifiedName, ExecutorUri, source)
                        {
                            DisplayName = testName,
                            CodeFilePath = testFile,
                            LineNumber = GetLineNumber(content, match.Index)
                        };
                        
                        // Store namespace in traits for execution
                        testCase.Traits.Add(new Trait("Namespace", ns));
                        testCase.Traits.Add(new Trait("TestFile", testFile));
                        
                        discoverySink.SendTestCase(testCase);
                        logger?.SendMessage(TestMessageLevel.Informational, $"Discovered test: {fullyQualifiedName}");
                    }
                }
            }
            
            logger?.SendMessage(TestMessageLevel.Informational, "Clojure Test Discovery completed");
        }
        
        private int GetLineNumber(string content, int charIndex)
        {
            var lineNumber = 1;
            for (int i = 0; i < charIndex && i < content.Length; i++)
            {
                if (content[i] == '\n')
                    lineNumber++;
            }
            return lineNumber;
        }
    }
}