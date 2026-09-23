using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using SqlLense.Sql;

namespace SqlLense.Analyzers.CSharp
{
    /// <summary>A C# string expression recognised as SQL.</summary>
    public sealed class SqlStringCandidate
    {
        internal SqlStringCandidate(ExpressionSyntax expression, string sql, SqlSourceMap map, bool isKnownSqlContext)
        {
            Expression = expression;
            Sql = sql;
            Map = map;
            IsKnownSqlContext = isKnownSqlContext;
        }

        /// <summary>The outermost expression: a literal, interpolated string or concatenation.</summary>
        public ExpressionSyntax Expression { get; }

        public string Sql { get; }

        public SqlSourceMap Map { get; }

        /// <summary>True when an API, name or comment says this is SQL (vs. the text heuristics alone).</summary>
        public bool IsKnownSqlContext { get; }
    }

    public enum SqlDetectionMode
    {
        /// <summary>Balanced heuristics; used for diagnostics.</summary>
        Diagnostics,

        /// <summary>Only a leading statement keyword is needed; used while the user is typing (completion, highlighting).</summary>
        Lenient,
    }

    /// <summary>
    /// Finds SQL in C# string expressions: regular, verbatim, raw and interpolated literals plus
    /// <c>+</c> concatenations of them (and of constants). Everything is syntactic except resolving
    /// non-literal concatenation operands, which uses the semantic model only when one is supplied.
    /// </summary>
    public static class SqlStringExtractor
    {
        private static readonly HashSet<string> s_sqlMethodNames = new HashSet<string>(StringComparer.Ordinal)
        {
            // Dapper
            "Query", "QueryAsync", "QueryFirst", "QueryFirstAsync", "QueryFirstOrDefault", "QueryFirstOrDefaultAsync",
            "QuerySingle", "QuerySingleAsync", "QuerySingleOrDefault", "QuerySingleOrDefaultAsync", "QueryMultiple",
            "QueryMultipleAsync", "QueryUnbufferedAsync", "Execute", "ExecuteAsync", "ExecuteScalar", "ExecuteScalarAsync",
            "ExecuteReader", "ExecuteReaderAsync",

            // Entity Framework Core / EF6
            "FromSqlRaw", "FromSqlInterpolated", "FromSql", "ExecuteSqlRaw", "ExecuteSqlRawAsync", "ExecuteSqlInterpolated",
            "ExecuteSqlInterpolatedAsync", "ExecuteSql", "ExecuteSqlAsync", "SqlQuery", "SqlQueryRaw", "ExecuteSqlCommand",
            "ExecuteSqlCommandAsync",
        };

        private static readonly HashSet<string> s_sqlCommandTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "SqlCommand", "SqlDataAdapter", "OleDbCommand", "OdbcCommand", "DbCommand", "CommandDefinition",
        };

