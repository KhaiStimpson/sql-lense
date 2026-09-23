using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using SqlLense.Sql;

namespace SqlLense.Analyzers
{
    public static class Descriptors
    {
        public const string Category = "SqlLense";

        public const string SyntaxErrorId = "SQLL001";
        public const string InvalidObjectId = "SQLL002";
        public const string InvalidColumnId = "SQLL003";
        public const string UnboundIdentifierId = "SQLL004";
        public const string AmbiguousColumnId = "SQLL005";
        public const string UnknownProcedureId = "SQLL006";
        public const string UnknownParameterId = "SQLL007";

        /// <summary>Diagnostic property: '|'-separated "did you mean" candidates, best first.</summary>
        public const string SuggestionsProperty = "SqlLense.Suggestions";

        /// <summary>Diagnostic property: the offending name as written in SQL (without brackets).</summary>
        public const string NameProperty = "SqlLense.Name";

        private const string HelpLink = "https://github.com/KhaiStimpson/sql-lense#diagnostics";

        public static readonly DiagnosticDescriptor SyntaxError = Create(
            SyntaxErrorId, "SQL syntax error", "SQL: {0}", "The SQL text in this string does not parse as T-SQL.");

        public static readonly DiagnosticDescriptor InvalidObject = Create(
            InvalidObjectId, "Unknown SQL object", "SQL: {0}", "The table, view or function does not exist in the schema snapshot.");

        public static readonly DiagnosticDescriptor InvalidColumn = Create(
            InvalidColumnId, "Unknown SQL column", "SQL: {0}", "The column does not exist on any table in scope.");

        public static readonly DiagnosticDescriptor UnboundIdentifier = Create(
            UnboundIdentifierId, "Unknown SQL table alias", "SQL: {0}", "The qualifier does not match any table or alias in scope.");

        public static readonly DiagnosticDescriptor AmbiguousColumn = Create(
            AmbiguousColumnId, "Ambiguous SQL column", "SQL: {0}", "The column exists on more than one table in scope; qualify it.");

        public static readonly DiagnosticDescriptor UnknownProcedure = Create(
            UnknownProcedureId, "Unknown stored procedure", "SQL: {0}", "The stored procedure does not exist in the schema snapshot.");

        public static readonly DiagnosticDescriptor UnknownParameter = Create(
            UnknownParameterId, "Unknown stored procedure parameter", "SQL: {0}", "The stored procedure has no parameter with this name.");

        public static readonly ImmutableArray<DiagnosticDescriptor> All = ImmutableArray.Create(
            SyntaxError, InvalidObject, InvalidColumn, UnboundIdentifier, AmbiguousColumn, UnknownProcedure, UnknownParameter);

        public static readonly ImmutableArray<string> SchemaDiagnosticIds = ImmutableArray.Create(
            InvalidObjectId, InvalidColumnId, UnboundIdentifierId, UnknownProcedureId, UnknownParameterId);

        public static DiagnosticDescriptor For(SqlDiagnosticKind kind) => kind switch
        {
            SqlDiagnosticKind.SyntaxError => SyntaxError,
            SqlDiagnosticKind.InvalidObject => InvalidObject,
            SqlDiagnosticKind.InvalidColumn => InvalidColumn,
            SqlDiagnosticKind.UnboundIdentifier => UnboundIdentifier,
            SqlDiagnosticKind.AmbiguousColumn => AmbiguousColumn,
            SqlDiagnosticKind.UnknownProcedure => UnknownProcedure,
            _ => UnknownParameter,
        };

        private static DiagnosticDescriptor Create(string id, string title, string format, string description) =>
            new DiagnosticDescriptor(id, title, format, Category, DiagnosticSeverity.Error, isEnabledByDefault: true, description, HelpLink);
    }
}
