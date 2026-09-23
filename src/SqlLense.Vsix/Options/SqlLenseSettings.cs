using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Settings;
using SqlLense.Schema;

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

        private static volatile bool s_highlightingEnabled = true;
        private static volatile bool s_quickInfoEnabled = true;

        /// <summary>Read by editor components on any thread; refreshed when the options page is saved.</summary>
        public static bool HighlightingEnabled => s_highlightingEnabled;

        public static bool QuickInfoEnabled => s_quickInfoEnabled;

        public static void LoadEditorSettings()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var store = GetStore();
            s_highlightingEnabled = store.GetBoolean(EditorCollection, "Highlighting", true);
            s_quickInfoEnabled = store.GetBoolean(EditorCollection, "QuickInfo", true);
        }

        public static void SaveEditorSettings(bool highlighting, bool quickInfo)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var store = GetStore();
            EnsureCollection(store, EditorCollection);
            store.SetBoolean(EditorCollection, "Highlighting", highlighting);
            store.SetBoolean(EditorCollection, "QuickInfo", quickInfo);
            s_highlightingEnabled = highlighting;
            s_quickInfoEnabled = quickInfo;
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