        private static readonly HashSet<string> s_sqlParameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sql", "sqlText", "commandText", "cmdText", "query", "sqlQuery", "selectCommandText",
        };

        /// <summary>
        /// True for the outermost node of a string expression: a literal or interpolated string that is
        /// not an operand of a concatenation, or the top of a concatenation chain.
        /// </summary>
        public static bool IsRootStringExpression(ExpressionSyntax node)
        {
            var parent = SkipParenthesesUp(node).Parent;
            if (parent is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.AddExpression))
            {
                return false;
            }

            return node.Kind() switch
            {
                SyntaxKind.StringLiteralExpression => true,
                SyntaxKind.InterpolatedStringExpression => true,
                SyntaxKind.AddExpression => ContainsStringLeaf((BinaryExpressionSyntax)node),
                _ => false,
            };
        }

        /// <summary>Finds the root string expression containing <paramref name="position"/>, if any.</summary>
        public static ExpressionSyntax? FindRootAt(SyntaxNode root, int position)
        {
            var token = root.FindToken(position, findInsideTrivia: false);
            ExpressionSyntax? candidate = null;
            for (var node = token.Parent; node != null; node = node.Parent)
            {
                if (node.IsKind(SyntaxKind.StringLiteralExpression) || node.IsKind(SyntaxKind.InterpolatedStringExpression))
                {
                    candidate = (ExpressionSyntax)node;
                }
                else if (candidate != null && !(node is ParenthesizedExpressionSyntax) &&
                         !(node is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.AddExpression)))
                {
                    break;
                }
                else if (candidate != null && node is BinaryExpressionSyntax add && add.IsKind(SyntaxKind.AddExpression))
                {
                    candidate = add;
                }
                else if (candidate == null && (node is StatementSyntax || node is MemberDeclarationSyntax))
                {
                    return null;
                }
            }

            return candidate;
        }

        public static SqlStringCandidate? TryExtract(
            ExpressionSyntax root,
            SemanticModel? semanticModel,
            SqlDetectionMode mode,
            CancellationToken cancellationToken)
        {
            var leaves = new List<ExpressionSyntax>(4);
            CollectLeaves(root, leaves);
            if (leaves.Count == 0 || !HasStringLeaf(leaves))
            {
                return null;
            }

            // Gate 1 (cheap): does the text start with a statement keyword?
            var prefix = FirstText(leaves, semanticModel, cancellationToken);
            var forced = false;
            if (prefix == null || !SqlHeuristics.StartsWithStatementKeyword(prefix))
            {
                forced = HasDirective(root, Directive.LangSql);
                if (!forced)
                {
                    return null;
                }
            }

            if (HasDirective(root, Directive.Ignore))
            {
                return null;
            }

            // Gate 2: build the text and apply the full heuristics.
            if (!TryBuild(leaves, semanticModel, cancellationToken, out var sql, out var map))
            {
                return null;
            }

            var known = forced || mode == SqlDetectionMode.Lenient || IsSqlContext(root) || HasDirective(root, Directive.LangSql);
            if (!SqlHeuristics.LooksLikeSql(sql, known) && !forced)
            {
                return null;
            }

            return new SqlStringCandidate(root, sql, map, known && mode != SqlDetectionMode.Lenient);
        }

        // ------------------------------------------------------------------ leaves

        private static void CollectLeaves(ExpressionSyntax node, List<ExpressionSyntax> leaves)
        {
            while (node is ParenthesizedExpressionSyntax parenthesized)
            {
                node = parenthesized.Expression;
            }

            if (node is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.AddExpression))
            {
                CollectLeaves(binary.Left, leaves);
                CollectLeaves(binary.Right, leaves);
            }
            else
            {
                leaves.Add(node);
            }
        }

        private static bool ContainsStringLeaf(BinaryExpressionSyntax node)
        {
            ExpressionSyntax current = node;
            while (true)
            {
                while (current is ParenthesizedExpressionSyntax p)
                {
                    current = p.Expression;
                }

                if (current is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.AddExpression))
                {
                    if (IsStringLeaf(b.Right) || (b.Right is ParenthesizedExpressionSyntax or BinaryExpressionSyntax && ContainsStringLeafSlow(b.Right)))
                    {
                        return true;
                    }

                    current = b.Left;
                    continue;
                }

                return IsStringLeaf(current);
            }
        }

        private static bool ContainsStringLeafSlow(ExpressionSyntax node)
        {
            var leaves = new List<ExpressionSyntax>();
            CollectLeaves(node, leaves);
            return HasStringLeaf(leaves);
        }

        private static bool HasStringLeaf(List<ExpressionSyntax> leaves)
        {
            foreach (var leaf in leaves)
            {
                if (IsStringLeaf(leaf))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsStringLeaf(ExpressionSyntax node) =>
            (node.IsKind(SyntaxKind.StringLiteralExpression) && !IsUtf8(((LiteralExpressionSyntax)node).Token)) ||
            node.IsKind(SyntaxKind.InterpolatedStringExpression);

        private static bool IsUtf8(SyntaxToken token) =>
            token.IsKind(SyntaxKind.Utf8StringLiteralToken) ||
            token.IsKind(SyntaxKind.Utf8SingleLineRawStringLiteralToken) ||
            token.IsKind(SyntaxKind.Utf8MultiLineRawStringLiteralToken);

        private static string? FirstText(List<ExpressionSyntax> leaves, SemanticModel? semanticModel, CancellationToken cancellationToken)
        {
            var first = leaves[0];
            switch (first)
            {
                case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                    return IsUtf8(literal.Token) ? null : literal.Token.ValueText;
                case InterpolatedStringExpressionSyntax interpolated:
                    return interpolated.Contents.Count > 0 && interpolated.Contents[0] is InterpolatedStringTextSyntax text
                        ? text.TextToken.ValueText
                        : null;
                default:
                    // "BaseQuery + \" WHERE ...\"": only worth binding when the rest looks like SQL clauses.
                    if (semanticModel == null || leaves.Count < 2 || !OtherLeavesLookLikeSqlClauses(leaves))
                    {
                        return null;
                    }

                    var constant = semanticModel.GetConstantValue(first, cancellationToken);
                    return constant.HasValue ? constant.Value as string : null;
            }
        }

        private static bool OtherLeavesLookLikeSqlClauses(List<ExpressionSyntax> leaves)
        {
            for (var i = 1; i < leaves.Count; i++)
            {
                if (leaves[i] is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    var text = literal.Token.ValueText;
                    if (ContainsWord(text, "WHERE") || ContainsWord(text, "FROM") || ContainsWord(text, "JOIN") ||
                        ContainsWord(text, "ORDER") || ContainsWord(text, "GROUP") || ContainsWord(text, "SET") ||
                        ContainsWord(text, "VALUES") || ContainsWord(text, "AND"))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool ContainsWord(string text, string word)
        {
            var index = 0;
            while ((index = text.IndexOf(word, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var before = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
                var afterIndex = index + word.Length;
                var after = afterIndex >= text.Length || !char.IsLetterOrDigit(text[afterIndex]);
                if (before && after)
                {
                    return true;
                }

                index = afterIndex;
            }

            return false;
        }

        // ------------------------------------------------------------------ building

        private readonly struct Part
        {
            public Part(string? text, SyntaxToken token, LiteralFlavor flavor, bool doubledBraces, TextSpan span, bool isLiteral)
            {
                Text = text;
                Token = token;
                Flavor = flavor;
                DoubledBraces = doubledBraces;
                Span = span;
                IsLiteral = isLiteral;
            }

            /// <summary>Null for a placeholder (interpolation hole or non-constant operand).</summary>
            public string? Text { get; }

            public SyntaxToken Token { get; }

            public LiteralFlavor Flavor { get; }

            public bool DoubledBraces { get; }

            public TextSpan Span { get; }

            public bool IsLiteral { get; }
        }

        private static bool TryBuild(
            List<ExpressionSyntax> leaves,
            SemanticModel? semanticModel,
            CancellationToken cancellationToken,
            out string sql,
            out SqlSourceMap map)
        {
            var parts = new List<Part>(leaves.Count + 2);
            foreach (var leaf in leaves)
            {
                switch (leaf)
                {
                    case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                        if (IsUtf8(literal.Token))
                        {
                            sql = string.Empty;
                            map = null!;
                            return false;
                        }

                        parts.Add(new Part(literal.Token.ValueText, literal.Token, FlavorOf(literal.Token), false, literal.Token.Span, isLiteral: true));
                        break;

                    case InterpolatedStringExpressionSyntax interpolated:
                        var flavor = FlavorOf(interpolated.StringStartToken);
                        var doubledBraces = flavor != LiteralFlavor.Raw;
                        foreach (var content in interpolated.Contents)
                        {
                            if (content is InterpolatedStringTextSyntax text)
                            {
                                parts.Add(new Part(text.TextToken.ValueText, text.TextToken, flavor, doubledBraces, text.TextToken.Span, isLiteral: true));
                            }
                            else
                            {
                                parts.Add(new Part(null, default, default, false, content.Span, isLiteral: false));
                            }
                        }

                        break;

                    default:
                        string? constant = null;
                        if (semanticModel != null)
                        {
                            var value = semanticModel.GetConstantValue(leaf, cancellationToken);
                            constant = value.HasValue ? value.Value as string : null;
                        }

                        parts.Add(new Part(constant, default, default, false, leaf.Span, isLiteral: false));
                        break;
                }
            }

            var sb = new StringBuilder();
            var segments = new List<SqlSourceMap.Segment>(parts.Count);
            var placeholderIndex = 0;
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                var start = sb.Length;
                if (part.IsLiteral)
                {
                    sb.Append(part.Text);
                    segments.Add(SqlSourceMap.Segment.Literal(start, part.Token, part.Text!, part.Flavor, part.DoubledBraces));
                    continue;
                }

                var text = part.Text ?? Placeholder(sb, i + 1 < parts.Count ? parts[i + 1].Text : null, placeholderIndex++);
                sb.Append(text);
                segments.Add(SqlSourceMap.Segment.Opaque(start, text.Length, part.Span));
            }

            sql = sb.ToString();
            map = new SqlSourceMap(segments);
            return true;
        }

        /// <summary>
        /// Chooses placeholder text that keeps the surrounding SQL parseable: a variable in value
        /// positions, a bare identifier next to '.' or inside brackets/quotes, a number after TOP.
        /// </summary>
        private static string Placeholder(StringBuilder before, string? after, int index)
        {
            var previous = LastNonWhitespace(before);
            var next = after == null ? '\0' : FirstNonWhitespace(after);
            var name = SqlEngine.PlaceholderMarker + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (InsideQuotes(before))
            {
                return name;
            }

            if (previous == '.' || previous == '[' || next == '.' || next == ']' || previous == '#')
            {
                return name;
            }

            if (EndsWithWord(before, "TOP"))
            {
                return "1";
            }

            return "@" + name;
        }

        private static bool InsideQuotes(StringBuilder sb)
        {
            var quotes = 0;
            for (var i = 0; i < sb.Length; i++)
            {
                if (sb[i] == '\'')
                {
                    quotes++;
                }
            }

            return (quotes & 1) == 1;
        }

        private static char LastNonWhitespace(StringBuilder sb)
        {
            for (var i = sb.Length - 1; i >= 0; i--)
            {
                if (!char.IsWhiteSpace(sb[i]))
                {
                    return sb[i];
                }
            }

            return '\0';
        }

        private static char FirstNonWhitespace(string text)
        {
            foreach (var c in text)
            {
                if (!char.IsWhiteSpace(c))
                {
                    return c;
                }
            }

            return '\0';
        }

        private static bool EndsWithWord(StringBuilder sb, string word)
        {
            var end = sb.Length;
            while (end > 0 && char.IsWhiteSpace(sb[end - 1]))
            {
                end--;
            }

            var start = end - word.Length;
            if (start < 0 || (start > 0 && char.IsLetterOrDigit(sb[start - 1])))
            {
                return false;
            }

            for (var i = 0; i < word.Length; i++)
            {
                if (char.ToUpperInvariant(sb[start + i]) != word[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static LiteralFlavor FlavorOf(SyntaxToken token)
        {
            switch (token.Kind())
            {
                case SyntaxKind.SingleLineRawStringLiteralToken:
                case SyntaxKind.MultiLineRawStringLiteralToken:
                case SyntaxKind.InterpolatedSingleLineRawStringStartToken:
                case SyntaxKind.InterpolatedMultiLineRawStringStartToken:
                    return LiteralFlavor.Raw;
                case SyntaxKind.InterpolatedVerbatimStringStartToken:
                    return LiteralFlavor.Verbatim;
                case SyntaxKind.StringLiteralToken:
                    return token.Text.StartsWith("@", StringComparison.Ordinal) ? LiteralFlavor.Verbatim : LiteralFlavor.Regular;
                default:
                    return LiteralFlavor.Regular;
            }
        }

        // ------------------------------------------------------------------ context

        /// <summary>Syntactic check for "this string flows into a SQL API" (no semantic model needed).</summary>
        internal static bool IsSqlContext(ExpressionSyntax root)
        {
            var node = SkipParenthesesUp(root);
            switch (node.Parent)
            {
                case ArgumentSyntax argument:
                    if (argument.NameColon != null)
                    {
                        return s_sqlParameterNames.Contains(argument.NameColon.Name.Identifier.ValueText);
                    }

                    if (argument.Parent is not BaseArgumentListSyntax list)
                    {
                        return false;
                    }

                    var index = list.Arguments.IndexOf(argument);
                    switch (list.Parent)
                    {
                        case InvocationExpressionSyntax invocation:
                            var name = MethodName(invocation.Expression);
                            return name != null && index <= 1 && s_sqlMethodNames.Contains(name);
                        case ObjectCreationExpressionSyntax creation:
                            return index == 0 && s_sqlCommandTypes.Contains(TypeName(creation.Type));
                        default:
                            return false;
                    }

                case AssignmentExpressionSyntax assignment when assignment.Right == node:
                    return MemberName(assignment.Left) is "CommandText" || IsSqlName(MemberName(assignment.Left));

                case EqualsValueClauseSyntax equals:
                    return equals.Parent switch
                    {
                        VariableDeclaratorSyntax declarator => IsSqlName(declarator.Identifier.ValueText),
                        PropertyDeclarationSyntax property => IsSqlName(property.Identifier.ValueText),
                        ParameterSyntax parameter => IsSqlName(parameter.Identifier.ValueText),
                        _ => false,
                    };

                case ArrowExpressionClauseSyntax arrow:
                    return arrow.Parent switch
                    {
                        PropertyDeclarationSyntax property => IsSqlName(property.Identifier.ValueText),
                        MethodDeclarationSyntax method => IsSqlName(method.Identifier.ValueText),
                        _ => false,
                    };

                default:
                    return false;
            }
        }

        /// <summary>Names such as <c>sql</c>, <c>GetUsersSql</c>, <c>selectQuery</c>.</summary>
        private static bool IsSqlName(string? name) =>
            name != null &&
            (name.EndsWith("sql", StringComparison.OrdinalIgnoreCase) || name.EndsWith("query", StringComparison.OrdinalIgnoreCase));

        private static string? MethodName(ExpressionSyntax expression) => expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            _ => null,
        };

        private static string? MemberName(ExpressionSyntax expression) => expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            _ => null,
        };

        private static string TypeName(TypeSyntax type) => type switch
        {
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
            _ => string.Empty,
        };

        private static SyntaxNode SkipParenthesesUp(SyntaxNode node)
        {
            while (node.Parent is ParenthesizedExpressionSyntax parenthesized)
            {
                node = parenthesized;
            }

            return node;
        }

        // ------------------------------------------------------------------ comment directives

        private enum Directive
        {
            LangSql,
            Ignore,
        }

        /// <summary>
        /// Looks for <c>/*lang=sql*/</c> or <c>// sqllense:ignore</c> style comments immediately before the
        /// string, or on the line above the containing statement or member.
        /// </summary>
        private static bool HasDirective(ExpressionSyntax root, Directive directive)
        {
            var first = root.GetFirstToken();
            if (Matches(first.LeadingTrivia, directive) || Matches(first.GetPreviousToken().TrailingTrivia, directive))
            {
                return true;
            }

            for (SyntaxNode? node = root.Parent; node != null; node = node.Parent)
            {
                if (node is StatementSyntax || node is MemberDeclarationSyntax)
                {
                    return Matches(node.GetFirstToken().LeadingTrivia, directive);
                }
            }

            return false;
        }

        private static bool Matches(SyntaxTriviaList trivia, Directive directive)
        {
            foreach (var t in trivia)
            {
                if (!t.IsKind(SyntaxKind.SingleLineCommentTrivia) && !t.IsKind(SyntaxKind.MultiLineCommentTrivia))
                {
                    continue;
                }

                var text = t.ToString();
                if (directive == Directive.Ignore)
                {
                    if (text.IndexOf("sqllense:ignore", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        text.IndexOf("sqllense-ignore", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
                else if (IsLangSqlComment(text))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsLangSqlComment(string comment)
        {
            var compact = new StringBuilder(comment.Length);
            foreach (var c in comment)
            {
                if (!char.IsWhiteSpace(c))
                {
                    compact.Append(char.ToLowerInvariant(c));
                }
            }

            var text = compact.ToString();
            return text.Contains("lang=sql") || text.Contains("language=sql") || text.Contains("lang=tsql") || text.Contains("language=tsql");
        }
    }
}
