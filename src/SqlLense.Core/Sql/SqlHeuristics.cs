using System;
using System.Text.RegularExpressions;

namespace SqlLense.Sql
{
    /// <summary>
    /// Decides whether a piece of text is SQL when nothing in the C# code says so explicitly.
    /// The rules are tuned to reject UI and log text ("Select an item", "Update available",
    /// "delete failed") while accepting real statements:
    /// <list type="number">
    /// <item>the text must start with a statement keyword (after whitespace, comments, '(' or ';');</item>
    /// <item>that keyword must be written ALL UPPER or all lower case, not Sentence case;</item>
    /// <item>the rest of the statement must have the shape that keyword requires (SELECT..FROM, UPDATE..SET, ...).</item>
    /// </list>
    /// Rules 2 and 3 are skipped when the string is known to reach a SQL API.
    /// </summary>
    public static class SqlHeuristics
    {
        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

        private static readonly string[] s_statementKeywords =
        {
            "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "WITH", "EXEC", "EXECUTE", "CREATE", "ALTER",
            "DROP", "TRUNCATE", "DECLARE", "SET", "IF", "BEGIN",
        };

        private static readonly Regex s_select = new Regex(
            @"^SELECT\s+(?:[@*('\d]|N'|TOP\b|DISTINCT\b|ALL\b|[\w\[\]\.]+\s*\()|^SELECT\s.*\bFROM\s", Options | RegexOptions.Singleline);

        private static readonly Regex s_insert = new Regex(@"^INSERT\s+(?:INTO\b|.*\b(?:VALUES|SELECT|EXEC|EXECUTE|DEFAULT\s+VALUES)\b)", Options | RegexOptions.Singleline);
        private static readonly Regex s_update = new Regex(@"^UPDATE\s+\S.*\bSET\s", Options | RegexOptions.Singleline);
        private static readonly Regex s_delete = new Regex(@"^DELETE\s+(?:FROM\b|TOP\b|.*\bWHERE\b)", Options | RegexOptions.Singleline);
        private static readonly Regex s_merge = new Regex(@"^MERGE\s.*\bUSING\b", Options | RegexOptions.Singleline);
        private static readonly Regex s_with = new Regex(@"^WITH\s+[\w\[\]""#]+\s*(?:\([^)]*\))?\s*AS\s*\(", Options);
        private static readonly Regex s_exec = new Regex(@"^EXEC(?:UTE)?\s+(?:\(|@|[\w\[\]\.#]+\s*(?:$|;|@|N?'|\d|,|\r|\n))", Options);
        private static readonly Regex s_create = new Regex(
            @"^(?:CREATE|ALTER)\s+(?:OR\s+ALTER\s+)?(?:TABLE|VIEW|PROCEDURE|PROC|FUNCTION|INDEX|UNIQUE|CLUSTERED|NONCLUSTERED|TRIGGER|SCHEMA|TYPE|SEQUENCE|SYNONYM)\b", Options);
        private static readonly Regex s_drop = new Regex(
            @"^DROP\s+(?:TABLE|VIEW|PROCEDURE|PROC|FUNCTION|INDEX|TRIGGER|SCHEMA|TYPE|SEQUENCE|SYNONYM)\b", Options);
        private static readonly Regex s_truncate = new Regex(@"^TRUNCATE\s+TABLE\b", Options);
        private static readonly Regex s_declare = new Regex(@"^DECLARE\s+@", Options);
        private static readonly Regex s_set = new Regex(
            @"^SET\s+(?:@|NOCOUNT\b|XACT_ABORT\b|IDENTITY_INSERT\b|TRANSACTION\b|ANSI_\w+\b|QUOTED_IDENTIFIER\b|ARITHABORT\b|LOCK_TIMEOUT\b|DEADLOCK_PRIORITY\b|DATEFIRST\b|DATEFORMAT\b|LANGUAGE\b|ROWCOUNT\b|STATISTICS\b)", Options);
        private static readonly Regex s_if = new Regex(@"^IF\s*(?:\(\s*)?(?:NOT\s+)?(?:EXISTS\b|OBJECT_ID\s*\(|@)", Options);
        private static readonly Regex s_begin = new Regex(@"^BEGIN\s+(?:TRAN|TRANSACTION|TRY|DISTRIBUTED)\b", Options);

        /// <summary>
        /// Cheap first gate: does the text start with a statement keyword (any case)? Allocation-free.
        /// </summary>
        public static bool StartsWithStatementKeyword(string text) => TryGetLeadingKeyword(text, out _, out _);

        /// <summary>Finds the leading statement keyword, skipping whitespace, comments, '(' and ';'.</summary>
        public static bool TryGetLeadingKeyword(string text, out int start, out int length)
        {
            start = SkipTrivia(text, 0);
            length = 0;
            var end = start;
            while (end < text.Length && IsAsciiLetter(text[end]))
            {
                end++;
            }

            length = end - start;
            if (length < 2 || length > 8 || (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')))
            {
                return false;
            }

            foreach (var keyword in s_statementKeywords)
            {
                if (keyword.Length == length && string.Compare(text, start, keyword, 0, length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Full heuristic check. <paramref name="knownSqlContext"/> is true when the string flows into a
        /// SQL API or is otherwise known to be SQL; then only the leading keyword is required.
        /// </summary>
        public static bool LooksLikeSql(string text, bool knownSqlContext)
        {
            if (!TryGetLeadingKeyword(text, out var start, out var length))
            {
                return false;
            }

            if (knownSqlContext)
            {
                return true;
            }

            if (!IsUniformCase(text, start, length))
            {
                return false;
            }

            var statement = start == 0 ? text : text.Substring(start);
            var regex = char.ToUpperInvariant(text[start]) switch
            {
                'S' => length == 3 ? s_set : s_select,
                'I' => length == 2 ? s_if : s_insert,
                'U' => s_update,
                'D' => length == 7 && char.ToUpperInvariant(text[start + 2]) == 'C' ? s_declare : length == 4 ? s_drop : s_delete,
                'M' => s_merge,
                'W' => s_with,
                'E' => s_exec,
                'C' => s_create,
                'A' => s_create,
                'T' => s_truncate,
                'B' => s_begin,
                _ => null,
            };

            return regex != null && regex.IsMatch(statement);
        }

        private static bool IsUniformCase(string text, int start, int length)
        {
            bool allUpper = true, allLower = true;
            for (var i = start; i < start + length; i++)
            {
                var c = text[i];
                allUpper &= c >= 'A' && c <= 'Z';
                allLower &= c >= 'a' && c <= 'z';
            }

            return allUpper || allLower;
        }

        internal static int SkipTrivia(string text, int i)
        {
            while (i < text.Length)
            {
                var c = text[i];
                if (char.IsWhiteSpace(c) || c == '(' || c == ';')
                {
                    i++;
                }
                else if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
                {
                    var newline = text.IndexOf('\n', i);
                    i = newline < 0 ? text.Length : newline + 1;
                }
                else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = close < 0 ? text.Length : close + 2;
                }
                else
                {
                    break;
                }
            }

            return i;
        }

        private static bool IsAsciiLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
    }
}
