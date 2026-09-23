using System;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using SqlLense.Schema;
using SqlLense.SchemaReader;
using SqlLense.Vsix.Options;
using Task = System.Threading.Tasks.Task;

namespace SqlLense.Vsix.Commands
{
    /// <summary>
    /// Reads the database schema and writes the per-user snapshot. Uses the in-box
    /// System.Data.SqlClient so no SQL client binaries are loaded into devenv.exe; for Entra ID
    /// authentication use the <c>sqllense</c> CLI instead.
    /// </summary>
    internal sealed class SchemaRefreshService
    {
        private static readonly Guid s_outputPaneGuid = new Guid("0d4e3b3a-1f7e-4d55-9a0c-5a1b1c2d9e77");

        private readonly AsyncPackage _package;
        private int _running;

        public SchemaRefreshService(AsyncPackage package) => _package = package;

        public bool IsRunning => Volatile.Read(ref _running) != 0;

        /// <summary>Refreshes the snapshot for the open solution. Safe to call from any thread.</summary>
        public async Task RefreshAsync(bool interactive, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _running, 1) != 0)
            {
                return;
            }

            try
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                var solutionDir = SolutionInfo.GetSolutionDirectory();
                if (solutionDir == null)
                {
                    await ReportAsync("Open a solution before refreshing the database schema.", error: true, interactive, cancellationToken);
                    return;
                }

                var settings = SqlLenseSettings.Load(solutionDir);
                if (!settings.IsConfigured)
                {
                    await ReportAsync("No connection string configured for this solution. Use Tools > SqlLense: Configure Connection...", error: true, interactive, cancellationToken);
                    if (interactive)
                    {
                        _package.ShowOptionPage(typeof(SqlLenseOptionsPage));
                    }

                    return;
                }

                var path = SchemaLocator.GetSnapshotPath(null, solutionDir, settings.DatabaseProfile)!;
                await SetStatusAsync("SqlLense: reading database schema...", cancellationToken);

                // Leave the UI thread for all I/O.
                await TaskScheduler.Default;
                var stopwatch = Stopwatch.StartNew();
                DatabaseSchema schema;
                using (var connection = new SqlConnection(BuildConnectionString(settings)))
                {
                    schema = await SqlServerSchemaReader.ReadAsync(connection, cancellationToken).ConfigureAwait(false);
                }

                SchemaSnapshotSerializer.Save(schema, path);
                SchemaCache.Invalidate(path);

                await ReportAsync(
                    $"Schema for '{schema.DatabaseName}' refreshed: {schema.Objects.Count} objects in {stopwatch.ElapsedMilliseconds} ms. Snapshot: {path}",
                    error: false,
                    interactive: false,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                await ReportAsync("Schema refresh failed: " + ex.Message, error: true, interactive, CancellationToken.None);
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        }

        internal static string BuildConnectionString(SolutionSettings settings)
        {
            var builder = new SqlConnectionStringBuilder(settings.ConnectionString);
            if (!string.IsNullOrEmpty(settings.Password))
            {
                builder.Password = settings.Password;
            }

            if (string.IsNullOrEmpty(builder.ApplicationName) || builder.ApplicationName == ".Net SqlClient Data Provider")
            {
                builder.ApplicationName = "SqlLense";
            }

            return builder.ConnectionString;
        }

        private async Task ReportAsync(string message, bool error, bool interactive, CancellationToken cancellationToken)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            await SetStatusAsync("SqlLense: " + message, cancellationToken);

            if (await _package.GetServiceAsync(typeof(SVsOutputWindow)) is IVsOutputWindow output)
            {
                var guid = s_outputPaneGuid;
                if (output.GetPane(ref guid, out var pane) != VSConstants.S_OK || pane == null)
                {
                    output.CreatePane(ref guid, "SqlLense", 1, 0);
                    output.GetPane(ref guid, out pane);
                }

                pane?.OutputStringThreadSafe($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
                if (error)
                {
                    pane?.Activate();
                }
            }

            if (error && interactive)
            {
                VsShellUtilities.ShowMessageBox(_package, message, "SqlLense", OLEMSGICON.OLEMSGICON_WARNING,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
        }

        private async Task SetStatusAsync(string text, CancellationToken cancellationToken)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (await _package.GetServiceAsync(typeof(SVsStatusbar)) is IVsStatusbar statusBar)
            {
                statusBar.SetText(text);
            }
        }
    }
}
