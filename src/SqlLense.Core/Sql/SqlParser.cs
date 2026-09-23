using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlLense.Sql
{
    public sealed class SqlParseResult
    {
        private IList<TSqlParserToken>? _tokens;

        internal SqlParseResult(string text, TSqlFragment? fragment, IList<ParseError> errors)
        {
            Text = text;
            Fragment = fragment;
            Errors = errors;
            _tokens = fragment?.ScriptTokenStream;
        }

        public string Text { get; }

        public TSqlFragment? Fragment { get; }

        public IList<ParseError> Errors { get; }

        public bool HasErrors => Errors.Count > 0;

        /// <summary>The lexical tokens. Available even when parsing failed.</summary>
        public IList<TSqlParserToken> Tokens
        {
            get
            {
                var tokens = _tokens;
                if (tokens == null)
                {
                    tokens = SqlParser.Tokenize(Text);
                    Interlocked.CompareExchange(ref _tokens, tokens, null);
                    tokens = _tokens!;
                }

                return tokens;
            }
        }
    }

    /// <summary>
    /// Thread-safe front end over ScriptDom with a bounded, process-wide parse cache keyed by SQL text.
    /// Re-analysing an unchanged string (the common case when the user edits elsewhere) costs a single
    /// dictionary lookup.
    /// </summary>
    public static class SqlParser
    {
        [ThreadStatic]
        private static TSqlParser? t_parser;

        private static readonly BoundedCache<string, SqlParseResult> s_cache =
            new BoundedCache<string, SqlParseResult>(4096, StringComparer.Ordinal);

        private static TSqlParser Parser => t_parser ??= new TSql170Parser(initialQuotedIdentifiers: true);

        public static SqlParseResult Parse(string sql) => s_cache.GetOrAdd(sql, static text => ParseUncached(text));

        public static SqlParseResult ParseUncached(string sql)
        {
            using var reader = new StringReader(sql);
            var fragment = Parser.Parse(reader, out var errors);
            return new SqlParseResult(sql, fragment, errors ?? Array.Empty<ParseError>());
        }

        public static IList<TSqlParserToken> Tokenize(string sql)
        {
            using var reader = new StringReader(sql);
            return Parser.GetTokenStream(reader, out _) ?? Array.Empty<TSqlParserToken>();
        }

        internal static void ClearCache() => s_cache.Clear();
    }
}
