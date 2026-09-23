using System;
using System.IO;
using System.Linq;
using System.Text;

namespace SqlLense.Schema
{
    /// <summary>
    /// Computes where the schema snapshot for a solution lives. Snapshots are per user and stay out
    /// of the repository: <c>%LOCALAPPDATA%/SqlLense/schemas/&lt;solution folder&gt;-&lt;hash&gt;/&lt;database&gt;.json</c>.
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
        /// the solution directory and the database name. Returns null when there is no solution.
        /// </summary>
        public static string? GetSnapshotPath(string? explicitPath, string? solutionDir, string? databaseName)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                return explicitPath;
            }

            var solutionKey = GetSolutionKey(solutionDir);
            if (solutionKey == null)
            {
                return null;
            }

            return Path.Combine(GetRootDirectory(), solutionKey, SanitizeFileName(NormalizeDatabaseName(databaseName)) + ".json");
        }

        public static string NormalizeDatabaseName(string? databaseName) =>
            string.IsNullOrWhiteSpace(databaseName) ? DefaultDatabaseName : databaseName!.Trim();

        /// <summary>
        /// A stable directory name for a solution folder: a readable prefix plus a hash of the
        /// normalized path, so two solutions with the same folder name do not collide. The folder
        /// (rather than the .sln file) is the key because analyzers installed through the VSIX do not
        /// receive MSBuild properties and must discover the solution from source file locations.
        /// </summary>
        public static string? GetSolutionKey(string? solutionDir)
        {
            if (string.IsNullOrWhiteSpace(solutionDir) || solutionDir!.IndexOf("*Undefined*", StringComparison.Ordinal) >= 0)
            {
                return null;
            }

            var normalized = NormalizePath(solutionDir);
            var name = normalized.Substring(normalized.LastIndexOf('/') + 1);
            return SanitizeFileName(name) + "-" + Fnv1a64(normalized).ToString("x16");
        }

        /// <summary>
        /// Walks up from a file or directory to the nearest folder containing a solution file.
        /// Results are cached per directory; this runs at most once per folder per process.
        /// </summary>
        public static string? FindSolutionDirectory(string? startPath)
        {
            if (string.IsNullOrEmpty(startPath))
            {
                return null;
            }

            string? directory;
            try
            {
                directory = File.Exists(startPath) ? Path.GetDirectoryName(startPath) : startPath;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                return null;
            }

            return directory == null ? null : s_solutionDirectories.GetOrAdd(directory, FindSolutionDirectoryUncached);
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> s_solutionDirectories =
            new System.Collections.Concurrent.ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        private static string? FindSolutionDirectoryUncached(string directory)
        {
            try
            {
                for (var current = new DirectoryInfo(directory); current != null; current = current.Parent)
                {
                    if (!current.Exists)
                    {
                        continue;
                    }

                    if (current.EnumerateFiles("*.sln").Any() || current.EnumerateFiles("*.slnx").Any())
                    {
                        return current.FullName;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }

            return null;
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
