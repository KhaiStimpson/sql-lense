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

        internal SqlDocumentRegions(List<SqlStringCandidate> candidates) => Candidates = candidates;

        public IReadOnlyList<SqlStringCandidate> Candidates { get; }

        public SqlStringCandidate? FindAt(int position)
        {
            foreach (var candidate in Candidates)
            {
                var span = candidate.Expression.Span;
                if (position < span.Start)
                {
                    break;
                }

                if (position <= span.End)
                {
                    return candidate;
                }
            }

            return null;
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

        /// <summary>Classified SQL tokens intersecting <paramref name="range"/>.</summary>
        public static IEnumerable<SqlClassifiedSourceSpan> Classify(SqlDocumentRegions regions, TextSpan range)
        {
            foreach (var candidate in regions.Candidates)
            {
                if (candidate.Expression.Span.End < range.Start)
                {
                    continue;
                }

                if (candidate.Expression.Span.Start > range.End)
                {
                    yield break;
                }

                foreach (var token in SqlClassifier.Classify(candidate.Sql))
                {
                    if (candidate.Map.TryMapWithinLiteral(token.Start, token.Length, out var span) && span.IntersectsWith(range))
                    {
                        yield return new SqlClassifiedSourceSpan(span, token.Class);
                    }
                }
            }
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
