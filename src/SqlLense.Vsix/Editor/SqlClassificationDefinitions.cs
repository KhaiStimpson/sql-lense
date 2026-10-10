using System.ComponentModel.Composition;
using System.Windows.Media;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;
using SqlLense.Sql;

namespace SqlLense.Vsix.Editor
{
    /// <summary>
    /// Classification types for SQL tokens inside C# strings. They are listed under
    /// Tools &gt; Options &gt; Environment &gt; Fonts and Colors as "SqlLense - ...". Defaults are mid-tone
    /// colors that stay readable on both light and dark themes. SQL string literals and operators
    /// are left unclassified so they keep the C# string color.
    /// </summary>
    internal static class SqlClassificationDefinitions
    {
        public const string Keyword = "SqlLense.Keyword";
        public const string Identifier = "SqlLense.Identifier";
        public const string Function = "SqlLense.Function";
        public const string Variable = "SqlLense.Variable";
        public const string Number = "SqlLense.Number";
        public const string Comment = "SqlLense.Comment";

        /// <summary>The Fonts and Colors item for a token class, or null for classes SqlLense leaves uncolored.</summary>
        public static string? NameOf(SqlTokenClass tokenClass) => tokenClass switch
        {
            SqlTokenClass.Keyword => Keyword,
            SqlTokenClass.Identifier => Identifier,
            SqlTokenClass.Function => Function,
            SqlTokenClass.Variable => Variable,
            SqlTokenClass.Number => Number,
            SqlTokenClass.Comment => Comment,
            _ => null,
        };

#pragma warning disable CS0649 // Assigned by MEF.
        [Export, Name(Keyword), BaseDefinition(PredefinedClassificationTypeNames.FormalLanguage)]
        internal static ClassificationTypeDefinition? KeywordType;

        [Export, Name(Identifier), BaseDefinition(PredefinedClassificationTypeNames.FormalLanguage)]
        internal static ClassificationTypeDefinition? IdentifierType;

        [Export, Name(Function), BaseDefinition(PredefinedClassificationTypeNames.FormalLanguage)]
        internal static ClassificationTypeDefinition? FunctionType;

        [Export, Name(Variable), BaseDefinition(PredefinedClassificationTypeNames.FormalLanguage)]
        internal static ClassificationTypeDefinition? VariableType;

        [Export, Name(Number), BaseDefinition(PredefinedClassificationTypeNames.FormalLanguage)]
        internal static ClassificationTypeDefinition? NumberType;

        [Export, Name(Comment), BaseDefinition(PredefinedClassificationTypeNames.FormalLanguage)]
        internal static ClassificationTypeDefinition? CommentType;
#pragma warning restore CS0649
    }

    internal abstract class SqlFormatDefinition : ClassificationFormatDefinition
    {
        /// <summary>Defaults come from the "SqlLense default" preset, which suits light and dark themes alike.</summary>
        protected SqlFormatDefinition(string displayName, SqlTokenClass tokenClass, bool italic = false)
        {
            var style = SqlColorPresets.GetScheme(SqlColorPreset.SqlLense, darkBackground: false)![tokenClass];
            DisplayName = "SqlLense - SQL " + displayName;
            ForegroundColor = Color.FromRgb(style.Foreground.R, style.Foreground.G, style.Foreground.B);
            IsBold = style.Bold ? true : null;
            IsItalic = italic ? true : null;
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = SqlClassificationDefinitions.Keyword)]
    [Name(SqlClassificationDefinitions.Keyword)]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class SqlKeywordFormat : SqlFormatDefinition
    {
        public SqlKeywordFormat() : base("Keyword", SqlTokenClass.Keyword) { }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = SqlClassificationDefinitions.Identifier)]
    [Name(SqlClassificationDefinitions.Identifier)]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class SqlIdentifierFormat : SqlFormatDefinition
    {
        public SqlIdentifierFormat() : base("Identifier", SqlTokenClass.Identifier) { }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = SqlClassificationDefinitions.Function)]
    [Name(SqlClassificationDefinitions.Function)]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class SqlFunctionFormat : SqlFormatDefinition
    {
        public SqlFunctionFormat() : base("Function", SqlTokenClass.Function) { }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = SqlClassificationDefinitions.Variable)]
    [Name(SqlClassificationDefinitions.Variable)]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class SqlVariableFormat : SqlFormatDefinition
    {
        public SqlVariableFormat() : base("Variable", SqlTokenClass.Variable, italic: true) { }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = SqlClassificationDefinitions.Number)]
    [Name(SqlClassificationDefinitions.Number)]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class SqlNumberFormat : SqlFormatDefinition
    {
        public SqlNumberFormat() : base("Number", SqlTokenClass.Number) { }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = SqlClassificationDefinitions.Comment)]
    [Name(SqlClassificationDefinitions.Comment)]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class SqlCommentFormat : SqlFormatDefinition
    {
        public SqlCommentFormat() : base("Comment", SqlTokenClass.Comment, italic: true) { }
    }
}
