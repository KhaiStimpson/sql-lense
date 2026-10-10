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
        internal SqlStringCandidate(ExpressionSyntax expression, string sql, SqlSourceMap map, bool isKnownSqlContext, TextSpan span)
        {
            Expression = expression;
            Sql = sql;
            Map = map;
            IsKnownSqlContext = isKnownSqlContext;
            Span = map.Bounds.IsEmpty ? span : TextSpan.FromBounds(Math.Min(span.Start, map.Bounds.Start), Math.Max(span.End, map.Bounds.End));
        }

        /// <summary>
        /// The outermost expression: a literal, interpolated string, concatenation or format call. For a
        /// string built up over several statements, the first piece that starts like SQL.
        /// </summary>
        public ExpressionSyntax Expression { get; }

        public string Sql { get; }

        public SqlSourceMap Map { get; }

        /// <summary>True when an API, name or comment says this is SQL (vs. the text heuristics alone).</summary>
        public bool IsKnownSqlContext { get; }

        /// <summary>
        /// Source covered by the SQL: <see cref="Expression"/>, every statement of a built-up string, and the
        /// initializers of inlined locals.
        /// </summary>
        public TextSpan Span { get; }
    }

    public enum SqlDetectionMode
    {
        /// <summary>Balanced heuristics; used for diagnostics.</summary>
        Diagnostics,

        /// <summary>Only a leading statement keyword is needed; used while the user is typing (completion, highlighting).</summary>
        Lenient,
    }

    /// <summary>
    /// Finds SQL in C# string expressions: regular, verbatim, raw and interpolated literals,
    /// <c>+</c> concatenations, <c>string.Format</c>/<c>Concat</c> and composite format strings, locals
    /// that hold a string, and strings built up over several statements with <c>+=</c> or a
    /// <c>StringBuilder</c>. Everything is syntactic except resolving constants, which uses the semantic
    /// model only when one is supplied.
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

        /// <summary>APIs whose SQL string uses <c>{0}</c> items as parameters (never substituted as text).</summary>
        private static readonly HashSet<string> s_formatParameterMethodNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "FromSqlRaw", "ExecuteSqlRaw", "ExecuteSqlRawAsync", "SqlQueryRaw", "SqlQuery", "ExecuteSqlCommand", "ExecuteSqlCommandAsync",
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
        /// not an operand of a concatenation, the top of a concatenation chain, or the first string
        /// argument of a <c>string.Format</c>/<c>string.Concat</c> call (which stands for the call).
        /// </summary>
        public static bool IsRootStringExpression(ExpressionSyntax node)
        {
            var parent = SkipParenthesesUp(node).Parent;
            if (parent is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.AddExpression))
            {
                return false;
            }

            var isCandidate = node.Kind() switch
            {
                SyntaxKind.StringLiteralExpression => true,
                SyntaxKind.InterpolatedStringExpression => true,
                SyntaxKind.AddExpression => ContainsStringLeaf((BinaryExpressionSyntax)node),
                _ => false,
            };

            if (!isCandidate)
            {
                return false;
            }

            var outer = Outer(node);
            return outer == node || FirstRootCandidate(outer) == node;
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

            return candidate == null ? null : Outer(candidate);
        }

        /// <summary>
        /// Extracts the SQL of a root string expression. A piece of a string built up over several
        /// statements yields the whole statement, but only from its first piece that starts like SQL, so
        /// that each statement is reported once.
        /// </summary>
        public static SqlStringCandidate? TryExtract(
            ExpressionSyntax root,
            SemanticModel? semanticModel,
            SqlDetectionMode mode,
            CancellationToken cancellationToken) =>
            TryExtractCore(root, semanticModel, mode, containing: false, depth: 0, cancellationToken);

        /// <summary>
        /// Like <see cref="TryExtract"/>, but for any piece of a built-up string (for example the
        /// <c>" WHERE c."</c> being typed in <c>sql += " WHERE c."</c>) returns the whole statement.
        /// </summary>
        public static SqlStringCandidate? TryExtractContaining(
            ExpressionSyntax root,
            SemanticModel? semanticModel,
            SqlDetectionMode mode,
            CancellationToken cancellationToken) =>
            TryExtractCore(root, semanticModel, mode, containing: true, depth: 0, cancellationToken);

        private static SqlStringCandidate? TryExtractCore(
            ExpressionSyntax node,
            SemanticModel? semanticModel,
            SqlDetectionMode mode,
            bool containing,
            int depth,
            CancellationToken cancellationToken)
        {
            var outer = Outer(node);
            if (!IsStringShapedOuter(outer))
            {
                return null;
            }

            // Gate 1 (cheap): does the text start with a statement keyword? Pieces such as " WHERE ..."
            // stop here unless an editor feature asks for the statement they belong to.
            var prefix = FirstText(outer, semanticModel, depth, cancellationToken);
            Directive? directives = null;
            var passes = prefix != null && SqlHeuristics.StartsWithStatementKeyword(prefix);
            if (!passes)
            {
                directives = GetDirectives(node, outer);
                passes = (directives & Directive.LangSql) != 0;
            }
            if (!passes && !containing)
            {
                return null;
            }

            if (SqlStringFlow.TryGetTarget(outer, out var name, out var statement, out var isDefinition) &&
                (!isDefinition || SqlStringFlow.IsAppendedAfter(statement, name)))
            {
                var chain = SqlStringFlow.FindChain(outer, semanticModel, cancellationToken);
                if (chain != null && chain.Fragments.Count > 1)
                {
                    var owner = FindOwner(chain, semanticModel, depth, cancellationToken);
                    if (owner != null && (owner.Anchor == outer || containing))
                    {
                        var candidate = TryExtractChain(chain, owner, semanticModel, mode, depth, cancellationToken, out var unknownPrefix);
                        if (candidate != null || !unknownPrefix || owner.Anchor != outer)
                        {
                            return candidate;
                        }

                        // The pieces before this one make the start unknown (say, a parameter used as a
                        // prefix): fall back to this piece on its own.
                    }
                    else if (owner != null)
                    {
                        return null;
                    }
                }
            }

            return passes ? TryExtractExpression(outer, directives ?? GetDirectives(node, outer), semanticModel, mode, depth, cancellationToken) : null;
        }

        private static SqlStringCandidate? TryExtractExpression(
            ExpressionSyntax outer,
            Directive directives,
            SemanticModel? semanticModel,
            SqlDetectionMode mode,
            int depth,
            CancellationToken cancellationToken)
        {
            var forced = (directives & Directive.LangSql) != 0;
            if ((directives & Directive.Ignore) != 0)
            {
                return null;
            }

            // Gate 2: build the text and apply the full heuristics.
            var builder = new SqlTextBuilder(semanticModel, depth, cancellationToken);
            builder.BeginFragment();
            builder.AddExpression(outer);
            if (!builder.TryBuild(out var sql, out var map))
            {
                return null;
            }

            var known = forced || mode == SqlDetectionMode.Lenient || IsSqlContext(outer);
            if (!SqlHeuristics.LooksLikeSql(sql, known) && !forced)
            {
                return null;
            }

            return new SqlStringCandidate(outer, sql, map, known && mode != SqlDetectionMode.Lenient, outer.Span);
        }

        private static SqlStringFlow.Fragment? FindOwner(SqlStringFlow.Chain chain, SemanticModel? semanticModel, int depth, CancellationToken cancellationToken)
        {
            foreach (var fragment in chain.Fragments)
            {
                if (fragment.Anchor == null || fragment.Kind == SqlStringFlow.FragmentKind.SelfAppend)
                {
                    continue;
                }

                var text = FirstText(fragment.Anchor, semanticModel, depth, cancellationToken);
                if ((text != null && SqlHeuristics.StartsWithStatementKeyword(text)) || HasDirective(fragment.Anchor, fragment.Anchor, Directive.LangSql))
                {
                    return fragment;
                }
            }

            return null;
        }

        private static SqlStringCandidate? TryExtractChain(
            SqlStringFlow.Chain chain,
            SqlStringFlow.Fragment owner,
            SemanticModel? semanticModel,
            SqlDetectionMode mode,
            int depth,
            CancellationToken cancellationToken,
            out bool unknownPrefix)
        {
            unknownPrefix = false;
            var forced = false;
            var start = int.MaxValue;
            var end = 0;
            var builder = new SqlTextBuilder(semanticModel, depth, cancellationToken);
            foreach (var fragment in chain.Fragments)
            {
                if (fragment.Anchor != null)
                {
                    var directives = GetDirectives(fragment.Anchor);
                    if ((directives & Directive.Ignore) != 0)
                    {
                        return null;
                    }

                    forced |= (directives & Directive.LangSql) != 0;
                }

                start = Math.Min(start, fragment.Span.Start);
                end = Math.Max(end, fragment.Span.End);
                builder.BeginFragment();
                switch (fragment.Kind)
                {
                    case SqlStringFlow.FragmentKind.Expression:
                        builder.AddExpression(fragment.Anchor!);
                        break;
                    case SqlStringFlow.FragmentKind.SelfAppend:
                        builder.AddExpression(fragment.Anchor!, skipFirstLeaf: true);
                        break;
                    case SqlStringFlow.FragmentKind.Format:
                        builder.AddFormatCall(fragment.Call!, fragment.FormatIndex, substituteArguments: true);
                        break;
                    case SqlStringFlow.FragmentKind.Text:
                        builder.AddText(fragment.Text!, fragment.Span);
                        break;
                    default:
                        builder.AddPlaceholder(fragment.Span);
                        break;
                }
            }

            if (!builder.TryBuild(out var sql, out var map))
            {
                return null;
            }

            if (!SqlHeuristics.StartsWithStatementKeyword(sql) && !forced)
            {
                unknownPrefix = true;
                return null;
            }

            var known = forced || mode == SqlDetectionMode.Lenient || IsSqlName(chain.Name) || IsSqlContext(owner.Anchor!);
            if (!SqlHeuristics.LooksLikeSql(sql, known) && !forced)
            {
                return null;
            }

            return new SqlStringCandidate(owner.Anchor!, sql, map, known && mode != SqlDetectionMode.Lenient, TextSpan.FromBounds(start, end));
        }

        /// <summary>
        /// True when <paramref name="expression"/> (the initializer of an inlined local) is reported as SQL
        /// on its own, so whoever inlines it should not report the same problems again.
        /// </summary>
        internal static bool IsAnalyzedOnItsOwn(ExpressionSyntax expression, SemanticModel? semanticModel, int depth, CancellationToken cancellationToken)
        {
            var root = FirstRootCandidate(expression);
            return IsRootStringExpression(root) &&
                TryExtractCore(root, semanticModel, SqlDetectionMode.Diagnostics, containing: false, depth, cancellationToken) != null;
        }

        // ------------------------------------------------------------------ shape

        /// <summary>
        /// Climbs from a string expression to the expression whose value is the whole string: through
        /// <c>+</c>, the format argument of <c>string.Format</c> and the arguments of <c>string.Concat</c>.
        /// </summary>
        internal static ExpressionSyntax Outer(ExpressionSyntax node)
        {
            var current = node;
            while (true)
            {
                var parent = SkipParenthesesUp(current).Parent;
                if (parent is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.AddExpression))
                {
                    current = binary;
                    continue;
                }

                if (parent is ArgumentSyntax { Parent: ArgumentListSyntax list } argument && list.Parent is InvocationExpressionSyntax invocation &&
                    (IsStringConcat(invocation) || (IsStringFormat(invocation, out var formatIndex) && list.Arguments.IndexOf(argument) == formatIndex)))
                {
                    current = invocation;
                    continue;
                }

                return current;
            }
        }

        /// <summary>The node that represents <paramref name="outer"/> among the analyzer's root nodes.</summary>
        private static ExpressionSyntax FirstRootCandidate(ExpressionSyntax outer)
        {
            outer = SkipParentheses(outer);
            if (outer is InvocationExpressionSyntax invocation)
            {
                if (IsStringFormat(invocation, out var formatIndex) && formatIndex < invocation.ArgumentList.Arguments.Count)
                {
                    return FirstRootCandidate(invocation.ArgumentList.Arguments[formatIndex].Expression);
                }

                if (IsStringConcat(invocation))
                {
                    foreach (var argument in invocation.ArgumentList.Arguments)
                    {
                        if (SqlStringFlow.IsStringShaped(argument.Expression))
                        {
                            return FirstRootCandidate(argument.Expression);
                        }
                    }
                }
            }

            return outer;
        }

        private static bool IsStringShapedOuter(ExpressionSyntax outer) => SqlStringFlow.IsStringShaped(outer);

        internal static void CollectLeaves(ExpressionSyntax node, List<ExpressionSyntax> leaves)
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

        internal static bool ContainsStringLeaf(BinaryExpressionSyntax node)
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
            foreach (var leaf in leaves)
            {
                if (IsStringLeaf(leaf))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>A (non-UTF-8) string literal, interpolated string, or <c>string.Format</c>/<c>Concat</c> call.</summary>
        internal static bool IsStringLeaf(ExpressionSyntax node) =>
            (node.IsKind(SyntaxKind.StringLiteralExpression) && !IsUtf8(((LiteralExpressionSyntax)node).Token)) ||
            node.IsKind(SyntaxKind.InterpolatedStringExpression) ||
            (node is InvocationExpressionSyntax invocation && (IsStringFormat(invocation, out _) || IsStringConcat(invocation)));

        internal static bool IsUtf8(SyntaxToken token) =>
            token.IsKind(SyntaxKind.Utf8StringLiteralToken) ||
            token.IsKind(SyntaxKind.Utf8SingleLineRawStringLiteralToken) ||
            token.IsKind(SyntaxKind.Utf8MultiLineRawStringLiteralToken);

        /// <summary><c>string.Format(format, ...)</c> or <c>string.Format(provider, format, ...)</c>.</summary>
        internal static bool IsStringFormat(InvocationExpressionSyntax invocation, out int formatIndex)
        {
            formatIndex = 0;
            if (!(invocation.Expression is MemberAccessExpressionSyntax access) || access.Name.Identifier.ValueText != "Format" ||
                !IsStringType(access.Expression))
            {
                return false;
            }

            var arguments = invocation.ArgumentList.Arguments;
            if (arguments.Count >= 2 && !SqlStringFlow.IsStringShaped(arguments[0].Expression) && SqlStringFlow.IsStringShaped(arguments[1].Expression))
            {
                formatIndex = 1;
            }

            return arguments.Count > 0;
        }

        internal static bool IsStringConcat(InvocationExpressionSyntax invocation) =>
            invocation.Expression is MemberAccessExpressionSyntax access && access.Name.Identifier.ValueText == "Concat" &&
            IsStringType(access.Expression) && invocation.ArgumentList.Arguments.Count > 0;

        internal static bool IsStringEmpty(MemberAccessExpressionSyntax member) =>
            member.Name.Identifier.ValueText == "Empty" && IsStringType(member.Expression);

        private static bool IsStringType(ExpressionSyntax expression) => expression switch
        {
            PredefinedTypeSyntax predefined => predefined.Keyword.IsKind(SyntaxKind.StringKeyword),
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText == "String",
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText == "String" && member.Expression is IdentifierNameSyntax { Identifier.ValueText: "System" },
            _ => false,
        };

        /// <summary>
        /// Recognizes the format string argument of a call that treats <c>{0}</c> items specially:
        /// <c>string.Format</c> and <c>AppendFormat</c> (items are replaced by the argument text) or EF's
        /// <c>FromSqlRaw</c>/<c>ExecuteSqlRaw</c>/... (items become parameters).
        /// </summary>
        internal static bool TryGetFormatCall(ExpressionSyntax format, out InvocationExpressionSyntax invocation, out int formatIndex, out bool substituteArguments)
        {
            invocation = null!;
            formatIndex = 0;
            substituteArguments = false;
            if (!(SkipParenthesesUp(format).Parent is ArgumentSyntax { Parent: ArgumentListSyntax list } argument) ||
                !(list.Parent is InvocationExpressionSyntax call))
            {
                return false;
            }

            var index = list.Arguments.IndexOf(argument);
            var name = MethodName(call.Expression);
            if (IsStringFormat(call, out var stringFormatIndex))
            {
                if (index != stringFormatIndex)
                {
                    return false;
                }

                substituteArguments = true;
            }
            else if (name == "AppendFormat")
            {
                if (index > 1 || (index == 1 && SqlStringFlow.IsStringShaped(list.Arguments[0].Expression)))
                {
                    return false;
                }

                substituteArguments = true;
            }
            else if (name == null || !s_formatParameterMethodNames.Contains(name) || index > 1)
            {
                return false;
            }

            invocation = call;
            formatIndex = index;
            return true;
        }

        // ------------------------------------------------------------------ gate

        /// <summary>The text at the start of the string, for the cheap keyword check; null when unknown.</summary>
        private static string? FirstText(ExpressionSyntax expression, SemanticModel? semanticModel, int depth, CancellationToken cancellationToken)
        {
            expression = SkipParentheses(expression);
            switch (expression)
            {
                case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                    return IsUtf8(literal.Token) ? null : literal.Token.ValueText;

                case InterpolatedStringExpressionSyntax interpolated:
                    return interpolated.Contents.Count > 0 && interpolated.Contents[0] is InterpolatedStringTextSyntax text
                        ? text.TextToken.ValueText
                        : null;

                case InvocationExpressionSyntax invocation when IsStringFormat(invocation, out var formatIndex):
                    return formatIndex < invocation.ArgumentList.Arguments.Count
                        ? FirstText(invocation.ArgumentList.Arguments[formatIndex].Expression, semanticModel, depth, cancellationToken)
                        : null;

                case InvocationExpressionSyntax invocation when IsStringConcat(invocation):
                    return FirstText(invocation.ArgumentList.Arguments[0].Expression, semanticModel, depth, cancellationToken);

                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression):
                    var leaves = new List<ExpressionSyntax>(4);
                    CollectLeaves(binary, leaves);
                    if (IsStringLeaf(leaves[0]))
                    {
                        return FirstText(leaves[0], semanticModel, depth, cancellationToken);
                    }

                    // "BaseQuery + \" WHERE ...\"": only worth resolving when the rest looks like SQL clauses.
                    return leaves.Count >= 2 && OtherLeavesLookLikeSqlClauses(leaves)
                        ? OperandText(leaves[0], semanticModel, depth, cancellationToken)
                        : null;

                default:
                    return OperandText(expression, semanticModel, depth, cancellationToken);
            }
        }

        private static string? OperandText(ExpressionSyntax operand, SemanticModel? semanticModel, int depth, CancellationToken cancellationToken)
        {
            if (semanticModel != null)
            {
                var constant = semanticModel.GetConstantValue(operand, cancellationToken);
                if (constant.HasValue)
                {
                    return constant.Value as string;
                }
            }

            return operand is IdentifierNameSyntax identifier && depth < SqlTextBuilder.MaxInlineDepth &&
                   SqlStringFlow.TryFindInlinableLocal(identifier, out var initializer)
                ? FirstText(initializer, semanticModel, depth + 1, cancellationToken)
                : null;
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

        internal static LiteralFlavor FlavorOf(SyntaxToken token)
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

        internal static ExpressionSyntax SkipParentheses(ExpressionSyntax node)
        {
            while (node is ParenthesizedExpressionSyntax parenthesized)
            {
                node = parenthesized.Expression;
            }

            return node;
        }

        private static SyntaxNode SkipParenthesesUp(SyntaxNode node)
        {
            while (node.Parent is ParenthesizedExpressionSyntax parenthesized)
            {
                node = parenthesized;
            }

            return node;
        }

        // ------------------------------------------------------------------ comment directives

        [Flags]
        private enum Directive
        {
            None = 0,
            LangSql = 1,
            Ignore = 2,
        }

        /// <summary>True when a <c>sqllense:ignore</c> comment applies to the expression.</summary>
        internal static bool IsIgnored(ExpressionSyntax expression) => (GetDirectives(expression) & Directive.Ignore) != 0;

        private static bool HasDirective(ExpressionSyntax node, ExpressionSyntax outer, Directive directive) =>
            (GetDirectives(node, outer) & directive) != 0;

        private static Directive GetDirectives(ExpressionSyntax node, ExpressionSyntax outer) =>
            node == outer ? GetDirectives(outer) : GetDirectives(outer) | GetDirectives(node);

        /// <summary>
        /// Looks for <c>/*lang=sql*/</c> or <c>// sqllense:ignore</c> style comments immediately before the
        /// string, or on the line above the containing statement or member.
        /// </summary>
        private static Directive GetDirectives(ExpressionSyntax root)
        {
            var first = root.GetFirstToken();
            var found = Scan(first.LeadingTrivia);

            // Finding the previous token is a tree walk; only do it when a comment can be there.
            if (MayEndWithComment(first))
            {
                found |= Scan(first.GetPreviousToken().TrailingTrivia);
            }

            for (SyntaxNode? node = root.Parent; node != null; node = node.Parent)
            {
                if (node is StatementSyntax || node is MemberDeclarationSyntax)
                {
                    found |= Scan(node.GetFirstToken().LeadingTrivia);
                    break;
                }
            }

            return found;
        }

        /// <summary>
        /// The previous token's trailing trivia is the rest of its line. Checks that line (the current one,
        /// or the one above when <paramref name="token"/>'s trivia starts a line) for <c>//</c> or <c>*/</c>.
        /// </summary>
        private static bool MayEndWithComment(SyntaxToken token)
        {
            if (token.SyntaxTree == null || !token.SyntaxTree.TryGetText(out var text))
            {
                return true;
            }

            var i = Math.Min(token.FullSpan.Start, text.Length) - 1;
            if (i >= 0 && text[i] == '\n')
            {
                i--;
            }

            if (i >= 0 && text[i] == '\r')
            {
                i--;
            }

            for (; i > 0 && text[i] != '\n'; i--)
            {
                // The end of a /* */ comment, or a // comment.
                if (text[i] == '/' && (text[i - 1] == '*' || text[i - 1] == '/'))
                {
                    return true;
                }
            }

            return false;
        }

        private static Directive Scan(SyntaxTriviaList trivia)
        {
            var found = Directive.None;
            foreach (var t in trivia)
            {
                if (t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia))
                {
                    found |= Matches(t);
                }
            }

            return found;
        }

        private static Directive Matches(SyntaxTrivia comment)
        {
            var text = comment.ToString();
            if (text.IndexOf("sqllense:ignore", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("sqllense-ignore", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Directive.Ignore;
            }

            return IsLangSqlComment(text) ? Directive.LangSql : Directive.None;
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
