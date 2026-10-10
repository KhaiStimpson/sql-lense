using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using SqlLense.Analyzers.CSharp;
using SqlLense.Schema;
using SqlLense.Sql;

namespace SqlLense.CodeFixes
{
    /// <summary>All SQL strings in one document, sorted by position.</summary>
    public sealed class SqlDocumentRegions
    {
        public static readonly SqlDocumentRegions Empty = new SqlDocumentRegions(new List<SqlStringCandidate>());

        internal SqlDocumentRegions(List<SqlStringCandidate> candidates)
        {
            // A string built up over several statements can start before (and enclose) other SQL strings.
            candidates.Sort((a, b) => a.Span.Start.CompareTo(b.Span.Start));
            Candidates = candidates;
        }

        public IReadOnlyList<SqlStringCandidate> Candidates { get; }

        public SqlStringCandidate? FindAt(int position)
        {
            SqlStringCandidate? enclosing = null;
            foreach (var candidate in Candidates)
            {
                var span = candidate.Span;
                if (position < span.Start)
                {
                    break;
                }

                if (position <= span.End)
                {
                    if (candidate.Map.MapToSql(position) >= 0)
                    {
                        return candidate;
                    }

                    enclosing ??= candidate;
                }
            }

            return enclosing;
        }
    }

    public readonly struct SqlClassifiedSourceSpan
    {
        public SqlClassifiedSourceSpan(TextSpan span, SqlTokenClass tokenClass)
        {
            Span = span;
            Class = tokenClass;
        }

        public TextSpan Span { get; }

        public SqlTokenClass Class { get; }
    }

    public readonly struct SqlAliasSourceSpan
    {
        public SqlAliasSourceSpan(TextSpan span, int colorIndex)
        {
            Span = span;
            ColorIndex = colorIndex;
        }

        public TextSpan Span { get; }

        /// <summary>Index of the alias within its query; see <see cref="SqlAliasResolver"/>.</summary>
        public int ColorIndex { get; }
    }

    public sealed class SqlQuickInfo
    {
        public SqlQuickInfo(TextSpan span, SqlSymbol symbol)
        {
            Span = span;
            Symbol = symbol;
        }

        public TextSpan Span { get; }

        public SqlSymbol Symbol { get; }

        /// <summary>First line: signature. Following lines: columns or parameters.</summary>
        public string Description => SqlEditorServices.Describe(Symbol);
    }

    /// <summary>Editor-facing services (highlighting, Quick Info) shared by the VSIX and tests.</summary>
    public static class SqlEditorServices
    {
        private const int MaxListedMembers = 25;

        /// <summary>Finds every SQL string in a document. Purely syntactic, so it is safe to run per edit.</summary>
        public static SqlDocumentRegions ComputeRegions(SyntaxNode root, CancellationToken cancellationToken)
        {
            List<SqlStringCandidate>? candidates = null;
            foreach (var node in root.DescendantNodes())
            {
                if (!(node is ExpressionSyntax expression) ||
                    !(node.IsKind(SyntaxKind.StringLiteralExpression) || node.IsKind(SyntaxKind.InterpolatedStringExpression) || node.IsKind(SyntaxKind.AddExpression)) ||
                    !SqlStringExtractor.IsRootStringExpression(expression))
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var candidate = SqlStringExtractor.TryExtract(expression, semanticModel: null, SqlDetectionMode.Diagnostics, cancellationToken);
                if (candidate != null)
                {
                    (candidates ??= new List<SqlStringCandidate>()).Add(candidate);
                }
            }

            return candidates == null ? SqlDocumentRegions.Empty : new SqlDocumentRegions(candidates);
        }

        /// <summary>Classified SQL tokens intersecting <paramref name="range"/>, sorted by position.</summary>
        public static IEnumerable<SqlClassifiedSourceSpan> Classify(SqlDocumentRegions regions, TextSpan range)
        {
            var result = new List<SqlClassifiedSourceSpan>();
            foreach (var candidate in regions.Candidates)
            {
                if (candidate.Span.Start > range.End)
                {
                    break;
                }

                if (candidate.Span.End < range.Start)
                {
                    continue;
                }

                foreach (var token in SqlClassifier.Classify(candidate.Sql))
                {
                    if (candidate.Map.TryMapWithinLiteral(token.Start, token.Length, out var span) && span.IntersectsWith(range))
                    {
                        result.Add(new SqlClassifiedSourceSpan(span, token.Class));
                    }
                }
            }

            // Pieces of different strings can interleave (a built-up string around another SQL string).
            result.Sort((a, b) => a.Span.Start.CompareTo(b.Span.Start));
            return result;
        }

