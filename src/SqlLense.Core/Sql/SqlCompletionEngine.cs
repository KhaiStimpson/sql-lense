using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlLense.Schema;

namespace SqlLense.Sql
{
    public enum SqlCompletionKind
    {
        Table,
        View,
        Column,
        Procedure,
        Function,
        Schema,
        Alias,
    }

    public sealed class SqlCompletionItem
    {
        public SqlCompletionItem(string displayText, string insertText, SqlCompletionKind kind, string? detail, string sortPrefix)
        {
            DisplayText = displayText;
            InsertText = insertText;
            Kind = kind;
            Detail = detail;
            SortText = sortPrefix + displayText;
        }

        public string DisplayText { get; }

        public string InsertText { get; }

        public SqlCompletionKind Kind { get; }

        public string? Detail { get; }

        public string SortText { get; }

        public override string ToString() => $"{Kind}:{DisplayText}";
    }

    public sealed class SqlCompletionResult
    {
        public SqlCompletionResult(int replaceStart, int replaceLength, IReadOnlyList<SqlCompletionItem> items)
        {
            ReplaceStart = replaceStart;
            ReplaceLength = replaceLength;
            Items = items;
        }

        /// <summary>Start of the partially typed word, as an offset into the SQL text.</summary>
        public int ReplaceStart { get; }

        public int ReplaceLength { get; }

        public IReadOnlyList<SqlCompletionItem> Items { get; }
    }

    /// <summary>
    /// Schema-aware completion for SQL being typed. It is token based rather than AST based because
    /// SQL under the caret is almost never syntactically complete.
    /// </summary>
    public static class SqlCompletionEngine
    {
        private static readonly Regex s_regularIdentifier = new Regex(@"^[A-Za-z_][A-Za-z0-9_@$#]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly HashSet<string> s_tableContextKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "FROM", "JOIN", "INTO", "UPDATE", "TABLE", "MERGE", "USING", "APPLY",
        };

        private static readonly HashSet<string> s_clauseEndKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "WHERE", "GROUP", "ORDER", "HAVING", "UNION", "EXCEPT", "INTERSECT", "SELECT", "SET", "ON",
            "OPTION", "FOR", "WINDOW", "VALUES", "OUTPUT", "WHEN",
        };

        public static SqlCompletionResult? GetCompletions(string sql, int offset, DatabaseSchema schema)
        {
            if (offset < 0 || offset > sql.Length)
            {
                return null;
            }

            var tokens = SqlParser.Tokenize(sql);
            if (IsInsideStringOrComment(tokens, offset))
            {
                return null;
            }

            var wordStart = offset;
            while (wordStart > 0 && IsWordChar(sql[wordStart - 1]))
            {
                wordStart--;
            }

            if (wordStart < sql.Length && sql[wordStart] == '@')
            {
                return null; // variables and parameters
            }

            var replaceStart = wordStart;
            var replaceEnd = offset;
            while (replaceEnd < sql.Length && IsWordChar(sql[replaceEnd]))
            {
                replaceEnd++;
            }

            var references = CollectReferences(tokens, schema);
            var items = new List<SqlCompletionItem>();
            var qualifier = ReadQualifier(sql, wordStart);

            if (qualifier != null)
            {
                AddQualifiedItems(items, qualifier, references, schema, PreviousKeyword(tokens, qualifierStart: wordStart - qualifier.Length - 1));
            }
            else
            {
                var previous = PreviousKeyword(tokens, wordStart);
                if (previous == "EXEC" || previous == "EXECUTE")
                {
                    AddObjects(items, schema, o => o.Kind == SchemaObjectKind.Procedure, qualify: false);
                    AddSchemas(items, schema);
                }
                else if (previous != null && s_tableContextKeywords.Contains(previous))
                {
                    AddObjects(items, schema, o => o.IsRowSource, qualify: false);
                    AddSchemas(items, schema);
                }
                else
                {
                    AddColumns(items, references, schema);
                }
            }

            return new SqlCompletionResult(replaceStart, replaceEnd - replaceStart, items);
        }

