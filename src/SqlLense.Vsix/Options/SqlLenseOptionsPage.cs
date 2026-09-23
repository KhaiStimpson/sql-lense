using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using SqlLense.Schema;

namespace SqlLense.Vsix.Options
{
    /// <summary>
    /// Tools &gt; Options &gt; SqlLense &gt; General. Connection settings apply to the solution that is
    /// currently open and are stored per user (never in the repository).
    /// </summary>
    [ComVisible(true)]
    [Guid("5f0b8d3e-6c2a-4c1f-9a51-2c6f7c3e9b10")]
    public sealed class SqlLenseOptionsPage : DialogPage
    {
        private const string ConnectionCategory = "Connection (current solution)";
        private const string EditorCategory = "Editor";

        [Category(ConnectionCategory)]
        [DisplayName("Solution folder")]
        [Description("Connection settings below apply to this solution only.")]
        [ReadOnly(true)]
        public string SolutionFolder { get; private set; } = "(no solution open)";

        [Category(ConnectionCategory)]
        [DisplayName("Connection string")]
        [Description("SQL Server connection string used by 'Refresh Database Schema', e.g. " +
                     "Server=localhost;Database=Shop;Integrated Security=true;TrustServerCertificate=true. " +
                     "Stored encrypted for your Windows account.")]
        public string ConnectionString { get; set; } = string.Empty;

        [Category(ConnectionCategory)]
        [DisplayName("Password")]
        [Description("Optional SQL authentication password, applied on top of the connection string. Stored encrypted.")]
        [PasswordPropertyText(true)]
        public string Password { get; set; } = string.Empty;

        [Category(ConnectionCategory)]
        [DisplayName("Database profile")]
        [Description("Snapshot name. Leave as 'default' unless projects select a database with the SqlLenseDatabase MSBuild property.")]
        public string DatabaseProfile { get; set; } = SchemaLocator.DefaultDatabaseName;

        [Category(ConnectionCategory)]
        [DisplayName("Refresh schema when the solution opens")]
        [Description("Re-read the database schema in the background every time this solution is opened.")]
        public bool RefreshOnSolutionOpen { get; set; }

        [Category(ConnectionCategory)]
        [DisplayName("Snapshot file")]
        [Description("Where the cached schema for this solution is stored.")]
        [ReadOnly(true)]
        public string SnapshotPath { get; private set; } = string.Empty;

        [Category(EditorCategory)]
        [DisplayName("SQL syntax highlighting")]
        [Description("Color SQL keywords, identifiers, variables and comments inside C# strings.")]
        public bool EnableHighlighting { get; set; } = true;

        [Category(EditorCategory)]
        [DisplayName("SQL Quick Info")]
        [Description("Show table, column and procedure details when hovering SQL inside C# strings.")]
        public bool EnableQuickInfo { get; set; } = true;

        public override void LoadSettingsFromStorage()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            SqlLenseSettings.LoadEditorSettings();
            EnableHighlighting = SqlLenseSettings.HighlightingEnabled;
            EnableQuickInfo = SqlLenseSettings.QuickInfoEnabled;

            var solutionDir = SolutionInfo.GetSolutionDirectory();
            if (solutionDir == null)
            {
                SolutionFolder = "(no solution open)";
                SnapshotPath = string.Empty;
                ConnectionString = Password = string.Empty;
                DatabaseProfile = SchemaLocator.DefaultDatabaseName;
                RefreshOnSolutionOpen = false;
                return;
            }

            var settings = SqlLenseSettings.Load(solutionDir);
            SolutionFolder = solutionDir;
            ConnectionString = settings.ConnectionString;
            Password = settings.Password;
            DatabaseProfile = settings.DatabaseProfile;
            RefreshOnSolutionOpen = settings.RefreshOnSolutionOpen;
            SnapshotPath = SchemaLocator.GetSnapshotPath(null, solutionDir, settings.DatabaseProfile) ?? string.Empty;
        }

        public override void SaveSettingsToStorage()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            SqlLenseSettings.SaveEditorSettings(EnableHighlighting, EnableQuickInfo);

            var solutionDir = SolutionInfo.GetSolutionDirectory();
            if (solutionDir == null)
            {
                return;
            }

            SqlLenseSettings.Save(solutionDir, new SolutionSettings
            {
                ConnectionString = ConnectionString?.Trim() ?? string.Empty,
                Password = Password ?? string.Empty,
                DatabaseProfile = DatabaseProfile,
                RefreshOnSolutionOpen = RefreshOnSolutionOpen,
            });
            SnapshotPath = SchemaLocator.GetSnapshotPath(null, solutionDir, DatabaseProfile) ?? string.Empty;
        }
    }
}
