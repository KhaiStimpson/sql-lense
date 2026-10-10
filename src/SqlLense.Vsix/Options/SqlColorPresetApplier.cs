using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.TextManager.Interop;
using SqlLense.Sql;
using SqlLense.Vsix.Editor;

namespace SqlLense.Vsix.Options
{
    /// <summary>
    /// Writes a preset into the "SqlLense - SQL ..." items under Tools &gt; Options &gt; Environment &gt;
    /// Fonts and Colors, so users can still adjust single colors afterwards. The light or dark variant is
    /// chosen from the current editor background.
    /// </summary>
    internal static class SqlColorPresetApplier
    {
        // Fonts and Colors category "Text Editor".
        private static readonly Guid s_textEditorCategory = new Guid("A27B4E24-A735-4D1D-B8E7-9716E1E3D8E0");

        public static void Apply(SqlColorPreset preset)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var componentModel = (IComponentModel)ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel));
            var editorFormatMap = componentModel.GetService<IEditorFormatMapService>().GetEditorFormatMap("text");
            var scheme = SqlColorPresets.GetScheme(preset, SqlAliasClassifications.GetBackground(editorFormatMap).IsDark);
            if (scheme == null)
            {
                return;
            }

            var storage = (IVsFontAndColorStorage)ServiceProvider.GlobalProvider.GetService(typeof(SVsFontAndColorStorage));
            var category = s_textEditorCategory;
            ErrorHandler.ThrowOnFailure(storage.OpenCategory(
                ref category,
                (uint)(__FCSTORAGEFLAGS.FCSF_LOADDEFAULTS | __FCSTORAGEFLAGS.FCSF_PROPAGATECHANGES)));
            try
            {
                foreach (var entry in scheme)
                {
                    var name = SqlClassificationDefinitions.NameOf(entry.Key);
                    if (name == null)
                    {
                        continue;
                    }

                    // Start from the stored item so its background and other flags are kept.
                    var item = new ColorableItemInfo[1];
                    if (storage.GetItem(name, item) != VSConstants.S_OK)
                    {
                        item[0] = new ColorableItemInfo();
                    }

                    var color = entry.Value.Foreground;
                    item[0].bForegroundValid = 1;
                    item[0].crForeground = (uint)(color.R | (color.G << 8) | (color.B << 16));
                    item[0].bFontFlagsValid = 1;
                    item[0].dwFontFlags = entry.Value.Bold
                        ? item[0].dwFontFlags | (uint)FONTFLAGS.FF_BOLD
                        : item[0].dwFontFlags & ~(uint)FONTFLAGS.FF_BOLD;
                    ErrorHandler.ThrowOnFailure(storage.SetItem(name, item));
                }
            }
            finally
            {
                storage.CloseCategory();
            }
        }
    }
}
