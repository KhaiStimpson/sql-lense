using System;
using System.Collections.Generic;
using SqlLense.Schema;

namespace SqlLense.Sql
{
    public enum SqlDiagnosticKind
    {
        SyntaxError,
        InvalidObject,
        InvalidColumn,
        UnboundIdentifier,
        AmbiguousColumn,
        UnknownProcedure,
        UnknownParameter,
    }

    /// <summary>A problem found in a SQL text. Offsets are relative to the SQL text, not the C# file.</summary>
    public sealed class SqlDiagnostic
    {
        public SqlDiagnostic(SqlDiagnosticKind kind, int start, int length, string message, string? name = null, IReadOnlyList<string>? suggestions = null)
        {
            Kind = kind;
            Start = start;
            Length = length;
            Message = message;
            Name = name;
            Suggestions = suggestions ?? Array.Empty<string>();
        }

        public SqlDiagnosticKind Kind { get; }

        public int Start { get; }

        public int Length { get; }

        public string Message { get; }

        /// <summary>The offending name as written (without brackets), when applicable.</summary>
        public string? Name { get; }

        /// <summary>"Did you mean" candidates, best first.</summary>
        public IReadOnlyList<string> Suggestions { get; }

        public override string ToString() => $"{Kind}@{Start}+{Length}: {Message}";
    }

    public enum SqlSymbolKind
    {
        Table,
        Column,
        Procedure,
        Function,
    }

    /// <summary>A resolved reference to a schema element, used for Quick Info.</summary>
    public sealed class SqlSymbol
    {
        public SqlSymbol(SqlSymbolKind kind, int start, int length, SchemaObject obj, SchemaColumn? column = null)
        {
            Kind = kind;
            Start = start;
            Length = length;
            Object = obj;
            Column = column;
        }

        public SqlSymbolKind Kind { get; }

        public int Start { get; }

        public int Length { get; }

        public SchemaObject Object { get; }

        public SchemaColumn? Column { get; }
    }

    public sealed class SqlAnalysisResult
    {
        public static readonly SqlAnalysisResult Empty =
            new SqlAnalysisResult(Array.Empty<SqlDiagnostic>(), Array.Empty<SqlSymbol>());

        public SqlAnalysisResult(IReadOnlyList<SqlDiagnostic> diagnostics, IReadOnlyList<SqlSymbol> symbols)
        {
            Diagnostics = diagnostics;
            Symbols = symbols;
        }

        public IReadOnlyList<SqlDiagnostic> Diagnostics { get; }

        /// <summary>Resolved references sorted by <see cref="SqlSymbol.Start"/>.</summary>
        public IReadOnlyList<SqlSymbol> Symbols { get; }

        public SqlSymbol? FindSymbolAt(int offset)
        {
            int lo = 0, hi = Symbols.Count - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >> 1;
                var s = Symbols[mid];
                if (offset < s.Start)
                {
                    hi = mid - 1;
                }
                else if (offset > s.Start + s.Length)
                {
                    lo = mid + 1;
                }
                else
                {
                    return s;
                }
            }

            return null;
        }
    }
}
