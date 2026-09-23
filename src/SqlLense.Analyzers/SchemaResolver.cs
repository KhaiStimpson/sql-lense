using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using SqlLense.Schema;

namespace SqlLense.Analyzers
{
    /// <summary>
    /// Finds the schema snapshot for a project. MSBuild properties (surfaced by the NuGet package's
    /// props file) win; analyzers installed through the VSIX get no MSBuild properties and fall back
    /// to locating the solution folder from the source file paths.
    /// </summary>
    public static class SchemaResolver
    {
        public const string EnabledProperty = "build_property.SqlLenseEnabled";
        public const string SchemaPathProperty = "build_property.SqlLenseSchemaPath";
        public const string DatabaseProperty = "build_property.SqlLenseDatabase";
        public const string SolutionDirProperty = "build_property.SolutionDir";

        public static bool IsEnabled(AnalyzerConfigOptions globalOptions) =>
            !(globalOptions.TryGetValue(EnabledProperty, out var enabled) &&
              string.Equals(enabled?.Trim(), "false", System.StringComparison.OrdinalIgnoreCase));

        public static string? GetSnapshotPath(AnalyzerConfigOptions globalOptions, string? anySourceFilePath)
        {
            globalOptions.TryGetValue(SchemaPathProperty, out var explicitPath);
            globalOptions.TryGetValue(DatabaseProperty, out var database);
            globalOptions.TryGetValue(SolutionDirProperty, out var solutionDir);

            if (string.IsNullOrWhiteSpace(explicitPath) &&
                (string.IsNullOrWhiteSpace(solutionDir) || solutionDir!.Contains("*Undefined*")))
            {
                solutionDir = SchemaLocator.FindSolutionDirectory(anySourceFilePath);
            }

            return SchemaLocator.GetSnapshotPath(explicitPath, solutionDir, database);
        }

        public static DatabaseSchema? GetSchema(AnalyzerConfigOptions globalOptions, Compilation compilation)
        {
            string? firstFile = null;
            foreach (var tree in compilation.SyntaxTrees)
            {
                if (!string.IsNullOrEmpty(tree.FilePath))
                {
                    firstFile = tree.FilePath;
                    break;
                }
            }

            return SchemaCache.TryGet(GetSnapshotPath(globalOptions, firstFile));
        }
    }
}