        /// <summary>
        /// Finds the schema element under <paramref name="offset"/>. Uses the full semantic analysis when
        /// the SQL parses, and falls back to token-level alias resolution when it does not.
        /// </summary>
        public static SqlSymbol? FindSymbol(string sql, int offset, DatabaseSchema schema)
        {
            var analyzed = SqlEngine.Analyze(sql, schema).FindSymbolAt(offset);
            if (analyzed != null)
            {
                return analyzed;
            }

            var tokens = SqlParser.Parse(sql).Tokens;
            var index = SqlEngine.FindTokenIndex(tokens, offset);
            if (index < 0 || index >= tokens.Count)
            {
                return null;
            }

            var token = tokens[index];
            if (token.TokenType is not (TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier) ||
                offset < token.Offset || offset > token.Offset + token.Text.Length)
            {
                return null;
            }

            var name = Unquote(token.Text);
            var references = CollectReferences(tokens, schema);
            if (index >= 2 && tokens[index - 1].TokenType == TSqlTokenType.Dot)
            {
                var qualifier = Unquote(tokens[index - 2].Text);
                if (references.Aliases.TryGetValue(qualifier, out var aliased) &&
                    aliased.FindColumn(name, schema.Comparer) is { } qualifiedColumn)
                {
                    return new SqlSymbol(SqlSymbolKind.Column, token.Offset, token.Text.Length, aliased, qualifiedColumn);
                }

                if (schema.Find(qualifier, name) is { } qualifiedObject)
                {
                    return new SqlSymbol(KindOf(qualifiedObject), token.Offset, token.Text.Length, qualifiedObject);
                }

                return null;
            }

            if (schema.Find(null, name) is { } obj)
            {
                return new SqlSymbol(KindOf(obj), token.Offset, token.Text.Length, obj);
            }

            foreach (var referenced in references.Objects)
            {
                if (referenced.FindColumn(name, schema.Comparer) is { } column)
                {
                    return new SqlSymbol(SqlSymbolKind.Column, token.Offset, token.Text.Length, referenced, column);
                }
            }

            return null;
        }

        private static SqlSymbolKind KindOf(SchemaObject obj) => obj.Kind switch
        {
            SchemaObjectKind.Procedure => SqlSymbolKind.Procedure,
            SchemaObjectKind.ScalarFunction or SchemaObjectKind.TableFunction => SqlSymbolKind.Function,
            _ => SqlSymbolKind.Table,
        };

        private static void AddQualifiedItems(List<SqlCompletionItem> items, string qualifier, References references, DatabaseSchema schema, string? previousKeyword)
        {
            if (references.Aliases.TryGetValue(qualifier, out var aliased))
            {
                AddColumnsOf(items, aliased, includeTable: false);
                return;
            }

            if (schema.HasSchema(qualifier))
            {
                Func<SchemaObject, bool> filter = previousKeyword is "EXEC" or "EXECUTE"
                    ? o => o.Kind == SchemaObjectKind.Procedure
                    : previousKeyword != null && s_tableContextKeywords.Contains(previousKeyword)
                        ? o => o.IsRowSource
                        : o => o.Kind != SchemaObjectKind.Procedure;
                foreach (var obj in schema.Objects)
                {
                    if (schema.Comparer.Equals(obj.Schema, qualifier) && filter(obj))
                    {
                        items.Add(ObjectItem(obj, qualify: false));
                    }
                }

                return;
            }

            if (schema.Find(null, qualifier) is { } table)
            {
                AddColumnsOf(items, table, includeTable: false);
            }
        }

        private static void AddColumns(List<SqlCompletionItem> items, References references, DatabaseSchema schema)
        {
            foreach (var alias in references.Aliases)
            {
                if (!schema.Comparer.Equals(alias.Key, alias.Value.Name))
                {
                    items.Add(new SqlCompletionItem(alias.Key, Quote(alias.Key), SqlCompletionKind.Alias, "alias for " + alias.Value.QualifiedName, "1"));
                }
            }

            var includeTable = references.Objects.Count > 1;
            foreach (var obj in references.Objects)
            {
                AddColumnsOf(items, obj, includeTable);
            }
        }

