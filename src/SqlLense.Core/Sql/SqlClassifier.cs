using System;
using System.Collections.Generic;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlLense.Sql
{
    public enum SqlTokenClass
    {
        Keyword,
        Identifier,
        Function,
        Variable,
        String,
        Number,
        Comment,
        Operator,
    }

    public readonly struct SqlClassifiedSpan
    {
        public SqlClassifiedSpan(int start, int length, SqlTokenClass tokenClass)
        {
            Start = start;
            Length = length;
            Class = tokenClass;
        }

        public int Start { get; }

        public int Length { get; }

        public SqlTokenClass Class { get; }

        public override string ToString() => $"{Class}@{Start}+{Length}";
    }

    /// <summary>Lexical classification for syntax highlighting. Works on invalid SQL too.</summary>
    public static class SqlClassifier
    {
        // Words ScriptDom lexes as plain identifiers but users read as keywords.
        private static readonly HashSet<string> s_softKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "OFFSET", "ROWS", "ROW", "NEXT", "ONLY", "FIRST", "LAST", "NOCOUNT", "XACT_ABORT", "TRY", "CATCH",
            "THROW", "MATCHED", "TARGET", "SOURCE", "PARTITION", "NOLOCK", "READUNCOMMITTED", "ROWLOCK",
            "UPDLOCK", "HOLDLOCK", "READPAST", "TABLOCK", "TABLOCKX", "PAGLOCK", "APPLY", "WITHIN", "RETURNS",
            "INSTEAD", "AFTER", "OUTPUT", "RECOMPILE", "OPTIMIZE", "MAXDOP", "PRECEDING", "FOLLOWING",
            "UNBOUNDED", "RANGE", "CONCAT", "OUT", "READONLY", "ASC", "DESC", "TIES", "PERCENT",
        };

        private static readonly BoundedCache<string, IReadOnlyList<SqlClassifiedSpan>> s_cache =
            new BoundedCache<string, IReadOnlyList<SqlClassifiedSpan>>(2048, StringComparer.Ordinal);

        public static IReadOnlyList<SqlClassifiedSpan> Classify(string sql) =>
            s_cache.GetOrAdd(sql, static text => ClassifyCore(SqlParser.Parse(text).Tokens));

        internal static IReadOnlyList<SqlClassifiedSpan> ClassifyCore(IList<TSqlParserToken> tokens)
        {
            var result = new List<SqlClassifiedSpan>(tokens.Count / 2 + 1);
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                var text = token.Text;
                if (string.IsNullOrEmpty(text) || SqlEngine.IsPlaceholder(text))
                {
                    continue;
                }

                SqlTokenClass? tokenClass;
                switch (token.TokenType)
                {
                    case TSqlTokenType.WhiteSpace:
                    case TSqlTokenType.EndOfFile:
                        tokenClass = null;
                        break;
                    case TSqlTokenType.SingleLineComment:
                    case TSqlTokenType.MultilineComment:
                        tokenClass = SqlTokenClass.Comment;
                        break;
                    case TSqlTokenType.AsciiStringLiteral:
                    case TSqlTokenType.UnicodeStringLiteral:
                        tokenClass = SqlTokenClass.String;
                        break;
                    case TSqlTokenType.Integer:
                    case TSqlTokenType.Numeric:
                    case TSqlTokenType.Real:
                    case TSqlTokenType.Money:
                    case TSqlTokenType.HexLiteral:
                        tokenClass = SqlTokenClass.Number;
                        break;
                    case TSqlTokenType.Variable:
                        tokenClass = SqlTokenClass.Variable;
                        break;
                    case TSqlTokenType.Identifier:
                        tokenClass = s_softKeywords.Contains(text) ? SqlTokenClass.Keyword
                            : IsFollowedByParenthesis(tokens, i) ? SqlTokenClass.Function
                            : SqlTokenClass.Identifier;
                        break;
                    case TSqlTokenType.QuotedIdentifier:
                    case TSqlTokenType.AsciiStringOrQuotedIdentifier:
                        tokenClass = SqlTokenClass.Identifier;
                        break;
                    default:
                        tokenClass = token.IsKeyword() ? SqlTokenClass.Keyword
                            : char.IsLetter(text[0]) ? SqlTokenClass.Identifier
                            : SqlTokenClass.Operator;
                        break;
                }

                if (tokenClass.HasValue)
                {
                    result.Add(new SqlClassifiedSpan(token.Offset, text.Length, tokenClass.Value));
                }
            }

            return result;
        }

        private static bool IsFollowedByParenthesis(IList<TSqlParserToken> tokens, int index)
        {
            for (var i = index + 1; i < tokens.Count; i++)
            {
                var type = tokens[i].TokenType;
                if (type == TSqlTokenType.WhiteSpace)
                {
                    continue;
                }

                return type == TSqlTokenType.LeftParenthesis;
            }

            return false;
        }
    }
}
