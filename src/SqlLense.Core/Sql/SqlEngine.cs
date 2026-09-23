using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlLense.Schema;

namespace SqlLense.Sql
{
    /// <summary>
    /// Entry point for analysing a SQL text: syntax errors plus, when a schema is available, object
    /// and column validation. Results are memoized per (schema snapshot, SQL text); a refreshed
    /// snapshot is a new <see cref="DatabaseSchema"/> instance, so stale results are dropped with it.
    /// </summary>
    public static class SqlEngine
    {
        /// <summary>
        /// Marker used for C# interpolation holes and non-constant concatenation operands. Any SQL
        /// name containing it is never validated, and syntax errors next to it are suppressed.
        /// </summary>
        public const string PlaceholderMarker = "__sqllense_p";

        private const int CacheCapacity = 4096;

        private static readonly Regex s_expectedButEncountered =
            new Regex(@"^Expected .+ but encountered (?<token>.+) instead\.$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly BoundedCache<string, SqlAnalysisResult> s_syntaxOnlyCache =
            new BoundedCache<string, SqlAnalysisResult>(CacheCapacity, StringComparer.Ordinal);

        private static readonly ConditionalWeakTable<DatabaseSchema, BoundedCache<string, SqlAnalysisResult>> s_schemaCaches =
            new ConditionalWeakTable<DatabaseSchema, BoundedCache<string, SqlAnalysisResult>>();

        public static bool IsPlaceholder(string? text) =>
            text != null && text.IndexOf(PlaceholderMarker, StringComparison.Ordinal) >= 0;

        public static SqlAnalysisResult Analyze(string sql, DatabaseSchema? schema)
        {
            if (schema == null)
            {
                return s_syntaxOnlyCache.GetOrAdd(sql, static text => AnalyzeCore(text, null));
            }

            var cache = s_schemaCaches.GetValue(schema, static _ => new BoundedCache<string, SqlAnalysisResult>(CacheCapacity, StringComparer.Ordinal));
            return cache.GetOrAdd(sql, text => AnalyzeCore(text, schema));
        }

        private static SqlAnalysisResult AnalyzeCore(string sql, DatabaseSchema? schema)
        {
            var parse = SqlParser.Parse(sql);
            var diagnostics = new List<SqlDiagnostic>();
            var symbols = new List<SqlSymbol>();

            if (parse.HasErrors)
            {
                AddSyntaxErrors(parse, diagnostics);
            }
            else if (schema != null && parse.Fragment != null)
            {
                SqlSemanticValidator.Validate(parse.Fragment, schema, diagnostics, symbols);
                symbols.Sort((a, b) => a.Start.CompareTo(b.Start));
            }

            return diagnostics.Count == 0 && symbols.Count == 0
                ? SqlAnalysisResult.Empty
                : new SqlAnalysisResult(diagnostics, symbols);
        }

        private static void AddSyntaxErrors(SqlParseResult parse, List<SqlDiagnostic> diagnostics)
        {
            var tokens = parse.Tokens;
            var seen = new HashSet<int>();
            foreach (var error in parse.Errors)
            {
                if (!seen.Add(error.Offset))
                {
                    continue;
                }

                var index = FindTokenIndex(tokens, error.Offset);
                if (index >= 0 && IsNearPlaceholder(tokens, index))
                {
                    continue;
                }

                int start, length;
                if (index >= 0 && tokens[index].TokenType != TSqlTokenType.EndOfFile)
                {
                    start = tokens[index].Offset;
                    length = Math.Max(1, tokens[index].Text?.Length ?? 1);
                }
                else
                {
                    // Unexpected end of input: point at the last meaningful token.
                    var last = PreviousSignificant(tokens, index < 0 ? tokens.Count : index);
                    start = last >= 0 ? tokens[last].Offset : 0;
                    length = last >= 0 ? Math.Max(1, tokens[last].Text?.Length ?? 1) : Math.Max(1, parse.Text.Length);
                }

                diagnostics.Add(new SqlDiagnostic(SqlDiagnosticKind.SyntaxError, start, length, NormalizeMessage(error.Message)));
            }
        }

        private static string NormalizeMessage(string message)
        {
            // ScriptDom reports some failures as "Expected WINDOW but encountered FORM instead.", which
            // is confusing; SQL Server itself says "Incorrect syntax near 'FORM'."
            var match = s_expectedButEncountered.Match(message);
            return match.Success ? $"Incorrect syntax near '{match.Groups["token"].Value}'." : message;
        }

        private static bool IsNearPlaceholder(IList<TSqlParserToken> tokens, int index)
        {
            if (IsPlaceholder(tokens[index].Text))
            {
                return true;
            }

            var previous = PreviousSignificant(tokens, index);
            if (previous >= 0 && IsPlaceholder(tokens[previous].Text))
            {
                return true;
            }

            var next = NextSignificant(tokens, index);
            return next >= 0 && IsPlaceholder(tokens[next].Text);
        }

        /// <summary>Index of the token containing <paramref name="offset"/>, or the first token after it.</summary>
        internal static int FindTokenIndex(IList<TSqlParserToken> tokens, int offset)
        {
            int lo = 0, hi = tokens.Count - 1, result = -1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >> 1;
                if (tokens[mid].Offset <= offset)
                {
                    result = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            if (result >= 0 && tokens[result].TokenType == TSqlTokenType.WhiteSpace)
            {
                var next = NextSignificant(tokens, result);
                return next >= 0 ? next : result;
            }

            return result;
        }

        private static int PreviousSignificant(IList<TSqlParserToken> tokens, int index)
        {
            for (var i = index - 1; i >= 0; i--)
            {
                if (IsSignificant(tokens[i]))
                {
                    return i;
                }
            }

            return -1;
        }

        private static int NextSignificant(IList<TSqlParserToken> tokens, int index)
        {
            for (var i = index + 1; i < tokens.Count; i++)
            {
                if (IsSignificant(tokens[i]))
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool IsSignificant(TSqlParserToken token) =>
            token.TokenType is not (TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment
                or TSqlTokenType.MultilineComment or TSqlTokenType.EndOfFile);
    }
}
