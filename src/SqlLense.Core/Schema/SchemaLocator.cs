using System;
using System.IO;
using System.Text;

namespace SqlLense.Schema
{
    /// <summary>
    /// Computes where the schema snapshot for a solution lives. Snapshots are per user and stay out
    /// of the repository: <c>%LOCALAPPDATA%/SqlLense/schemas/&lt;solution&gt;-&lt;hash&gt;/&lt;database&gt;.json</c>.
    /// The database name defaults to <see cref="DefaultDatabaseName"/>; the MSBuild property
    /// <c>SqlLenseDatabase</c> selects a different one per project, which is how several databases
    /// per solution are supported.
    /// </summary>
    public static class SchemaLocator
    {
        public const string DefaultDatabaseName = "default";

        /// <summary>Overrides the snapshot root directory (mainly for tests and CI).</summary>
        public const string RootOverrideEnvironmentVariable = "SQLLENSE_SCHEMA_DIR";

        public static string GetRootDirectory()
        {
            var overridden = Environment.GetEnvironmentVariable(RootOverrideEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                return overridden!;
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localAppData))
            {
                localAppData = Path.GetTempPath();
            }

            return Path.Combine(localAppData, "SqlLense", "schemas");
        }

        /// <summary>
        /// Resolves the snapshot path. An explicit path always wins; otherwise the path is derived from
        /// the solution file (or directory) and the database name. Returns null when there is nothing
        /// to derive it from, e.g. a project built outside of a solution.
        /// </summary>
        public static string? GetSnapshotPath(string? explicitPath, string? solutionPath, string? solutionDir, string? databaseName)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                return explicitPath;
            }

            var solutionKey = GetSolutionKey(solutionPath, solutionDir);
            if (solutionKey == null)
            {
                return null;
            }

            return Path.Combine(GetRootDirectory(), solutionKey, SanitizeFileName(NormalizeDatabaseName(databaseName)) + ".json");
        }

        public static string NormalizeDatabaseName(string? databaseName) =>
            string.IsNullOrWhiteSpace(databaseName) ? DefaultDatabaseName : databaseName!.Trim();

        /// <summary>
        /// A stable directory name for a solution: readable prefix plus a hash of the normalized path so
        /// two solutions with the same name in different folders do not collide.
        /// </summary>
        public static string? GetSolutionKey(string? solutionPath, string? solutionDir)
        {
            string? source = null;
            string name;
            if (!string.IsNullOrWhiteSpace(solutionPath) && !solutionPath!.EndsWith("*Undefined*", StringComparison.Ordinal))
            {
                source = solutionPath;
                name = Path.GetFileNameWithoutExtension(solutionPath);
            }
            else if (!string.IsNullOrWhiteSpace(solutionDir) && !solutionDir!.EndsWith("*Undefined*", StringComparison.Ordinal))
            {
                source = solutionDir;
                name = Path.GetFileName(solutionDir.TrimEnd('/', '\\'));
            }
            else
            {
                return null;
            }

            var normalized = NormalizePath(source);
            return SanitizeFileName(name) + "-" + Fnv1a64(normalized).ToString("x16");
        }

        internal static string NormalizePath(string path)
        {
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                full = path;
            }

            // Paths are case-insensitive on Windows, where Visual Studio runs; normalizing case keeps the
            // key identical whichever host (IDE, command-line build) computes it.
            return full.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                sb.Append(Array.IndexOf(invalid, c) >= 0 || c == '/' || c == '\\' || c == ':' ? '_' : c);
            }

            return sb.Length == 0 ? "solution" : sb.ToString();
        }

        private static ulong Fnv1a64(string text)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            var hash = offset;
            foreach (var c in text)
            {
                hash ^= c;
                hash *= prime;
            }

            return hash;
        }
    }
}
