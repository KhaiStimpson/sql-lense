using System.ComponentModel.Composition;
using System.Windows.Media;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;

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
        protected SqlFormatDefinition(string displayName, Color foreground, bool bold = false, bool italic = false)
        {
            DisplayName = "SqlLense - SQL " + displayName;
            ForegroundColor = foreground;
            IsBold = bold ? true : null;
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
        public SqlKeywordFormat() : base("Keyword", Color.FromRgb(0x3E, 0x8E, 0xDE), bold: true) { }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = SqlClassificationDefinitions.Identifier)]
    [Name(SqlClassificationDefinitions.Identifier)]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class SqlIdentifierFormat : SqlFormatDefinition
    {
        public SqlIdentifierFormat() : base("Identifier", Color.FromRgb(0x1F, 0xA0, 0x9A)) { }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = SqlClassificationDefinitions.Function)]
    [Name(SqlClassificationDefinitions.Function)]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class SqlFunctionFormat : SqlFormatDefinition
    {
        public SqlFunctionFormat() : base("Function", Color.FromRgb(0xB0, 0x6A, 0xC8)) { }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = SqlClassificationDefinitions.Variable)]
    [Name(SqlClassificationDefinitions.Variable)]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class SqlVariableFormat : SqlFormatDefinition
    {
        public SqlVariableFormat() : base("Variable", Color.FromRgb(0xC7, 0x7C, 0x1E), italic: true) { }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = SqlClassificationDefinitions.Number)]
    [Name(SqlClassificationDefinitions.Number)]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class SqlNumberFormat : SqlFormatDefinition
    {
        public SqlNumberFormat() : base("Number", Color.FromRgb(0x3A, 0x9A, 0x5C)) { }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = SqlClassificationDefinitions.Comment)]
    [Name(SqlClassificationDefinitions.Comment)]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class SqlCommentFormat : SqlFormatDefinition
    {
        public SqlCommentFormat() : base("Comment", Color.FromRgb(0x6A, 0x8F, 0x5A), italic: true) { }
    }
}