        /// <summary>Alias spans in <paramref name="range"/>, sorted by position. Colors restart at 0 for each SQL string.</summary>
        public static IEnumerable<SqlAliasSourceSpan> ClassifyAliases(SqlDocumentRegions regions, TextSpan range, SqlAliasParts parts)
        {
            var result = new List<SqlAliasSourceSpan>();
            if (parts == SqlAliasParts.None)
            {
                return result;
            }

            foreach (var candidate in regions.Candidates)
            {
                if (candidate.Span.Start > range.End)
                {
                    break;
                }

                if (candidate.Span.End < range.Start)
                {
                    continue;
                }

                foreach (var alias in SqlAliasResolver.Classify(candidate.Sql, parts))
                {
                    if (candidate.Map.TryMapWithinLiteral(alias.Start, alias.Length, out var span) && span.IntersectsWith(range))
                    {
                        result.Add(new SqlAliasSourceSpan(span, alias.ColorIndex));
                    }
                }
            }

            result.Sort((a, b) => a.Span.Start.CompareTo(b.Span.Start));
            return result;
        }

        public static SqlQuickInfo? GetQuickInfo(SqlDocumentRegions regions, int position, DatabaseSchema schema)
        {
            var candidate = regions.FindAt(position);
            if (candidate == null)
            {
                return null;
            }

            var offset = candidate.Map.MapToSql(position);
            if (offset < 0)
            {
                return null;
            }

            var symbol = SqlCompletionEngine.FindSymbol(candidate.Sql, offset, schema);
            if (symbol == null)
            {
                return null;
            }

            return new SqlQuickInfo(candidate.Map.MapSpan(symbol.Start, symbol.Length), symbol);
        }

        public static async Task<SqlQuickInfo?> GetQuickInfoAsync(Document document, int position, CancellationToken cancellationToken)
        {
            var schema = WorkspaceSchema.GetSchema(document);
            var root = schema == null ? null : await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            return root == null ? null : GetQuickInfo(ComputeRegions(root, cancellationToken), position, schema!);
        }

        public static string Describe(SqlSymbol symbol)
        {
            var obj = symbol.Object;
            var sb = new StringBuilder();
            switch (symbol.Kind)
            {
                case SqlSymbolKind.Column when symbol.Column != null:
                    sb.Append("(column) ").Append(obj.QualifiedName).Append('.').Append(symbol.Column.Name)
                      .Append(' ').Append(symbol.Column.DataType).Append(symbol.Column.IsNullable ? " NULL" : " NOT NULL");
                    return sb.ToString();

                case SqlSymbolKind.Procedure:
                    sb.Append("(procedure) ").Append(obj.QualifiedName);
                    foreach (var parameter in obj.Parameters.Take(MaxListedMembers))
                    {
                        sb.Append('\n').Append("  ").Append(parameter.Name).Append(' ').Append(parameter.DataType)
                          .Append(parameter.IsOutput ? " OUTPUT" : string.Empty);
                    }

                    break;

                default:
                    sb.Append('(').Append(KindName(obj.Kind)).Append(") ").Append(obj.QualifiedName);
                    foreach (var column in obj.Columns.Take(MaxListedMembers))
                    {
                        sb.Append('\n').Append("  ").Append(column.Name).Append(' ').Append(column.DataType)
                          .Append(column.IsNullable ? " NULL" : " NOT NULL");
                    }

                    break;
            }

            var members = symbol.Kind == SqlSymbolKind.Procedure ? obj.Parameters.Count : obj.Columns.Count;
            if (members > MaxListedMembers)
            {
                sb.Append('\n').Append("  ... ").Append(members - MaxListedMembers).Append(" more");
            }

            return sb.ToString();
        }

        private static string KindName(SchemaObjectKind kind) => kind switch
        {
            SchemaObjectKind.View => "view",
            SchemaObjectKind.Procedure => "procedure",
            SchemaObjectKind.ScalarFunction => "function",
            SchemaObjectKind.TableFunction => "table-valued function",
            SchemaObjectKind.Synonym => "synonym",
            _ => "table",
        };
    }
}
