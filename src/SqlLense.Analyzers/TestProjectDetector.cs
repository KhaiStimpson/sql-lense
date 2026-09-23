using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SqlLense.Analyzers
{
    /// <summary>
    /// Recognizes test projects, whose SQL is often deliberately invalid or written against fixture
    /// schemas. The <c>IsTestProject</c> MSBuild property (set by Microsoft.NET.Test.Sdk) is used when
    /// the NuGet package surfaces it; analyzers installed through the VSIX get no MSBuild properties,
    /// so a reference to a known test framework assembly counts too.
    /// </summary>
    public static class TestProjectDetector
    {
        public const string IsTestProjectProperty = "build_property.IsTestProject";
        public const string AnalyzeTestsProperty = "build_property.SqlLenseAnalyzeTests";

        private static readonly string[] s_testFrameworkAssemblies =
        {
            "xunit.core",
            "xunit.v3.core",
            "nunit.framework",
            "Microsoft.VisualStudio.TestPlatform.TestFramework",
            "MSTest.TestFramework",
            "TUnit.Core",
        };

        /// <summary>True when the project should be skipped because it is a test project.</summary>
        public static bool ShouldSkip(AnalyzerConfigOptions globalOptions, Compilation compilation)
        {
            if (IsTrue(globalOptions, AnalyzeTestsProperty))
            {
                return false;
            }

            if (IsTrue(globalOptions, IsTestProjectProperty))
            {
                return true;
            }

            foreach (var assembly in compilation.ReferencedAssemblyNames)
            {
                foreach (var name in s_testFrameworkAssemblies)
                {
                    if (string.Equals(assembly.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsTrue(AnalyzerConfigOptions globalOptions, string key) =>
            globalOptions.TryGetValue(key, out var value) &&
            string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }
}
