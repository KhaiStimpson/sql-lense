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
    /// <summary>
    /// Turns C# string expressions into SQL text plus a <see cref="SqlSourceMap"/>. Literal text maps
    /// back to the literal; interpolation holes, format items and unknown operands become placeholders.
    /// </summary>
    internal sealed class SqlTextBuilder
    {
        /// <summary>How deep locals are inlined (<c>var a = "..."; var b = a + "...";</c>).</summary>
        internal const int MaxInlineDepth = 3;

        private readonly SemanticModel? _semanticModel;
        private readonly CancellationToken _cancellationToken;
        private readonly int _depth;
        private readonly List<Part> _parts = new List<Part>();
        private List<CompositeFormat.Item>? _formatItems;
        private int _group;
        private int _nextGroup;
        private bool _reportedElsewhere;
        private int _inlineDepth;

        public SqlTextBuilder(SemanticModel? semanticModel, int depth, CancellationToken cancellationToken)
        {
            _semanticModel = semanticModel;
            _depth = depth;
            _cancellationToken = cancellationToken;
        }

        /// <summary>Set when the expression cannot be SQL text (a UTF-8 literal).</summary>
        public bool Failed { get; private set; }

        /// <summary>Starts a new fragment (one statement of a built-up string).</summary>
        public void BeginFragment() => _group = ++_nextGroup;

        /// <summary>Adds a string expression: a literal, interpolation, concatenation, format call or operand.</summary>
        public void AddExpression(ExpressionSyntax expression, bool skipFirstLeaf = false)
        {
            var leaves = new List<ExpressionSyntax>(4);
            SqlStringExtractor.CollectLeaves(expression, leaves);
            for (var i = skipFirstLeaf ? 1 : 0; i < leaves.Count; i++)
            {
                AddLeaf(leaves[i]);
            }
        }

        /// <summary>Adds the expansion of <c>receiver.AppendFormat(format, args...)</c> or <c>string.Format</c>.</summary>
        public void AddFormatCall(InvocationExpressionSyntax invocation, int formatIndex, bool substituteArguments)
        {
            var arguments = invocation.ArgumentList.Arguments;
            if (formatIndex >= arguments.Count)
            {
                AddPlaceholder(invocation.Span);
                return;
            }

            var format = SqlStringExtractor.SkipParentheses(arguments[formatIndex].Expression);
            if (format is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                AddFormatLiteral(literal, invocation, formatIndex, substituteArguments);
            }
            else
            {
                AddExpression(format);
            }
        }

        public void AddText(string text, TextSpan span) =>
            _parts.Add(Part.Opaque(text, span, _group, _reportedElsewhere));

        public void AddPlaceholder(TextSpan span) =>
            _parts.Add(Part.Opaque(null, span, _group, _reportedElsewhere));

        public bool TryBuild(out string sql, out SqlSourceMap map)
        {
            if (Failed)
            {
                sql = string.Empty;
                map = null!;
                return false;
            }

            var sb = new StringBuilder();
            var segments = new List<SqlSourceMap.Segment>(_parts.Count);
            var placeholderIndex = 0;
            for (var i = 0; i < _parts.Count; i++)
            {
                var part = _parts[i];
                var start = sb.Length;
                if (part.IsLiteral)
                {
                    sb.Append(part.Text);
                    segments.Add(SqlSourceMap.Segment.Literal(
                        start, part.Token, part.Text!, part.Flavor, part.DoubledBraces, part.ValueStart, part.ValueMap, part.Group, part.ReportedElsewhere));
                    continue;
                }

                var text = part.Text ?? Placeholder(sb, NextText(i), placeholderIndex++);
                sb.Append(text);
                segments.Add(SqlSourceMap.Segment.Opaque(start, text.Length, part.Span, part.Group, part.ReportedElsewhere));
            }

            sql = sb.ToString();
            map = new SqlSourceMap(segments);
            return true;
        }

        // ------------------------------------------------------------------ leaves

        private void AddLeaf(ExpressionSyntax leaf)
        {
            switch (leaf)
            {
                case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                    if (SqlStringExtractor.IsUtf8(literal.Token))
                    {
                        Failed = true;
                        return;
                    }

                    if (SqlStringExtractor.TryGetFormatCall(literal, out var call, out var formatIndex, out var substitute))
                    {
                        AddFormatLiteral(literal, call, formatIndex, substitute);
                    }
                    else
                    {
                        AddFormatLiteral(literal, invocation: null, 0, substituteArguments: false);
                    }

                    return;

                case LiteralExpressionSyntax character when character.IsKind(SyntaxKind.CharacterLiteralExpression):
                    AddText(character.Token.ValueText, character.Span);
                    return;

                case InterpolatedStringExpressionSyntax interpolated:
                    AddInterpolated(interpolated);
                    return;

                case InvocationExpressionSyntax invocation when SqlStringExtractor.IsStringFormat(invocation, out var index):
                    AddFormatCall(invocation, index, substituteArguments: true);
                    return;

                case InvocationExpressionSyntax invocation when SqlStringExtractor.IsStringConcat(invocation):
                    foreach (var argument in invocation.ArgumentList.Arguments)
                    {
                        AddExpression(argument.Expression);
                    }

                    return;

                case MemberAccessExpressionSyntax member when SqlStringExtractor.IsStringEmpty(member):
                    AddText(string.Empty, member.Span);
                    return;

                default:
                    AddOperand(leaf, leaf.Span);
                    return;
            }
        }

        private void AddInterpolated(InterpolatedStringExpressionSyntax interpolated)
        {
            var flavor = SqlStringExtractor.FlavorOf(interpolated.StringStartToken);
            var doubledBraces = flavor != LiteralFlavor.Raw;
            foreach (var content in interpolated.Contents)
            {
                if (content is InterpolatedStringTextSyntax text)
                {
                    AddLiteral(text.TextToken, text.TextToken.ValueText, flavor, doubledBraces, 0, null);
                }
                else if (content is InterpolationSyntax hole && hole.AlignmentClause == null && hole.FormatClause == null)
                {
                    AddOperand(hole.Expression, content.Span);
                }
                else
                {
                    AddPlaceholder(content.Span);
                }
            }
        }

        private void AddLiteral(SyntaxToken token, string text, LiteralFlavor flavor, bool doubledBraces, int valueStart, int[]? valueMap) =>
            _parts.Add(Part.Literal(text, token, flavor, doubledBraces, valueStart, valueMap, _group, _reportedElsewhere));

        /// <summary>
        /// Adds a regular, verbatim or raw literal. Inside a format call, or when the text has
        /// <c>{0}</c>-style items, the items become placeholders (or the matching argument's text).
        /// </summary>
        private void AddFormatLiteral(LiteralExpressionSyntax literal, InvocationExpressionSyntax? invocation, int formatIndex, bool substituteArguments)
        {
            var token = literal.Token;
            var value = token.ValueText;
            var flavor = SqlStringExtractor.FlavorOf(token);
            var isFormatCall = invocation != null;
            if ((isFormatCall ? value.IndexOf('{') < 0 && value.IndexOf('}') < 0 : !CompositeFormat.MayContainHole(value)) ||
                !CompositeFormat.TryParse(value, _formatItems ??= new List<CompositeFormat.Item>()))
            {
                AddLiteral(token, value, flavor, false, 0, null);
                return;
            }

            var items = _formatItems;
            if (!isFormatCall && !HasHole(items))
            {
                AddLiteral(token, value, flavor, false, 0, null);
                return;
            }

            // Take the list: operands may be inlined recursively, and those need a list of their own.
            _formatItems = null;
            int[]? offsets = null;
            foreach (var item in items)
            {
                if (!item.IsHole)
                {
                    var contiguous = item.End - item.Start == item.Text!.Length;
                    AddLiteral(token, item.Text, flavor, false, contiguous ? item.Start : 0, contiguous ? null : item.ValueMap);
                    continue;
                }

                offsets ??= SqlSourceMap.ComputeTokenOffsets(token, flavor, doubledBraces: false);
                var span = TextSpan.FromBounds(token.SpanStart + offsets[item.Start], token.SpanStart + offsets[item.End]);
                var argumentIndex = formatIndex + 1 + item.ArgumentIndex;
                if (substituteArguments && invocation != null && argumentIndex < invocation.ArgumentList.Arguments.Count)
                {
                    AddOperand(invocation.ArgumentList.Arguments[argumentIndex].Expression, span);
                }
                else
                {
                    AddPlaceholder(span);
                }
            }

            _formatItems = items;
        }

        private static bool HasHole(List<CompositeFormat.Item> items)
        {
            foreach (var item in items)
            {
                if (item.IsHole)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>A non-literal operand: a constant, a local holding a string expression, or a placeholder.</summary>
        private void AddOperand(ExpressionSyntax operand, TextSpan span)
        {
            if (_semanticModel != null)
            {
                var constant = _semanticModel.GetConstantValue(operand, _cancellationToken);
                if (constant.HasValue && constant.Value is string text)
                {
                    AddText(text, span);
                    return;
                }
            }

            if (operand is IdentifierNameSyntax identifier &&
                _depth + _inlineDepth < MaxInlineDepth &&
                SqlStringFlow.TryFindInlinableLocal(identifier, out var initializer) &&
                !SqlStringExtractor.IsIgnored(initializer))
            {
                var savedGroup = _group;
                var savedReported = _reportedElsewhere;
                _group = ++_nextGroup;
                _reportedElsewhere |= SqlStringExtractor.IsAnalyzedOnItsOwn(initializer, _semanticModel, _depth + _inlineDepth + 1, _cancellationToken);
                _inlineDepth++;
                AddExpression(initializer);
                _inlineDepth--;
                _group = savedGroup;
                _reportedElsewhere = savedReported;
                return;
            }

            AddPlaceholder(span);
        }

        private string? NextText(int index)
        {
            for (var i = index + 1; i < _parts.Count; i++)
            {
                var text = _parts[i].Text;
                if (text == null || text.Length > 0)
                {
                    return text;
                }
            }

            return null;
        }

        // ------------------------------------------------------------------ placeholders

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

        private readonly struct Part
        {
            private Part(
                string? text, SyntaxToken token, LiteralFlavor flavor, bool doubledBraces, TextSpan span, bool isLiteral,
                int valueStart, int[]? valueMap, int group, bool reportedElsewhere)
            {
                Text = text;
                Token = token;
                Flavor = flavor;
                DoubledBraces = doubledBraces;
                Span = span;
                IsLiteral = isLiteral;
                ValueStart = valueStart;
                ValueMap = valueMap;
                Group = group;
                ReportedElsewhere = reportedElsewhere;
            }

            /// <summary>Null for a placeholder (interpolation hole, format item or unknown operand).</summary>
            public string? Text { get; }

            public SyntaxToken Token { get; }

            public LiteralFlavor Flavor { get; }

            public bool DoubledBraces { get; }

            public TextSpan Span { get; }

            public bool IsLiteral { get; }

            public int ValueStart { get; }

            public int[]? ValueMap { get; }

            public int Group { get; }

            public bool ReportedElsewhere { get; }

            public static Part Literal(
                string text, SyntaxToken token, LiteralFlavor flavor, bool doubledBraces, int valueStart, int[]? valueMap, int group, bool reportedElsewhere) =>
                new Part(text, token, flavor, doubledBraces, token.Span, isLiteral: true, valueStart, valueMap, group, reportedElsewhere);

            public static Part Opaque(string? text, TextSpan span, int group, bool reportedElsewhere) =>
                new Part(text, default, default, false, span, isLiteral: false, 0, null, group, reportedElsewhere);
        }
    }
}
