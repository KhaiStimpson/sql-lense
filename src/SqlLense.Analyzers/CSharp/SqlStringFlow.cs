using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SqlLense.Analyzers.CSharp
{
    /// <summary>
    /// Follows a local <c>string</c> or <c>StringBuilder</c> through the statements that build it up
    /// (<c>+=</c>, <c>x = x + ...</c>, <c>Append</c>/<c>AppendLine</c>/<c>AppendFormat</c>), so the
    /// pieces can be validated as one SQL statement. Purely syntactic and bounded to one block.
    /// </summary>
    /// <remarks>
    /// Control flow is approximated: the body of an <c>if</c> is included (optional filters), only the
    /// first branch of an <c>if</c>/<c>else</c> or <c>switch</c> is followed, and a loop that appends
    /// becomes a single placeholder. Anything else that touches the variable ends the chain.
    /// </remarks>
    internal static class SqlStringFlow
    {
        private const int MaxStatements = 512;
        private const int MaxFragments = 128;

        internal enum FragmentKind
        {
            /// <summary>A string expression appended as is.</summary>
            Expression,

            /// <summary><c>x = x + rest</c>: every leaf but the first.</summary>
            SelfAppend,

            /// <summary><c>sb.AppendFormat(format, args)</c>.</summary>
            Format,

            /// <summary>Fixed text such as the newline of <c>AppendLine</c>.</summary>
            Text,

            /// <summary>Unknown text, such as everything a loop appends.</summary>
            Placeholder,
        }

        internal sealed class Fragment
        {
            public Fragment(FragmentKind kind, ExpressionSyntax? anchor, InvocationExpressionSyntax? call, int formatIndex, string? text, TextSpan span)
            {
                Kind = kind;
                Anchor = anchor;
                Call = call;
                FormatIndex = formatIndex;
                Text = text;
                Span = span;
            }

            public FragmentKind Kind { get; }

            /// <summary>The string expression this fragment came from (what <see cref="SqlStringExtractor"/> sees as its root).</summary>
            public ExpressionSyntax? Anchor { get; }

            public InvocationExpressionSyntax? Call { get; }

            public int FormatIndex { get; }

            public string? Text { get; }

            public TextSpan Span { get; }
        }

        internal sealed class Chain
        {
            public Chain(string name, bool isBuilder)
            {
                Name = name;
                IsBuilder = isBuilder;
            }

            public string Name { get; }

            public bool IsBuilder { get; }

            public List<Fragment> Fragments { get; } = new List<Fragment>();

            public bool Truncated { get; set; }
        }

        /// <summary>
        /// Cheap check: does the string expression flow into a local variable (initializer, assignment,
        /// <c>+=</c>, <c>new StringBuilder(...)</c> or an <c>Append</c> call)?
        /// </summary>
        public static bool TryGetTarget(ExpressionSyntax outer, out string name, out StatementSyntax statement) =>
            TryGetTarget(outer, out name, out statement, out _);

        /// <param name="isDefinition">
        /// True when the expression gives the variable its value (initializer or plain assignment), so it
        /// only starts a chain if something later appends to the variable.
        /// </param>
        public static bool TryGetTarget(ExpressionSyntax outer, out string name, out StatementSyntax statement, out bool isDefinition)
        {
            name = string.Empty;
            statement = null!;
            isDefinition = false;
            var node = SkipParenthesesUp(outer);
            switch (node.Parent)
            {
                case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator } equals
                    when declarator.Parent?.Parent is LocalDeclarationStatementSyntax local && !local.IsConst:
                    name = declarator.Identifier.ValueText;
                    statement = local;
                    isDefinition = true;
                    return true;

                case AssignmentExpressionSyntax { Left: IdentifierNameSyntax left } assignment
                    when assignment.Right == node && assignment.Parent is ExpressionStatementSyntax es &&
                         (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) || assignment.IsKind(SyntaxKind.AddAssignmentExpression)):
                    name = left.Identifier.ValueText;
                    statement = es;
                    isDefinition = assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) && !StartsWith(node, name);
                    return true;

                case ArgumentSyntax { Parent: ArgumentListSyntax list } argument:
                    if (list.Parent is BaseObjectCreationExpressionSyntax creation && list.Arguments.IndexOf(argument) == 0 &&
                        SkipParenthesesUp(creation).Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax builder } &&
                        builder.Parent?.Parent is LocalDeclarationStatementSyntax builderLocal && IsStringBuilderCreation(creation, builder))
                    {
                        name = builder.Identifier.ValueText;
                        statement = builderLocal;
                        isDefinition = true;
                        return true;
                    }

                    if (list.Parent is InvocationExpressionSyntax invocation &&
                        TryGetBuilderStatement(invocation, out var receiver, out var expressionStatement))
                    {
                        name = receiver;
                        statement = expressionStatement;
                        return true;
                    }

                    return false;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Allocation-free pre-check for a definition: is the variable appended to (<c>x +=</c>,
        /// <c>x = x +</c>, <c>x.Append...</c>) anywhere after <paramref name="statement"/> in its block?
        /// Most strings are assigned once and never appended to, and skip the statement walk.
        /// </summary>
        public static bool IsAppendedAfter(StatementSyntax statement, string name)
        {
            var block = statement.Parent;
            if ((!(block is BlockSyntax) && !(block is SwitchSectionSyntax)) || name.Length == 0 ||
                !statement.SyntaxTree.TryGetText(out var text))
            {
                return block is BlockSyntax || block is SwitchSectionSyntax;
            }

            // A character scan of the source rather than a token walk: this runs for every string that
            // starts with a keyword, and a false match only costs the (exact) statement walk.
            var start = statement.Span.End;
            var length = Math.Min(block.Span.End, text.Length) - start;
            if (length <= name.Length)
            {
                return false;
            }

            var buffer = s_scanBuffer;
            if (buffer == null || buffer.Length < length)
            {
                s_scanBuffer = buffer = new char[Math.Max(length, 1024)];
            }

            text.CopyTo(start, buffer, 0, length);
            var end = length - name.Length;
            for (var i = 0; i < end; i++)
            {
                if (buffer[i] != name[0] || (i > 0 && IsIdentifierChar(buffer[i - 1])) || IsIdentifierChar(buffer[i + name.Length]) ||
                    !Matches(buffer, i, length, name))
                {
                    continue;
                }

                var j = SkipWhitespace(buffer, i + name.Length, length);
                if (j >= length - 1)
                {
                    continue;
                }

                if (buffer[j] == '+' && buffer[j + 1] == '=')
                {
                    return true;
                }

                if (buffer[j] == '.' && Matches(buffer, SkipWhitespace(buffer, j + 1, length), length, "Append"))
                {
                    return true;
                }

                if (buffer[j] == '=' && buffer[j + 1] != '=')
                {
                    var k = SkipWhitespace(buffer, j + 1, length);
                    if (Matches(buffer, k, length, name) && k + name.Length < length && !IsIdentifierChar(buffer[k + name.Length]))
                    {
                        k = SkipWhitespace(buffer, k + name.Length, length);
                        if (k < length && buffer[k] == '+')
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        [ThreadStatic]
        private static char[]? s_scanBuffer;

        /// <summary>
        /// Positions in <paramref name="scope"/> where the name appears as a word. A text scan, so it may
        /// include comments and strings; each position is confirmed against the token there.
        /// </summary>
        private static List<int> FindOccurrences(SyntaxNode scope, string name)
        {
            var result = new List<int>();
            var start = scope.Span.Start;
            if (!scope.SyntaxTree.TryGetText(out var text))
            {
                foreach (var token in scope.DescendantTokens())
                {
                    if (token.IsKind(SyntaxKind.IdentifierToken) && token.ValueText == name)
                    {
                        result.Add(token.SpanStart);
                    }
                }

                return result;
            }

            var length = Math.Min(scope.Span.End, text.Length) - start;
            var buffer = s_scanBuffer;
            if (buffer == null || buffer.Length < length)
            {
                s_scanBuffer = buffer = new char[Math.Max(length, 1024)];
            }

            text.CopyTo(start, buffer, 0, length);
            for (var i = 0; i + name.Length <= length; i++)
            {
                if (buffer[i] == name[0] && (i == 0 || !IsIdentifierChar(buffer[i - 1])) &&
                    (i + name.Length == length || !IsIdentifierChar(buffer[i + name.Length])) && Matches(buffer, i, length, name))
                {
                    // A verbatim identifier (@name) starts one character earlier.
                    result.Add(start + (i > 0 && buffer[i - 1] == '@' ? i - 1 : i));
                }
            }

            return result;
        }

        private static bool Matches(char[] buffer, int index, int length, string value)
        {
            if (index < 0 || index + value.Length > length)
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                if (buffer[index + i] != value[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static int SkipWhitespace(char[] buffer, int index, int length)
        {
            while (index < length && char.IsWhiteSpace(buffer[index]))
            {
                index++;
            }

            return index;
        }

        private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary><c>name + ...</c></summary>
        private static bool StartsWith(SyntaxNode expression, string name)
        {
            while (expression is ParenthesizedExpressionSyntax || (expression is BinaryExpressionSyntax add && add.IsKind(SyntaxKind.AddExpression)))
            {
                expression = expression is ParenthesizedExpressionSyntax p ? p.Expression : ((BinaryExpressionSyntax)expression).Left;
            }

            return expression is IdentifierNameSyntax identifier && identifier.Identifier.ValueText == name;
        }

        /// <summary>Finds the chain that <paramref name="outer"/> is a fragment of, or null.</summary>
        public static Chain? FindChain(ExpressionSyntax outer, SemanticModel? semanticModel, CancellationToken cancellationToken)
        {
            if (!TryGetTarget(outer, out var name, out var statement) ||
                !TryFindDeclaration(statement, name, out _, out var statements, out var index))
            {
                return null;
            }

            var walker = new Walker(name, outer, statements[index].Parent!, semanticModel, cancellationToken);
            walker.WalkStatements(statements, index, conditional: false);
            var target = walker.Target;
            return target == null || target.Truncated ? null : target;
        }

        /// <summary>
        /// True for a local that is initialized with a string expression and never assigned again, so its
        /// initializer can stand in for it: <c>var where = "WHERE Id = @id"; ... $"SELECT ... {where}"</c>.
        /// </summary>
        public static bool TryFindInlinableLocal(IdentifierNameSyntax identifier, out ExpressionSyntax initializer)
        {
            initializer = null!;
            if (identifier.Parent is MemberAccessExpressionSyntax member && member.Name == identifier)
            {
                return false;
            }

            var name = identifier.Identifier.ValueText;
            if (!TryFindDeclaration(identifier, name, out var declaration, out _, out _))
            {
                return false;
            }

            VariableDeclaratorSyntax? declarator = null;
            foreach (var variable in declaration.Declaration.Variables)
            {
                if (variable.Identifier.ValueText == name)
                {
                    declarator = variable;
                }
            }

            var value = declarator?.Initializer?.Value;
            if (value == null || !IsStringShaped(value))
            {
                return false;
            }

            var scope = (SyntaxNode?)declaration.Parent;
            for (var node = scope; node != null; node = node.Parent)
            {
                if (node is MemberDeclarationSyntax || node is AnonymousFunctionExpressionSyntax || node is LocalFunctionStatementSyntax)
                {
                    scope = node;
                    break;
                }
            }

            if (scope == null || IsWritten(scope, name))
            {
                return false;
            }

            initializer = value;
            return true;
        }

        /// <summary>A literal, interpolation, concatenation with a string leaf, or <c>string.Format</c>/<c>Concat</c>.</summary>
        internal static bool IsStringShaped(ExpressionSyntax expression)
        {
            expression = SqlStringExtractor.SkipParentheses(expression);
            return expression is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.AddExpression)
                ? SqlStringExtractor.ContainsStringLeaf(binary)
                : SqlStringExtractor.IsStringLeaf(expression);
        }

        // ------------------------------------------------------------------ declarations

        /// <summary>
        /// Finds the local declaration of <paramref name="name"/> in scope at <paramref name="node"/>:
        /// the statement containing it, or one before it, in this or an enclosing block of the same member.
        /// </summary>
        private static bool TryFindDeclaration(
            SyntaxNode node,
            string name,
            out LocalDeclarationStatementSyntax declaration,
            out SyntaxList<StatementSyntax> statements,
            out int index)
        {
            declaration = null!;
            statements = default;
            index = -1;
            var child = node;
            for (var parent = node.Parent; parent != null; child = parent, parent = parent.Parent)
            {
                SyntaxList<StatementSyntax> list;
                switch (parent)
                {
                    case BlockSyntax block:
                        list = block.Statements;
                        break;
                    case SwitchSectionSyntax section:
                        list = section.Statements;
                        break;
                    case MemberDeclarationSyntax:
                        return false;
                    case AnonymousFunctionExpressionSyntax lambda when DeclaresParameter(lambda, name):
                        return false;
                    case LocalFunctionStatementSyntax function when DeclaresParameter(function.ParameterList, name):
                        return false;
                    default:
                        continue;
                }

                // The statement itself counts: a declaration is where a built-up string starts.
                var childIndex = child is StatementSyntax statement ? list.IndexOf(statement) : -1;
                for (var i = childIndex; i >= 0; i--)
                {
                    if (list[i] is LocalDeclarationStatementSyntax local)
                    {
                        foreach (var variable in local.Declaration.Variables)
                        {
                            if (variable.Identifier.ValueText == name)
                            {
                                declaration = local;
                                statements = list;
                                index = i;
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        private static bool DeclaresParameter(AnonymousFunctionExpressionSyntax lambda, string name) => lambda switch
        {
            SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier.ValueText == name,
            ParenthesizedLambdaExpressionSyntax parenthesized => DeclaresParameter(parenthesized.ParameterList, name),
            AnonymousMethodExpressionSyntax method => method.ParameterList != null && DeclaresParameter(method.ParameterList, name),
            _ => false,
        };

        private static bool DeclaresParameter(ParameterListSyntax parameters, string name)
        {
            foreach (var parameter in parameters.Parameters)
            {
                if (parameter.Identifier.ValueText == name)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Any assignment, increment, or <c>ref</c>/<c>out</c> use of the name in <paramref name="scope"/>.</summary>
        private static bool IsWritten(SyntaxNode scope, string name)
        {
            foreach (var token in scope.DescendantTokens())
            {
                if (!token.IsKind(SyntaxKind.IdentifierToken) || token.ValueText != name || !(token.Parent is IdentifierNameSyntax identifier))
                {
                    continue;
                }

                var node = SkipParenthesesUp(identifier);
                switch (node.Parent)
                {
                    case AssignmentExpressionSyntax assignment when assignment.Left == node:
                    case PrefixUnaryExpressionSyntax _:
                    case PostfixUnaryExpressionSyntax _:
                        return true;
                    case ArgumentSyntax argument when !argument.RefKindKeyword.IsKind(SyntaxKind.None):
                        return true;
                }
            }

            return false;
        }

        // ------------------------------------------------------------------ builder calls

        private static bool IsStringBuilderCreation(BaseObjectCreationExpressionSyntax creation, VariableDeclaratorSyntax declarator)
        {
            if (creation is ObjectCreationExpressionSyntax explicitCreation)
            {
                return TypeName(explicitCreation.Type) == "StringBuilder";
            }

            return declarator.Parent is VariableDeclarationSyntax declaration && TypeName(declaration.Type) == "StringBuilder";
        }

        private static string TypeName(TypeSyntax type) => type switch
        {
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
            _ => string.Empty,
        };

        private static bool IsAppendName(string name) => name == "Append" || name == "AppendLine" || name == "AppendFormat";

        /// <summary>
        /// For an <c>Append</c> call in an expression statement such as <c>sb.Append(a).Append(b);</c>, the
        /// builder variable and the statement.
        /// </summary>
        private static bool TryGetBuilderStatement(InvocationExpressionSyntax invocation, out string receiver, out ExpressionStatementSyntax statement)
        {
            receiver = string.Empty;
            statement = null!;
            SyntaxNode outermost = invocation;
            while (outermost.Parent is MemberAccessExpressionSyntax access && access.Parent is InvocationExpressionSyntax next &&
                   IsAppendName(access.Name.Identifier.ValueText))
            {
                outermost = next;
            }

            if (!(outermost.Parent is ExpressionStatementSyntax es))
            {
                return false;
            }

            var calls = new List<InvocationExpressionSyntax>();
            if (!TryCollectBuilderCalls((InvocationExpressionSyntax)outermost, calls, out var name))
            {
                return false;
            }

            receiver = name;
            statement = es;
            return true;
        }

        /// <summary><c>x.Append(a).AppendLine(b)</c> → [Append(a), AppendLine(b)] on <c>x</c>.</summary>
        private static bool TryCollectBuilderCalls(ExpressionSyntax expression, List<InvocationExpressionSyntax> calls, out string receiver)
        {
            receiver = string.Empty;
            while (expression is InvocationExpressionSyntax invocation &&
                   invocation.Expression is MemberAccessExpressionSyntax access &&
                   access.IsKind(SyntaxKind.SimpleMemberAccessExpression))
            {
                calls.Add(invocation);
                expression = access.Expression;
            }

            if (calls.Count == 0 || !(expression is IdentifierNameSyntax identifier))
            {
                return false;
            }

            calls.Reverse();
            receiver = identifier.Identifier.ValueText;
            return true;
        }

        private static SyntaxNode SkipParenthesesUp(SyntaxNode node)
        {
            while (node.Parent is ParenthesizedExpressionSyntax parenthesized)
            {
                node = parenthesized;
            }

            return node;
        }

        // ------------------------------------------------------------------ walking

        private sealed class Walker
        {
            private readonly string _name;
            private readonly ExpressionSyntax _anchor;
            private readonly SemanticModel? _semanticModel;
            private readonly CancellationToken _cancellationToken;
            private readonly List<int> _occurrences;
            private Chain? _current;
            private int _budget = MaxStatements;

            public Walker(string name, ExpressionSyntax anchor, SyntaxNode scope, SemanticModel? semanticModel, CancellationToken cancellationToken)
            {
                _name = name;
                _anchor = anchor;
                _semanticModel = semanticModel;
                _cancellationToken = cancellationToken;
                _occurrences = FindOccurrences(scope, name);
            }

            /// <summary>The chain that contains the anchor, once found.</summary>
            public Chain? Target { get; private set; }

            /// <summary>The anchor's chain is complete: nothing later can add to it.</summary>
            private bool Done => Target != null && _current != Target;

            public void WalkStatements(SyntaxList<StatementSyntax> statements, int start, bool conditional)
            {
                for (var i = start; i < statements.Count && !Done; i++)
                {
                    if (--_budget < 0)
                    {
                        Truncate();
                        return;
                    }

                    WalkStatement(statements[i], conditional);
                }
            }

            private void WalkStatement(StatementSyntax statement, bool conditional)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                switch (statement)
                {
                    case LocalDeclarationStatementSyntax local:
                        VariableDeclaratorSyntax? declarator = null;
                        foreach (var variable in local.Declaration.Variables)
                        {
                            if (variable.Identifier.ValueText == _name)
                            {
                                declarator = variable;
                            }
                        }

                        if (declarator != null)
                        {
                            Start(declarator.Initializer?.Value, declarator);
                        }
                        else if (References(local))
                        {
                            EndAt(local);
                        }

                        break;

                    case ExpressionStatementSyntax expressionStatement:
                        WalkExpressionStatement(expressionStatement, conditional);
                        break;

                    case BlockSyntax block:
                        WalkStatements(block.Statements, 0, conditional);
                        break;

                    case IfStatementSyntax ifStatement:
                        if (!References(ifStatement.Statement) && (ifStatement.Else == null || !References(ifStatement.Else)))
                        {
                            break;
                        }

                        if (_current == null)
                        {
                            break;
                        }

                        // Optional clauses (no else) are included; for if/else only the first branch is followed.
                        WalkStatement(ifStatement.Statement, conditional: true);
                        if (ifStatement.Else != null && !OnlyAppends(ifStatement.Else))
                        {
                            EndAt(ifStatement.Else);
                        }

                        break;

                    case SwitchStatementSyntax switchStatement:
                        if (!References(switchStatement))
                        {
                            break;
                        }

                        if (_current == null)
                        {
                            break;
                        }

                        for (var i = 0; i < switchStatement.Sections.Count; i++)
                        {
                            if (i == 0)
                            {
                                WalkStatements(switchStatement.Sections[0].Statements, 0, conditional: true);
                            }
                            else if (!OnlyAppends(switchStatement.Sections[i]))
                            {
                                EndAt(switchStatement.Sections[i]);
                            }
                        }

                        break;

                    case ForStatementSyntax _:
                    case CommonForEachStatementSyntax _:
                    case WhileStatementSyntax _:
                    case DoStatementSyntax _:
                        if (!References(statement))
                        {
                            break;
                        }

                        // What a loop appends is unknown: one placeholder stands for all of it.
                        if (_current != null && OnlyAppends(statement))
                        {
                            Add(new Fragment(FragmentKind.Placeholder, null, null, 0, null, statement.Span));
                        }
                        else
                        {
                            EndAt(statement);
                        }

                        break;

                    case TryStatementSyntax tryStatement:
                        WalkStatement(tryStatement.Block, conditional);
                        foreach (var @catch in tryStatement.Catches)
                        {
                            if (References(@catch))
                            {
                                EndAt(@catch);
                            }
                        }

                        if (tryStatement.Finally != null && References(tryStatement.Finally))
                        {
                            EndAt(tryStatement.Finally);
                        }

                        break;

                    case UsingStatementSyntax usingStatement when !References(usingStatement.Declaration) && !References(usingStatement.Expression):
                        WalkStatement(usingStatement.Statement, conditional);
                        break;

                    case LockStatementSyntax lockStatement:
                        WalkStatement(lockStatement.Statement, conditional);
                        break;

                    case CheckedStatementSyntax checkedStatement:
                        WalkStatement(checkedStatement.Block, conditional);
                        break;

                    case LabeledStatementSyntax labeled:
                        WalkStatement(labeled.Statement, conditional);
                        break;

                    case LocalFunctionStatementSyntax _:
                        if (References(statement))
                        {
                            EndMutated(statement);
                        }

                        break;

                    default:
                        // return, throw, using declarations, ...: any use of the variable consumes it.
                        if (References(statement))
                        {
                            EndAt(statement);
                        }

                        break;
                }
            }

            private void WalkExpressionStatement(ExpressionStatementSyntax statement, bool conditional)
            {
                var expression = statement.Expression;
                if (expression is AssignmentExpressionSyntax { Left: IdentifierNameSyntax left } assignment && left.Identifier.ValueText == _name)
                {
                    var right = SqlStringExtractor.SkipParentheses(assignment.Right);
                    if (assignment.IsKind(SyntaxKind.AddAssignmentExpression) && _current != null && !_current.IsBuilder && !References(assignment.Right))
                    {
                        Add(new Fragment(FragmentKind.Expression, right, null, 0, null, right.Span));
                    }
                    else if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) && IsSelfAppend(right, out var clean))
                    {
                        if (_current != null && !_current.IsBuilder && clean)
                        {
                            Add(new Fragment(FragmentKind.SelfAppend, right, null, 0, null, right.Span));
                        }
                        else
                        {
                            EndMutated(statement);
                        }
                    }
                    else if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) && !conditional && !References(assignment.Right))
                    {
                        Start(assignment.Right, null);
                    }
                    else
                    {
                        EndMutated(statement);
                    }

                    return;
                }

                List<InvocationExpressionSyntax>? calls = null;
                if (expression is InvocationExpressionSyntax &&
                    TryCollectBuilderCalls(expression, calls = new List<InvocationExpressionSyntax>(2), out var receiver) && receiver == _name)
                {
                    if (_current == null || !_current.IsBuilder)
                    {
                        EndAt(statement);
                        return;
                    }

                    foreach (var call in calls)
                    {
                        if (!AddBuilderCall(call))
                        {
                            EndMutated(statement);
                            return;
                        }
                    }

                    return;
                }

                if (References(statement))
                {
                    EndAt(statement);
                }
            }

            /// <summary><c>x + a + b</c>: the first leaf is the variable itself and no other leaf uses it.</summary>
            private bool IsSelfAppend(ExpressionSyntax right, out bool clean)
            {
                clean = false;
                if (!(right is BinaryExpressionSyntax binary) || !binary.IsKind(SyntaxKind.AddExpression))
                {
                    return false;
                }

                var leaves = new List<ExpressionSyntax>(4);
                SqlStringExtractor.CollectLeaves(binary, leaves);
                if (!(leaves[0] is IdentifierNameSyntax first) || first.Identifier.ValueText != _name)
                {
                    return false;
                }

                clean = true;
                for (var i = 1; i < leaves.Count; i++)
                {
                    clean &= !References(leaves[i]);
                }

                return true;
            }

            private bool AddBuilderCall(InvocationExpressionSyntax call)
            {
                var method = ((MemberAccessExpressionSyntax)call.Expression).Name.Identifier.ValueText;
                var arguments = call.ArgumentList.Arguments;
                foreach (var argument in arguments)
                {
                    if (References(argument))
                    {
                        return false;
                    }
                }

                switch (method)
                {
                    case "Append" when arguments.Count == 1:
                        var value = SqlStringExtractor.SkipParentheses(arguments[0].Expression);
                        Add(new Fragment(FragmentKind.Expression, value, null, 0, null, value.Span));
                        return true;

                    case "Append":
                        // Append(string, start, count), Append(char, repeat), ...: unknown text.
                        Add(new Fragment(FragmentKind.Placeholder, null, null, 0, null, call.Span));
                        return true;

                    case "AppendLine":
                        if (arguments.Count == 1)
                        {
                            var line = SqlStringExtractor.SkipParentheses(arguments[0].Expression);
                            Add(new Fragment(FragmentKind.Expression, line, null, 0, null, line.Span));
                        }

                        Add(new Fragment(FragmentKind.Text, null, null, 0, "\n", call.ArgumentList.Span));
                        return true;

                    case "AppendFormat" when arguments.Count >= 1:
                        var formatIndex = arguments.Count >= 2 && !SqlStringFlow.IsStringShaped(arguments[0].Expression) &&
                                          SqlStringFlow.IsStringShaped(arguments[1].Expression) ? 1 : 0;
                        var format = SqlStringExtractor.SkipParentheses(arguments[formatIndex].Expression);
                        Add(new Fragment(FragmentKind.Format, format, call, formatIndex, null, format.Span));
                        return true;

                    default:
                        return false;
                }
            }

            private void Start(ExpressionSyntax? initializer, VariableDeclaratorSyntax? declarator)
            {
                End();
                if (initializer == null)
                {
                    return;
                }

                var value = SqlStringExtractor.SkipParentheses(initializer);
                if (value is BaseObjectCreationExpressionSyntax creation &&
                    (creation is ObjectCreationExpressionSyntax explicitCreation
                        ? TypeName(explicitCreation.Type) == "StringBuilder"
                        : declarator?.Parent is VariableDeclarationSyntax declaration && TypeName(declaration.Type) == "StringBuilder"))
                {
                    _current = new Chain(_name, isBuilder: true);
                    var arguments = creation.ArgumentList?.Arguments ?? default;
                    if (arguments.Count > 0)
                    {
                        var first = SqlStringExtractor.SkipParentheses(arguments[0].Expression);
                        if (IsStringShaped(first) || (first is IdentifierNameSyntax id && TryFindInlinableLocal(id, out _)))
                        {
                            Add(new Fragment(FragmentKind.Expression, first, null, 0, null, first.Span));
                        }
                        else if (!IsCapacity(first))
                        {
                            Add(new Fragment(FragmentKind.Placeholder, null, null, 0, null, first.Span));
                        }
                    }

                    return;
                }

                _current = new Chain(_name, isBuilder: false);
                Add(new Fragment(FragmentKind.Expression, value, null, 0, null, value.Span));
            }

            private bool IsCapacity(ExpressionSyntax argument)
            {
                if (argument.IsKind(SyntaxKind.NumericLiteralExpression))
                {
                    return true;
                }

                if (_semanticModel == null)
                {
                    return false;
                }

                var type = _semanticModel.GetTypeInfo(argument, _cancellationToken).Type;
                return type != null && type.SpecialType == SpecialType.System_Int32;
            }

            private void Add(Fragment fragment)
            {
                if (_current == null)
                {
                    return;
                }

                if (_current.Fragments.Count >= MaxFragments)
                {
                    Truncate();
                    return;
                }

                _current.Fragments.Add(fragment);
                if (fragment.Anchor == _anchor)
                {
                    Target = _current;
                }
            }

            private void End() => _current = null;

            /// <summary>
            /// Ends the chain after a statement that changes the variable in an unknown way: the text so far
            /// is followed by a placeholder rather than being taken as the whole statement.
            /// </summary>
            private void EndMutated(SyntaxNode statement)
            {
                Add(new Fragment(FragmentKind.Placeholder, null, null, 0, null, statement.Span));
                _current = null;
            }

            /// <summary>Ends the chain at a statement that uses the variable: a read, or an unknown change.</summary>
            private void EndAt(SyntaxNode statement)
            {
                if (_current == null)
                {
                    return;
                }

                foreach (var identifier in References(statement, all: true))
                {
                    if (!IsRead(identifier))
                    {
                        EndMutated(statement);
                        return;
                    }
                }

                End();
            }

            /// <summary>
            /// True for uses that cannot change the text: a string is immutable unless assigned or passed by
            /// ref; a builder can be changed by anything it is passed to.
            /// </summary>
            private bool IsRead(IdentifierNameSyntax identifier)
            {
                var node = SkipParenthesesUp(identifier);
                switch (node.Parent)
                {
                    case AssignmentExpressionSyntax assignment when assignment.Left == node:
                    case PrefixUnaryExpressionSyntax _:
                    case PostfixUnaryExpressionSyntax _:
                        return false;
                    case ArgumentSyntax argument when !argument.RefKindKeyword.IsKind(SyntaxKind.None):
                        return false;
                }

                if (_current == null || !_current.IsBuilder)
                {
                    for (var parent = node.Parent; parent != null; parent = parent.Parent)
                    {
                        if (parent is AnonymousFunctionExpressionSyntax || parent is LocalFunctionStatementSyntax)
                        {
                            return false;
                        }

                        if (parent is StatementSyntax)
                        {
                            break;
                        }
                    }

                    return true;
                }

                return node.Parent is MemberAccessExpressionSyntax access && access.Expression == node &&
                    (access.Name.Identifier.ValueText == "ToString" || access.Name.Identifier.ValueText == "Length");
            }

            private void Truncate()
            {
                if (_current != null)
                {
                    _current.Truncated = true;
                }

                _current = null;
            }

            /// <summary>Every use of the variable in <paramref name="node"/> appends to it or reads it in a condition.</summary>
            private bool OnlyAppends(SyntaxNode node)
            {
                foreach (var identifier in References(node, all: true))
                {
                    var parent = SkipParenthesesUp(identifier).Parent;
                    switch (parent)
                    {
                        case AssignmentExpressionSyntax assignment when assignment.Left == identifier:
                            if (assignment.IsKind(SyntaxKind.AddAssignmentExpression) && _current is { IsBuilder: false } && !References(assignment.Right))
                            {
                                continue;
                            }

                            if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) && _current is { IsBuilder: false } &&
                                IsSelfAppend(SqlStringExtractor.SkipParentheses(assignment.Right), out var clean) && clean)
                            {
                                continue;
                            }

                            return false;

                        case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression) && IsSelfAppendLeaf(identifier):
                            continue;

                        case MemberAccessExpressionSyntax access when access.Expression == identifier:
                            var member = access.Name.Identifier.ValueText;
                            if (member == "Length")
                            {
                                continue;
                            }

                            if (_current is { IsBuilder: true } && IsAppendName(member) && access.Parent is InvocationExpressionSyntax invocation &&
                                TryGetBuilderStatement(invocation, out var receiver, out _) && receiver == _name)
                            {
                                continue;
                            }

                            return false;

                        default:
                            return false;
                    }
                }

                return true;
            }

            /// <summary>The <c>x</c> on the right of <c>x = x + ...</c>.</summary>
            private bool IsSelfAppendLeaf(IdentifierNameSyntax identifier)
            {
                SyntaxNode node = SkipParenthesesUp(identifier);
                while (node.Parent is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.AddExpression) && SkipParenthesesUp(binary.Left) == SkipParenthesesUp(node))
                {
                    node = SkipParenthesesUp(binary);
                }

                return node.Parent is AssignmentExpressionSyntax assignment && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
                    assignment.Left is IdentifierNameSyntax left && left.Identifier.ValueText == _name;
            }

            private bool References(SyntaxNode? node)
            {
                if (node == null)
                {
                    return false;
                }

                var span = node.Span;
                for (var i = LowerBound(_occurrences, span.Start); i < _occurrences.Count && _occurrences[i] < span.End; i++)
                {
                    if (IsReferenceAt(node, _occurrences[i]))
                    {
                        return true;
                    }
                }

                return false;
            }

            private bool IsReferenceAt(SyntaxNode node, int position)
            {
                var token = node.FindToken(position);
                return token.IsKind(SyntaxKind.IdentifierToken) && token.ValueText == _name && IsReference(token);
            }

            private static bool IsReference(SyntaxToken token) =>
                token.Parent is IdentifierNameSyntax identifier &&
                !(identifier.Parent is MemberAccessExpressionSyntax access && access.Name == identifier) &&
                !(identifier.Parent is NameColonSyntax) && !(identifier.Parent is MemberBindingExpressionSyntax);

            /// <summary>Uses of the variable in <paramref name="node"/>, found through the occurrence index.</summary>
            private IEnumerable<IdentifierNameSyntax> References(SyntaxNode node, bool all)
            {
                var span = node.Span;
                for (var i = LowerBound(_occurrences, span.Start); i < _occurrences.Count && _occurrences[i] < span.End; i++)
                {
                    if (IsReferenceAt(node, _occurrences[i]))
                    {
                        yield return (IdentifierNameSyntax)node.FindToken(_occurrences[i]).Parent!;
                        if (!all)
                        {
                            yield break;
                        }
                    }
                }
            }

            private static int LowerBound(List<int> values, int value)
            {
                int lo = 0, hi = values.Count;
                while (lo < hi)
                {
                    var mid = (lo + hi) >> 1;
                    if (values[mid] < value)
                    {
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid;
                    }
                }

                return lo;
            }
        }
    }
}
