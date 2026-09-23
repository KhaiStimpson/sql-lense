using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using SqlLense.Vsix.Commands;
using SqlLense.Vsix.Options;
using SolutionEvents = Microsoft.VisualStudio.Shell.Events.SolutionEvents;
using Task = System.Threading.Tasks.Task;

namespace SqlLense.Vsix
{
    /// <summary>
    /// Hosts the options page and the schema commands. Editor features (highlighting, Quick Info) and
    /// Roslyn features (diagnostics, fixes, completion) are MEF/analyzer components and do not need the
    /// package to be loaded, so it loads in the background only once a solution is open.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("SqlLense", "Validates SQL inside C# strings against your database schema.", "0.1.0")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideOptionPage(typeof(SqlLenseOptionsPage), "SqlLense", "General", 0, 0, supportsAutomation: true)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExistsAndFullyLoaded_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideBindingPath]
    [Guid(PackageIds.PackageGuidString)]
    public sealed class SqlLensePackage : AsyncPackage
    {
        private SchemaRefreshService? _refresh;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            _refresh = new SchemaRefreshService(this);

            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            SqlLenseSettings.LoadEditorSettings();

            if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commands)
            {
                var refresh = new OleMenuCommand(
                    (_, _) => JoinableTaskFactory.RunAsync(() => _refresh.RefreshAsync(interactive: true, DisposalToken)).FileAndForget("SqlLense/Refresh"),
                    new CommandID(PackageIds.CommandSet, PackageIds.RefreshSchemaCommandId));
                refresh.BeforeQueryStatus += (_, _) =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    refresh.Enabled = !_refresh.IsRunning && SolutionInfo.GetSolutionDirectory() != null;
                };
                commands.AddCommand(refresh);

                commands.AddCommand(new MenuCommand(
                    (_, _) => ShowOptionPage(typeof(SqlLenseOptionsPage)),
                    new CommandID(PackageIds.CommandSet, PackageIds.ConfigureCommandId)));
            }

            SolutionEvents.OnAfterOpenSolution += (_, _) => RefreshIfConfiguredOnOpen();

            // The package loads after the solution has opened, so handle the current one too.
            RefreshIfConfiguredOnOpen();
        }

        private void RefreshIfConfiguredOnOpen()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var solutionDir = SolutionInfo.GetSolutionDirectory();
            if (solutionDir == null || _refresh == null)
            {
                return;
            }

            var settings = SqlLenseSettings.Load(solutionDir);
            if (settings.IsConfigured && settings.RefreshOnSolutionOpen)
            {
                JoinableTaskFactory.RunAsync(() => _refresh.RefreshAsync(interactive: false, DisposalToken)).FileAndForget("SqlLense/AutoRefresh");
            }
        }
    }
}
