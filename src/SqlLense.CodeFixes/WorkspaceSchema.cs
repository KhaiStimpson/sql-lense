using System.IO;
using Microsoft.CodeAnalysis;
using SqlLense.Analyzers;
using SqlLense.Schema;

namespace SqlLense.CodeFixes
{
    /// <summary>Resolves the schema snapshot for a workspace document (IDE features).</summary>
    public static class WorkspaceSchema
    {
        public static string? GetSnapshotPath(Document document)
        {
            var globalOptions = document.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions;
            globalOptions.TryGetValue(SchemaResolver.SchemaPathProperty, out var explicitPath);
            globalOptions.TryGetValue(SchemaResolver.DatabaseProperty, out var database);

            // In the IDE the solution file is authoritative; fall back to walking up from the document.
            var solutionPath = document.Project.Solution.FilePath;
            var solutionDir = !string.IsNullOrEmpty(solutionPath)
                ? Path.GetDirectoryName(solutionPath)
                : SchemaLocator.FindSolutionDirectory(document.FilePath);

            return SchemaLocator.GetSnapshotPath(explicitPath, solutionDir, database);
        }

        public static DatabaseSchema? GetSchema(Document document) => SchemaCache.TryGet(GetSnapshotPath(document));
    }
}
