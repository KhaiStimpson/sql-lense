using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using SqlLense.Schema;
using SqlLense.Sql;

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
        private const string AliasCategory = "Editor: alias colors";
        private const string ThemeCategory = "Editor: color theme";

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

        [Category(AliasCategory)]
        [DisplayName("Color aliases")]
        [Description("Give every alias in a query its own color. Colors are generated automatically, never repeat " +
                     "within a query, and adapt to light and dark themes. Requires SQL syntax highlighting.")]
        public bool AliasColoring { get; set; } = true;

        [Category(AliasCategory)]
        [DisplayName("Alias declarations")]
        [Description("Color the alias where it is introduced: 'c' in FROM Customers c.")]
        public bool AliasDeclarations { get; set; } = true;

        [Category(AliasCategory)]
        [DisplayName("Alias references")]
        [Description("Color uses of an alias: 'c' in c.Name, ORDER BY Total, FROM RecentOrders.")]
        public bool AliasReferences { get; set; } = true;

        [Category(AliasCategory)]
        [DisplayName("Aliased table names")]
        [Description("Color the table, view or function an alias stands for in its alias's color: 'Customers' in FROM Customers c.")]
        public bool AliasedTableNames { get; set; } = true;

        [Category(AliasCategory)]
        [DisplayName("Column aliases and CTE names")]
        [Description("Also color column aliases (SUM(x) AS Total) and common table expression names (WITH Recent AS ...). " +
                     "When off, only table aliases are colored.")]
        public bool ColumnAndCteAliases { get; set; } = true;

        [Category(ThemeCategory)]
        [DisplayName("Apply color preset")]
        [Description("Pick a preset and press OK to write its colors into the 'SqlLense - SQL ...' entries under " +
                     "Environment > Fonts and Colors, where you can still change single colors. The light or dark " +
                     "variant is chosen for the current theme; apply it again after switching theme.")]
        [TypeConverter(typeof(EnumDescriptionConverter<SqlColorPreset>))]
        public SqlColorPreset ColorPreset { get; set; } = SqlColorPreset.Custom;

        [Category(ThemeCategory)]
        [DisplayName("Last applied preset")]
        [Description("The preset most recently written to Fonts and Colors.")]
        [ReadOnly(true)]
        public string LastAppliedPreset { get; private set; } = string.Empty;

        public override void LoadSettingsFromStorage()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var editor = SqlLenseSettings.LoadEditorSettings();
            EnableHighlighting = editor.Highlighting;
            EnableQuickInfo = editor.QuickInfo;
            AliasColoring = editor.AliasColoring;
            AliasDeclarations = editor.AliasDeclarations;
            AliasReferences = editor.AliasReferences;
            AliasedTableNames = editor.AliasedTableNames;
            ColumnAndCteAliases = editor.ColumnAndCteAliases;

            // The picker always opens on "Custom" so choosing any preset, even the last one, applies it.
            ColorPreset = SqlColorPreset.Custom;
            LastAppliedPreset = editor.LastAppliedPreset == SqlColorPreset.Custom
                ? "(none)"
                : EnumDescriptionConverter<SqlColorPreset>.Describe(editor.LastAppliedPreset);

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
            var lastApplied = SqlLenseSettings.LastAppliedPreset;
            if (ColorPreset != SqlColorPreset.Custom)
            {
                SqlColorPresetApplier.Apply(ColorPreset);
                lastApplied = ColorPreset;
                LastAppliedPreset = EnumDescriptionConverter<SqlColorPreset>.Describe(ColorPreset);
                ColorPreset = SqlColorPreset.Custom;
            }

            SqlLenseSettings.SaveEditorSettings(new EditorSettings
            {
                Highlighting = EnableHighlighting,
                QuickInfo = EnableQuickInfo,
                AliasColoring = AliasColoring,
                AliasDeclarations = AliasDeclarations,
                AliasReferences = AliasReferences,
                AliasedTableNames = AliasedTableNames,
                ColumnAndCteAliases = ColumnAndCteAliases,
                LastAppliedPreset = lastApplied,
            });

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
