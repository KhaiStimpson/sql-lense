using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Settings;
using SqlLense.Schema;
using SqlLense.Sql;

namespace SqlLense.Vsix.Options
{
    /// <summary>Connection settings for one solution.</summary>
    internal sealed class SolutionSettings
    {
        public string ConnectionString { get; set; } = string.Empty;

        /// <summary>Kept apart from the connection string so it can be masked in the UI.</summary>
        public string Password { get; set; } = string.Empty;

        /// <summary>Snapshot name; matches the <c>SqlLenseDatabase</c> MSBuild property. "default" for single-database solutions.</summary>
        public string DatabaseProfile { get; set; } = SchemaLocator.DefaultDatabaseName;

        public bool RefreshOnSolutionOpen { get; set; }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);
    }

    /// <summary>Editor settings, shared by every solution.</summary>
    internal sealed class EditorSettings
    {
        public bool Highlighting { get; set; } = true;

        public bool QuickInfo { get; set; } = true;

        public bool AliasColoring { get; set; } = true;

        public bool AliasDeclarations { get; set; } = true;

        public bool AliasReferences { get; set; } = true;

        public bool AliasedTableNames { get; set; } = true;

        public bool ColumnAndCteAliases { get; set; } = true;

        /// <summary>The preset most recently written to Fonts and Colors.</summary>
        public SqlColorPreset LastAppliedPreset { get; set; } = SqlColorPreset.Custom;

        public SqlAliasParts AliasParts => !AliasColoring ? SqlAliasParts.None :
            (AliasDeclarations ? SqlAliasParts.Declarations : 0) |
            (AliasReferences ? SqlAliasParts.References : 0) |
            (AliasedTableNames ? SqlAliasParts.AliasedObjects : 0) |
            (ColumnAndCteAliases ? SqlAliasParts.ColumnAndCteAliases : 0);
    }

    /// <summary>
    /// Per-user settings in the Visual Studio settings store. Connection settings are keyed by solution
    /// folder (the same key the snapshot uses), and secrets are encrypted with DPAPI for the current
    /// Windows user. Nothing is written to the repository.
    /// </summary>
    internal static class SqlLenseSettings
    {
        private const string Root = "SqlLense";
        private const string EditorCollection = Root + "\\Editor";
        private static readonly byte[] s_entropy = Encoding.UTF8.GetBytes("SqlLense.ConnectionSettings.v1");

        private static volatile EditorSettings s_editor = new EditorSettings();

        /// <summary>Read by editor components on any thread; refreshed when the options page is saved.</summary>
        public static bool HighlightingEnabled => s_editor.Highlighting;

        public static bool QuickInfoEnabled => s_editor.QuickInfo;

        public static SqlAliasParts AliasParts => s_editor.AliasParts;

        public static SqlColorPreset LastAppliedPreset => s_editor.LastAppliedPreset;

        public static EditorSettings LoadEditorSettings()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var store = GetStore();
            var settings = new EditorSettings
            {
                Highlighting = store.GetBoolean(EditorCollection, "Highlighting", true),
                QuickInfo = store.GetBoolean(EditorCollection, "QuickInfo", true),
                AliasColoring = store.GetBoolean(EditorCollection, "AliasColoring", true),
                AliasDeclarations = store.GetBoolean(EditorCollection, "AliasDeclarations", true),
                AliasReferences = store.GetBoolean(EditorCollection, "AliasReferences", true),
                AliasedTableNames = store.GetBoolean(EditorCollection, "AliasedTableNames", true),
                ColumnAndCteAliases = store.GetBoolean(EditorCollection, "ColumnAndCteAliases", true),
                LastAppliedPreset = Enum.TryParse<SqlColorPreset>(store.GetString(EditorCollection, "LastAppliedPreset", string.Empty), out var preset)
                    ? preset
                    : SqlColorPreset.Custom,
            };
            s_editor = settings;
            return settings;
        }

        public static void SaveEditorSettings(EditorSettings settings)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var store = GetStore();
            EnsureCollection(store, EditorCollection);
            store.SetBoolean(EditorCollection, "Highlighting", settings.Highlighting);
            store.SetBoolean(EditorCollection, "QuickInfo", settings.QuickInfo);
            store.SetBoolean(EditorCollection, "AliasColoring", settings.AliasColoring);
            store.SetBoolean(EditorCollection, "AliasDeclarations", settings.AliasDeclarations);
            store.SetBoolean(EditorCollection, "AliasReferences", settings.AliasReferences);
            store.SetBoolean(EditorCollection, "AliasedTableNames", settings.AliasedTableNames);
            store.SetBoolean(EditorCollection, "ColumnAndCteAliases", settings.ColumnAndCteAliases);
            store.SetString(EditorCollection, "LastAppliedPreset", settings.LastAppliedPreset.ToString());
            s_editor = settings;
        }

        public static SolutionSettings Load(string solutionDir)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var store = GetStore();
            var collection = SolutionCollection(solutionDir);
            if (!store.CollectionExists(collection))
            {
                return new SolutionSettings();
            }

            return new SolutionSettings
            {
                ConnectionString = Unprotect(store.GetString(collection, "ConnectionString", string.Empty)),
                Password = Unprotect(store.GetString(collection, "Password", string.Empty)),
                DatabaseProfile = store.GetString(collection, "DatabaseProfile", SchemaLocator.DefaultDatabaseName),
                RefreshOnSolutionOpen = store.GetBoolean(collection, "RefreshOnSolutionOpen", false),
            };
        }

        public static void Save(string solutionDir, SolutionSettings settings)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var store = GetStore();
            var collection = SolutionCollection(solutionDir);
            EnsureCollection(store, collection);
            store.SetString(collection, "SolutionDir", solutionDir);
            store.SetString(collection, "ConnectionString", Protect(settings.ConnectionString));
            store.SetString(collection, "Password", Protect(settings.Password));
            store.SetString(collection, "DatabaseProfile", SchemaLocator.NormalizeDatabaseName(settings.DatabaseProfile));
            store.SetBoolean(collection, "RefreshOnSolutionOpen", settings.RefreshOnSolutionOpen);
        }

        private static string SolutionCollection(string solutionDir) =>
            Root + "\\Solutions\\" + SchemaLocator.GetSolutionKey(solutionDir);

        private static WritableSettingsStore GetStore() =>
            new ShellSettingsManager(ServiceProvider.GlobalProvider).GetWritableSettingsStore(SettingsScope.UserSettings);

        private static void EnsureCollection(WritableSettingsStore store, string collection)
        {
            if (!store.CollectionExists(collection))
            {
                store.CreateCollection(collection);
            }
        }

        private static string Protect(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), s_entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }

        private static string Unprotect(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), s_entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                // Written by another Windows user or corrupted: treat as not configured.
                return string.Empty;
            }
        }
    }
}