        private static void AddColumnsOf(List<SqlCompletionItem> items, SchemaObject obj, bool includeTable)
        {
            foreach (var column in obj.Columns)
            {
                var detail = (includeTable ? obj.Name + "." : string.Empty) + column.Name + " " + column.DataType + (column.IsNullable ? " NULL" : " NOT NULL");
                items.Add(new SqlCompletionItem(column.Name, Quote(column.Name), SqlCompletionKind.Column, detail, "0"));
            }
        }

        private static void AddObjects(List<SqlCompletionItem> items, DatabaseSchema schema, Func<SchemaObject, bool> filter, bool qualify)
        {
            foreach (var obj in schema.Objects)
            {
                if (filter(obj))
                {
                    var inDefault = schema.Comparer.Equals(obj.Schema, schema.DefaultSchema) || schema.Comparer.Equals(obj.Schema, "dbo");
                    items.Add(ObjectItem(obj, qualify || !inDefault));
                }
            }
        }

        private static void AddSchemas(List<SqlCompletionItem> items, DatabaseSchema schema)
        {
            foreach (var name in schema.SchemaNames)
            {
                items.Add(new SqlCompletionItem(name, Quote(name), SqlCompletionKind.Schema, "schema", "2"));
            }
        }

        private static SqlCompletionItem ObjectItem(SchemaObject obj, bool qualify)
        {
            var kind = obj.Kind switch
            {
                SchemaObjectKind.View => SqlCompletionKind.View,
                SchemaObjectKind.Procedure => SqlCompletionKind.Procedure,
                SchemaObjectKind.ScalarFunction or SchemaObjectKind.TableFunction => SqlCompletionKind.Function,
                _ => SqlCompletionKind.Table,
            };

            var display = qualify ? obj.Schema + "." + obj.Name : obj.Name;
            var insert = qualify ? Quote(obj.Schema) + "." + Quote(obj.Name) : Quote(obj.Name);
            var detail = obj.Kind.ToString().ToLowerInvariant() + " " + obj.QualifiedName +
                (obj.Columns.Count > 0 ? $" ({obj.Columns.Count} columns)" : string.Empty);
            return new SqlCompletionItem(display, insert, kind, detail, qualify ? "1" : "0");
        }

        private static string Quote(string name) => s_regularIdentifier.IsMatch(name) ? name : "[" + name.Replace("]", "]]") + "]";

