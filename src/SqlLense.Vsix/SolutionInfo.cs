using System.IO;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace SqlLense.Vsix
{
    internal static class SolutionInfo
    {
        /// <summary>The open solution's folder, or null when no solution is open.</summary>
        public static string? GetSolutionDirectory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(Package.GetGlobalService(typeof(SVsSolution)) is IVsSolution solution) ||
                solution.GetSolutionInfo(out var directory, out var file, out _) != VSConstants.S_OK)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(directory))
            {
                return directory.TrimEnd('\\', '/');
            }

            return string.IsNullOrEmpty(file) ? null : Path.GetDirectoryName(file);
        }
    }
}