        private static string Unquote(string text)
        {
            if (text.Length >= 2 && ((text[0] == '[' && text[text.Length - 1] == ']') || (text[0] == '"' && text[text.Length - 1] == '"')))
            {
                return text.Substring(1, text.Length - 2).Replace("]]", "]");
            }

            return text;
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '#' || c == '@' || c == '$' || c == '[';

        /// <summary>Reads the identifier before a '.' that immediately precedes <paramref name="wordStart"/>.</summary>
        private static string? ReadQualifier(string sql, int wordStart)
        {
            if (wordStart == 0 || sql[wordStart - 1] != '.')
            {
                return null;
            }

            var end = wordStart - 1;
            var start = end;
            if (start > 0 && sql[start - 1] == ']')
            {
                var open = sql.LastIndexOf('[', start - 1);
                if (open < 0)
                {
                    return null;
                }

                return sql.Substring(open + 1, start - 2 - open);
            }

            while (start > 0 && (char.IsLetterOrDigit(sql[start - 1]) || sql[start - 1] == '_' || sql[start - 1] == '#'))
            {
                start--;
            }

            return start == end ? null : sql.Substring(start, end - start);
        }

        private static string? PreviousKeyword(IList<TSqlParserToken> tokens, int qualifierStart)
        {
            for (var i = tokens.Count - 1; i >= 0; i--)
            {
                var token = tokens[i];
                if (token.Offset >= qualifierStart || token.TokenType is TSqlTokenType.WhiteSpace or TSqlTokenType.EndOfFile
                    or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment)
                {
                    continue;
                }

                if (token.TokenType == TSqlTokenType.Comma)
                {
                    // "FROM a, |" still completes tables.
                    return InFromList(tokens, i) ? "FROM" : ",";
                }

                return token.Text?.ToUpperInvariant();
            }

            return null;
        }

        private static bool InFromList(IList<TSqlParserToken> tokens, int commaIndex)
        {
            for (var i = commaIndex - 1; i >= 0; i--)
            {
                var text = tokens[i].Text;
                if (tokens[i].TokenType is TSqlTokenType.WhiteSpace || text == null)
                {
                    continue;
                }

                if (text.Equals("FROM", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (s_clauseEndKeywords.Contains(text) || text.Equals("JOIN", StringComparison.OrdinalIgnoreCase) || tokens[i].TokenType == TSqlTokenType.LeftParenthesis)
                {
                    return false;
                }
            }

            return false;
        }

        private static bool IsInsideStringOrComment(IList<TSqlParserToken> tokens, int offset)
        {
            foreach (var token in tokens)
            {
                if (token.TokenType is TSqlTokenType.AsciiStringLiteral or TSqlTokenType.UnicodeStringLiteral
                    or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment)
                {
                    var end = token.Offset + (token.Text?.Length ?? 0);
                    if (offset > token.Offset && offset < end)
                    {
                        return true;
                    }

                    if (token.TokenType == TSqlTokenType.SingleLineComment && offset == end && !token.Text!.EndsWith("\n", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private sealed class References
        {
            public References(StringComparer comparer) => Aliases = new Dictionary<string, SchemaObject>(comparer);

            public Dictionary<string, SchemaObject> Aliases { get; }

            public List<SchemaObject> Objects { get; } = new List<SchemaObject>();
        }

        /// <summary>Finds "&lt;keyword&gt; [schema.]name [AS] alias" table references in a token stream.</summary>
        private static References CollectReferences(IList<TSqlParserToken> tokens, DatabaseSchema schema)
        {
            var result = new References(schema.Comparer);
            var significant = tokens.Where(t => t.TokenType is not (TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment
                or TSqlTokenType.MultilineComment or TSqlTokenType.EndOfFile)).ToList();
            var inFrom = false;

            for (var i = 0; i < significant.Count; i++)
            {
                var text = significant[i].Text ?? string.Empty;
                var isComma = significant[i].TokenType == TSqlTokenType.Comma;
                if (text.Equals("FROM", StringComparison.OrdinalIgnoreCase))
                {
                    inFrom = true;
                }
                else if (s_clauseEndKeywords.Contains(text))
                {
                    inFrom = false;
                }

                if (!(s_tableContextKeywords.Contains(text) || (isComma && inFrom)))
                {
                    continue;
                }

                // Read a dotted name.
                var j = i + 1;
                var parts = new List<string>();
                while (j < significant.Count && significant[j].TokenType is TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier)
                {
                    parts.Add(Unquote(significant[j].Text));
                    if (j + 1 < significant.Count && significant[j + 1].TokenType == TSqlTokenType.Dot)
                    {
                        j += 2;
                        continue;
                    }

                    j++;
                    break;
                }

                if (parts.Count == 0)
                {
                    continue;
                }

                var obj = schema.Find(parts.Count >= 2 ? parts[parts.Count - 2] : null, parts[parts.Count - 1]);
                if (obj == null)
                {
                    continue;
                }

                if (!result.Objects.Contains(obj))
                {
                    result.Objects.Add(obj);
                }

                result.Aliases[obj.Name] = obj;
                if (j < significant.Count && significant[j].TokenType == TSqlTokenType.As)
                {
                    j++;
                }

                if (j < significant.Count && significant[j].TokenType is TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier &&
                    !significant[j].IsKeyword())
                {
                    result.Aliases[Unquote(significant[j].Text)] = obj;
                }
            }

            return result;
        }
    }
}
