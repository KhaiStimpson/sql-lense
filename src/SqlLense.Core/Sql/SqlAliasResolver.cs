using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlLense.Sql
{
    public enum SqlAliasKind
    {
        /// <summary>A table, view, derived table or function alias: <c>FROM Customers c</c>.</summary>
        Table,

        /// <summary>A column alias: <c>SUM(Total) AS Revenue</c>, or a derived table / CTE column list.</summary>
        Column,

        /// <summary>A common table expression name: <c>WITH Recent AS (...)</c>.</summary>
        Cte,
    }

    public enum SqlAliasRole
    {
        /// <summary>Where the alias is introduced.</summary>
        Declaration,

        /// <summary>A use of the alias: <c>c.Name</c>, <c>ORDER BY Revenue</c>, <c>FROM Recent</c>.</summary>
        Reference,

        /// <summary>The table or view name an alias stands for: <c>Customers</c> in <c>FROM Customers c</c>.</summary>
        AliasedObject,
    }

    /// <summary>Which alias spans to color. Each flag is a user setting.</summary>
    [Flags]
    public enum SqlAliasParts
    {
        None = 0,
        Declarations = 1,
        References = 2,
        AliasedObjects = 4,
        ColumnAndCteAliases = 8,
        All = Declarations | References | AliasedObjects | ColumnAndCteAliases,
    }

    public readonly struct SqlAliasSpan
    {
        public SqlAliasSpan(int start, int length, int colorIndex, SqlAliasKind kind, SqlAliasRole role)
        {
            Start = start;
            Length = length;
            ColorIndex = colorIndex;
            Kind = kind;
            Role = role;
        }

        public int Start { get; }

        public int Length { get; }

        /// <summary>0 for the first alias in the query, 1 for the second... Every alias gets its own index.</summary>
        public int ColorIndex { get; }

        public SqlAliasKind Kind { get; }

        public SqlAliasRole Role { get; }

        public override string ToString() => $"{Kind}.{Role}#{ColorIndex}@{Start}+{Length}";
    }

    /// <summary>
    /// Finds aliases in a query and every place they are used, so each alias can be drawn in its own
    /// color. Valid SQL is resolved from the parse tree with proper scoping; while the user is typing
    /// (ScriptDom returns no tree for invalid SQL) a token scan keeps table aliases and CTEs colored.
    /// </summary>
    public static class SqlAliasResolver
    {
        private static readonly BoundedCache<string, Resolution> s_cache =
            new BoundedCache<string, Resolution>(2048, StringComparer.Ordinal);

        /// <summary>
        /// Alias spans sorted by start, without overlaps. Colors are numbered in order of declaration,
        /// counting only aliases that have at least one span selected by <paramref name="parts"/>.
        /// </summary>
        public static IReadOnlyList<SqlAliasSpan> Classify(string sql, SqlAliasParts parts)
        {
            if ((parts & (SqlAliasParts.Declarations | SqlAliasParts.References | SqlAliasParts.AliasedObjects)) == 0)
            {
                return Array.Empty<SqlAliasSpan>();
            }

            var resolution = s_cache.GetOrAdd(sql, static text => Resolve(text));
            if (resolution.Spans.Count == 0)
            {
                return Array.Empty<SqlAliasSpan>();
            }

            var included = new List<RawSpan>(resolution.Spans.Count);
            var visible = new bool[resolution.Symbols.Count];
            foreach (var span in resolution.Spans)
            {
                if (IsIncluded(span, resolution.Symbols[span.Symbol].Kind, parts))
                {
                    included.Add(span);
                    visible[span.Symbol] = true;
                }
            }

            // Number the aliases that are still visible in declaration order.
            var colorOf = new int[resolution.Symbols.Count];
            var next = 0;
            foreach (var symbol in resolution.Symbols.Select((s, i) => (s.Offset, Id: i)).OrderBy(s => s.Offset).ThenBy(s => s.Id))
            {
                colorOf[symbol.Id] = visible[symbol.Id] ? next++ : -1;
            }

            // Where spans coincide (a CTE referenced with an alias: the name is both a CTE reference and the
            // aliased object) the alias's own declaration or reference wins over the aliased-object tint.
            included.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : Priority(a.Role).CompareTo(Priority(b.Role)));
            var result = new List<SqlAliasSpan>(included.Count);
            var end = -1;
            foreach (var span in included)
            {
                if (span.Start < end)
                {
                    continue;
                }

                var symbol = resolution.Symbols[span.Symbol];
                result.Add(new SqlAliasSpan(span.Start, span.Length, colorOf[span.Symbol], symbol.Kind, span.Role));
                end = span.Start + span.Length;
            }

            return result;
        }

        private static bool IsIncluded(RawSpan span, SqlAliasKind kind, SqlAliasParts parts)
        {
            if (kind != SqlAliasKind.Table && (parts & SqlAliasParts.ColumnAndCteAliases) == 0)
            {
                return false;
            }

            var role = span.Role switch
            {
                SqlAliasRole.Declaration => SqlAliasParts.Declarations,
                SqlAliasRole.Reference => SqlAliasParts.References,
                _ => SqlAliasParts.AliasedObjects,
            };
            return (parts & role) != 0;
        }

        private static int Priority(SqlAliasRole role) => role == SqlAliasRole.AliasedObject ? 1 : 0;

        internal static Resolution Resolve(string sql)
        {
            var parsed = SqlParser.Parse(sql);
            if (!parsed.HasErrors && parsed.Fragment != null)
            {
                var collector = new TreeCollector();
                parsed.Fragment.Accept(collector);
                return collector.Result;
            }

            return TokenCollector.Collect(parsed.Tokens);
        }

        internal readonly struct Symbol
        {
            public Symbol(SqlAliasKind kind, int offset)
            {
                Kind = kind;
                Offset = offset;
            }

            public SqlAliasKind Kind { get; }

            /// <summary>Start of the first declaration; orders the colors.</summary>
            public int Offset { get; }
        }

        internal readonly struct RawSpan
        {
            public RawSpan(int start, int length, int symbol, SqlAliasRole role)
            {
                Start = start;
                Length = length;
                Symbol = symbol;
                Role = role;
            }

            public int Start { get; }

            public int Length { get; }

            public int Symbol { get; }

            public SqlAliasRole Role { get; }
        }

        internal sealed class Resolution
        {
            public List<Symbol> Symbols { get; } = new List<Symbol>();

            public List<RawSpan> Spans { get; } = new List<RawSpan>();

            public int Declare(SqlAliasKind kind, int start, int length)
            {
                Symbols.Add(new Symbol(kind, start));
                var id = Symbols.Count - 1;
                Add(start, length, id, SqlAliasRole.Declaration);
                return id;
            }

            public void Add(int start, int length, int symbol, SqlAliasRole role)
            {
                if (start >= 0 && length > 0)
                {
                    Spans.Add(new RawSpan(start, length, symbol, role));
                }
            }
        }

        // ------------------------------------------------------------------ parse tree

        /// <summary>Resolves aliases with T-SQL scoping: each query block sees its own FROM clause and the enclosing ones.</summary>
        private sealed class TreeCollector : TSqlFragmentVisitor
        {
            private static readonly StringComparer s_cmp = StringComparer.OrdinalIgnoreCase;

            private readonly Dictionary<TSqlFragment, int> _declared = new Dictionary<TSqlFragment, int>();
            private Dictionary<string, Source> _ctes = new Dictionary<string, Source>(s_cmp);
            private Scope? _scope;
            private int _orderBy;

            public Resolution Result { get; } = new Resolution();

            // -------------------------------------------------------------- statements and CTEs

            public override void Visit(StatementWithCtesAndXmlNamespaces node)
            {
                _ctes = new Dictionary<string, Source>(s_cmp);
                var ctes = node.WithCtesAndXmlNamespaces?.CommonTableExpressions;
                if (ctes == null)
                {
                    return;
                }

                foreach (var cte in ctes)
                {
                    var symbol = Declare(SqlAliasKind.Cte, cte.ExpressionName);
                    var columns = cte.Columns.Count > 0 ? DeclareColumns(cte.Columns) : OutputAliases(cte.QueryExpression);
                    _ctes[cte.ExpressionName.Value] = new Source(symbol, columns, isAlias: false);
                }
            }

            // -------------------------------------------------------------- query blocks

            public override void ExplicitVisit(QuerySpecification node)
            {
                var scope = new Scope(_scope);
                if (node.FromClause != null)
                {
                    foreach (var tableReference in node.FromClause.TableReferences)
                    {
                        AddSources(scope, tableReference);
                    }
                }

                foreach (var element in node.SelectElements)
                {
                    if (element is SelectScalarExpression { ColumnName: { } name } && !string.IsNullOrEmpty(name.Value))
                    {
                        scope.SelectAliases[name.Value] = Declare(SqlAliasKind.Column, name);
                    }
                }

                WithScope(scope, () => base.ExplicitVisit(node));
            }

            public override void ExplicitVisit(BinaryQueryExpression node)
            {
                node.FirstQueryExpression?.Accept(this);
                node.SecondQueryExpression?.Accept(this);
                if (node.OrderByClause == null)
                {
                    node.OffsetClause?.Accept(this);
                    node.ForClause?.Accept(this);
                    return;
                }

                // ORDER BY after UNION sees the column aliases of the first query.
                var scope = new Scope(_scope);
                foreach (var alias in OutputAliases(node) ?? new Dictionary<string, int>())
                {
                    scope.SelectAliases[alias.Key] = alias.Value;
                }

                WithScope(scope, () =>
                {
                    node.OrderByClause.Accept(this);
                    node.OffsetClause?.Accept(this);
                    node.ForClause?.Accept(this);
                });
            }

            public override void ExplicitVisit(UpdateSpecification node) =>
                VisitDml(node.Target, node.FromClause, () => base.ExplicitVisit(node));

            public override void ExplicitVisit(DeleteSpecification node) =>
                VisitDml(node.Target, node.FromClause, () => base.ExplicitVisit(node));

            public override void ExplicitVisit(MergeSpecification node)
            {
                var scope = new Scope(_scope);
                if (node.Target is NamedTableReference target && node.TableAlias != null)
                {
                    var symbol = Declare(SqlAliasKind.Table, node.TableAlias);
                    AddAliasedObject(target.SchemaObject, symbol);
                    scope.Sources[node.TableAlias.Value] = new Source(symbol, null, isAlias: true);
                }
                else if (node.Target != null)
                {
                    AddSources(scope, node.Target);
                }

                if (node.TableReference != null)
                {
                    AddSources(scope, node.TableReference);
                }

                WithScope(scope, () => base.ExplicitVisit(node));
            }

            public override void ExplicitVisit(OrderByClause node)
            {
                _orderBy++;
                base.ExplicitVisit(node);
                _orderBy--;
            }

            /// <summary>UPDATE c SET ... FROM Customers c: the target names an alias from the FROM clause.</summary>
            private void VisitDml(TableReference? target, FromClause? from, Action visitChildren)
            {
                var scope = new Scope(_scope);
                if (from != null)
                {
                    foreach (var tableReference in from.TableReferences)
                    {
                        AddSources(scope, tableReference);
                    }
                }

                if (target is NamedTableReference { Alias: null, SchemaObject: { Count: 1 } name } &&
                    scope.Sources.TryGetValue(name.BaseIdentifier.Value, out var aliased) && aliased.IsAlias)
                {
                    Result.Add(name.BaseIdentifier.StartOffset, name.BaseIdentifier.FragmentLength, aliased.Symbol!.Value, SqlAliasRole.Reference);
                }
                else if (target != null)
                {
                    AddSources(scope, target);
                }

                WithScope(scope, visitChildren);
            }

            // -------------------------------------------------------------- references

            public override void ExplicitVisit(ColumnReferenceExpression node)
            {
                var identifiers = node.MultiPartIdentifier?.Identifiers;
                if (identifiers != null && node.ColumnType == ColumnType.Regular)
                {
                    if (identifiers.Count == 2)
                    {
                        var source = FindSource(identifiers[0].Value);
                        if (source?.Symbol is int symbol)
                        {
                            Reference(identifiers[0], symbol);
                        }

                        if (source?.Columns != null && source.Columns.TryGetValue(identifiers[1].Value, out var column))
                        {
                            Reference(identifiers[1], column);
                        }
                    }
                    else if (identifiers.Count == 1 && FindUnqualifiedColumn(identifiers[0].Value) is int column)
                    {
                        Reference(identifiers[0], column);
                    }
                }

                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(SelectStarExpression node)
            {
                var identifiers = node.Qualifier?.Identifiers;
                if (identifiers is { Count: 1 } && FindSource(identifiers[0].Value)?.Symbol is int symbol)
                {
                    Reference(identifiers[0], symbol);
                }

                base.ExplicitVisit(node);
            }

            private Source? FindSource(string name)
            {
                for (var scope = _scope; scope != null; scope = scope.Parent)
                {
                    if (scope.Sources.TryGetValue(name, out var source))
                    {
                        return source;
                    }
                }

                return null;
            }

            /// <summary>
            /// A bare column name is an alias reference when ORDER BY names a select-list alias, or when exactly
            /// one row source in the nearest scope that has it exposes it as a derived table / CTE column alias.
            /// </summary>
            private int? FindUnqualifiedColumn(string name)
            {
                for (var scope = _scope; scope != null; scope = scope.Parent)
                {
                    if (_orderBy > 0 && scope == _scope && scope.SelectAliases.TryGetValue(name, out var selectAlias))
                    {
                        return selectAlias;
                    }

                    int? found = null;
                    var matches = 0;
                    foreach (var source in scope.Sources.Values)
                    {
                        if (source.Columns != null && source.Columns.TryGetValue(name, out var column))
                        {
                            found = column;
                            matches++;
                        }
                    }

                    if (matches > 0)
                    {
                        return matches == 1 ? found : null;
                    }
                }

                return null;
            }

            // -------------------------------------------------------------- row sources

            private void AddSources(Scope scope, TableReference tableReference)
            {
                switch (tableReference)
                {
                    case JoinTableReference join:
                        AddSources(scope, join.FirstTableReference);
                        AddSources(scope, join.SecondTableReference);
                        return;
                    case JoinParenthesisTableReference parenthesis:
                        AddSources(scope, parenthesis.Join);
                        return;
                    case NamedTableReference named:
                        AddNamedSource(scope, named);
                        return;
                    case TableReferenceWithAlias { Alias: { } alias } aliased:
                        var symbol = Declare(SqlAliasKind.Table, alias);
                        switch (aliased)
                        {
                            case SchemaObjectFunctionTableReference function:
                                AddAliasedObject(function.SchemaObject, symbol);
                                break;
                            case VariableTableReference variable:
                                Result.Add(variable.Variable.StartOffset, variable.Variable.FragmentLength, symbol, SqlAliasRole.AliasedObject);
                                break;
                        }

                        var columns = aliased switch
                        {
                            TableReferenceWithAliasAndColumns { Columns.Count: > 0 } withColumns => DeclareColumns(withColumns.Columns),
                            QueryDerivedTable derived => OutputAliases(derived.QueryExpression),
                            _ => null,
                        };
                        scope.Sources[alias.Value] = new Source(symbol, columns, isAlias: true);
                        return;
                }
            }

            private void AddNamedSource(Scope scope, NamedTableReference named)
            {
                var name = named.SchemaObject;
                var baseName = name.BaseIdentifier?.Value;
                if (baseName == null)
                {
                    return;
                }

                Source? cte = null;
                if (name.Count == 1 && _ctes.TryGetValue(baseName, out cte))
                {
                    Reference(name.BaseIdentifier!, cte.Symbol!.Value);
                }

                if (named.Alias == null)
                {
                    // Unaliased: the name itself is how columns get qualified (Recent.Total, Customers.Name).
                    scope.Sources[baseName] = new Source(cte?.Symbol, cte?.Columns, isAlias: false);
                    return;
                }

                var symbol = Declare(SqlAliasKind.Table, named.Alias);
                AddAliasedObject(name, symbol);
                scope.Sources[named.Alias.Value] = new Source(symbol, cte?.Columns, isAlias: true);
            }

            private void AddAliasedObject(SchemaObjectName name, int symbol)
            {
                if (!name.Identifiers.Any(i => SqlEngine.IsPlaceholder(i.Value)))
                {
                    Result.Add(name.StartOffset, name.FragmentLength, symbol, SqlAliasRole.AliasedObject);
                }
            }

            /// <summary>Column aliases of the first query block, which name the columns of a derived table or CTE.</summary>
            private Dictionary<string, int>? OutputAliases(QueryExpression? query)
            {
                while (true)
                {
                    switch (query)
                    {
                        case BinaryQueryExpression binary:
                            query = binary.FirstQueryExpression;
                            continue;
                        case QueryParenthesisExpression parenthesis:
                            query = parenthesis.QueryExpression;
                            continue;
                    }

                    break;
                }

                if (!(query is QuerySpecification specification))
                {
                    return null;
                }

                Dictionary<string, int>? result = null;
                foreach (var element in specification.SelectElements)
                {
                    if (element is SelectScalarExpression { ColumnName: { } name } && !string.IsNullOrEmpty(name.Value))
                    {
                        (result ??= new Dictionary<string, int>(s_cmp))[name.Value] = Declare(SqlAliasKind.Column, name);
                    }
                }

                return result;
            }

            private Dictionary<string, int> DeclareColumns(IList<Identifier> columns)
            {
                var result = new Dictionary<string, int>(s_cmp);
                foreach (var column in columns)
                {
                    result[column.Value] = Declare(SqlAliasKind.Column, column);
                }

                return result;
            }

            /// <summary>Declares once per fragment: derived table columns are declared before their query is visited.</summary>
            private int Declare(SqlAliasKind kind, TSqlFragment fragment)
            {
                if (!_declared.TryGetValue(fragment, out var symbol))
                {
                    symbol = Result.Declare(kind, fragment.StartOffset, fragment.FragmentLength);
                    _declared[fragment] = symbol;
                }

                return symbol;
            }

            private void Reference(TSqlFragment fragment, int symbol) =>
                Result.Add(fragment.StartOffset, fragment.FragmentLength, symbol, SqlAliasRole.Reference);

            private void WithScope(Scope scope, Action action)
            {
                var saved = _scope;
                var savedOrderBy = _orderBy;
                _scope = scope;
                _orderBy = 0;
                try
                {
                    action();
                }
                finally
                {
                    _scope = saved;
                    _orderBy = savedOrderBy;
                }
            }

            private sealed class Scope
            {
                public Scope(Scope? parent) => Parent = parent;

                public Scope? Parent { get; }

                public Dictionary<string, Source> Sources { get; } = new Dictionary<string, Source>(s_cmp);

                public Dictionary<string, int> SelectAliases { get; } = new Dictionary<string, int>(s_cmp);
            }

            private sealed class Source
            {
                public Source(int? symbol, Dictionary<string, int>? columns, bool isAlias)
                {
                    Symbol = symbol;
                    Columns = columns;
                    IsAlias = isAlias;
                }

                /// <summary>The alias (or CTE) this source is exposed as; null for a plain table name.</summary>
                public int? Symbol { get; }

                /// <summary>Column aliases this source exposes (derived tables and CTEs).</summary>
                public Dictionary<string, int>? Columns { get; }

                /// <summary>Exposed under an alias rather than its own name.</summary>
                public bool IsAlias { get; }
            }
        }

        // ------------------------------------------------------------------ token fallback

        /// <summary>
        /// Best-effort resolution for SQL that does not parse yet. Recognises table aliases, CTE names and
        /// select-list <c>AS</c> aliases; aliases with the same name share one color.
        /// </summary>
        private static class TokenCollector
        {
            private static readonly HashSet<string> s_tableContext = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "FROM", "JOIN", "UPDATE", "INTO", "USING", "APPLY", "DELETE",
            };

            private static readonly HashSet<string> s_clauseEnd = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "WHERE", "GROUP", "ORDER", "HAVING", "UNION", "EXCEPT", "INTERSECT", "SELECT", "SET", "VALUES",
                "OPTION", "FOR", "WINDOW", "OUTPUT", "WHEN",
            };

            public static Resolution Collect(IList<TSqlParserToken> tokens)
            {
                var result = new Resolution();
                var t = tokens.Where(x => x.TokenType is not (TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment
                    or TSqlTokenType.MultilineComment or TSqlTokenType.EndOfFile)).ToList();

                var tables = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var ctes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var bareNames = new List<TSqlParserToken>();
                var inSelectList = new HashSet<int>();
                var depth = 0;
                var fromDepth = -1;

                int Declare(Dictionary<string, int> names, SqlAliasKind kind, TSqlParserToken token)
                {
                    var name = Unquote(token.Text);
                    if (names.TryGetValue(name, out var symbol))
                    {
                        result.Add(token.Offset, token.Text.Length, symbol, SqlAliasRole.Declaration);
                    }
                    else
                    {
                        names[name] = symbol = result.Declare(kind, token.Offset, token.Text.Length);
                    }

                    return symbol;
                }

                for (var i = 0; i < t.Count; i++)
                {
                    var token = t[i];
                    var text = token.Text ?? string.Empty;
                    switch (token.TokenType)
                    {
                        case TSqlTokenType.LeftParenthesis:
                            depth++;
                            continue;
                        case TSqlTokenType.RightParenthesis:
                            inSelectList.Remove(depth);
                            depth--;
                            if (fromDepth > depth)
                            {
                                fromDepth = -1;
                            }

                            continue;
                    }

                    if (text.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
                    {
                        inSelectList.Add(depth);
                    }
                    else if (text.Equals("FROM", StringComparison.OrdinalIgnoreCase))
                    {
                        inSelectList.Remove(depth);
                        fromDepth = depth;
                    }
                    else if (fromDepth == depth && s_clauseEnd.Contains(text))
                    {
                        fromDepth = -1;
                    }

                    // WITH name [(columns)] AS ( ... ), name AS ( ... )
                    if (IsName(t, i) && i > 0 && (t[i - 1].TokenType == TSqlTokenType.With || (t[i - 1].TokenType == TSqlTokenType.Comma && fromDepth != depth)))
                    {
                        var j = i + 1;
                        var columnList = j < t.Count && t[j].TokenType == TSqlTokenType.LeftParenthesis ? j : -1;
                        if (columnList >= 0)
                        {
                            j = MatchingParenthesis(t, j) + 1;
                        }

                        if (j + 1 < t.Count && t[j].TokenType == TSqlTokenType.As && t[j + 1].TokenType == TSqlTokenType.LeftParenthesis)
                        {
                            Declare(ctes, SqlAliasKind.Cte, token);
                            for (var k = columnList + 1; columnList >= 0 && k < j - 1; k++)
                            {
                                if (IsName(t, k))
                                {
                                    Declare(columns, SqlAliasKind.Column, t[k]);
                                }
                            }

                            continue;
                        }
                    }

                    // expression AS alias, in a select list.
                    if (token.TokenType == TSqlTokenType.As && inSelectList.Contains(depth) && i + 1 < t.Count &&
                        (IsName(t, i + 1) || t[i + 1].TokenType is TSqlTokenType.AsciiStringLiteral or TSqlTokenType.UnicodeStringLiteral) &&
                        !(i + 2 < t.Count && t[i + 2].TokenType == TSqlTokenType.LeftParenthesis))
                    {
                        Declare(columns, SqlAliasKind.Column, t[i + 1]);
                        i++;
                        continue;
                    }

                    if (!(s_tableContext.Contains(text) || (token.TokenType == TSqlTokenType.Comma && fromDepth == depth)))
                    {
                        continue;
                    }

                    // FROM (subquery) alias: only the alias is read here; the subquery is scanned as usual.
                    var next = i + 1;
                    if (next < t.Count && t[next].TokenType == TSqlTokenType.LeftParenthesis)
                    {
                        var close = MatchingParenthesis(t, next);
                        if (AliasAt(t, close + 1) is int derivedAlias)
                        {
                            Declare(tables, SqlAliasKind.Table, t[derivedAlias]);
                        }

                        continue;
                    }

                    // [schema.]name [(arguments)] [AS] alias
                    var first = next;
                    var last = -1;
                    while (next < t.Count && (IsName(t, next) || t[next].TokenType == TSqlTokenType.Variable))
                    {
                        last = next;
                        if (next + 2 < t.Count && t[next + 1].TokenType == TSqlTokenType.Dot)
                        {
                            next += 2;
                            continue;
                        }

                        next++;
                        break;
                    }

                    if (last < 0)
                    {
                        continue;
                    }

                    var isCall = next < t.Count && t[next].TokenType == TSqlTokenType.LeftParenthesis;
                    if (isCall)
                    {
                        next = MatchingParenthesis(t, next) + 1;
                    }

                    if (first == last && !isCall && ctes.TryGetValue(Unquote(t[first].Text), out var cte))
                    {
                        result.Add(t[first].Offset, t[first].Text.Length, cte, SqlAliasRole.Reference);
                    }

                    if (AliasAt(t, next) is int alias)
                    {
                        var symbol = Declare(tables, SqlAliasKind.Table, t[alias]);
                        var start = t[first].Offset;
                        if (!Enumerable.Range(first, last - first + 1).Any(k => SqlEngine.IsPlaceholder(t[k].Text)))
                        {
                            result.Add(start, t[last].Offset + t[last].Text.Length - start, symbol, SqlAliasRole.AliasedObject);
                        }

                        i = alias;
                    }
                    else if (first == last && !isCall)
                    {
                        bareNames.Add(t[first]);
                    }
                }

                // Qualifiers (c.Name) and alias targets (UPDATE c SET ... FROM Customers c).
                for (var i = 0; i < t.Count; i++)
                {
                    var qualifier = IsName(t, i) && i + 1 < t.Count && t[i + 1].TokenType == TSqlTokenType.Dot &&
                        (i == 0 || t[i - 1].TokenType != TSqlTokenType.Dot);
                    if (qualifier && (tables.TryGetValue(Unquote(t[i].Text), out var symbol) || ctes.TryGetValue(Unquote(t[i].Text), out symbol)))
                    {
                        result.Add(t[i].Offset, t[i].Text.Length, symbol, SqlAliasRole.Reference);
                    }
                }

                foreach (var name in bareNames)
                {
                    if (tables.TryGetValue(Unquote(name.Text), out var symbol))
                    {
                        result.Add(name.Offset, name.Text.Length, symbol, SqlAliasRole.Reference);
                    }
                }

                return result;
            }

            private static int? AliasAt(List<TSqlParserToken> t, int index)
            {
                if (index < t.Count && t[index].TokenType == TSqlTokenType.As)
                {
                    index++;
                }

                return IsName(t, index) && !SqlClassifier.IsSoftKeyword(t[index].Text) &&
                    !(index + 1 < t.Count && t[index + 1].TokenType is TSqlTokenType.Dot or TSqlTokenType.LeftParenthesis)
                    ? index
                    : null;
            }

            private static bool IsName(List<TSqlParserToken> t, int index) =>
                index >= 0 && index < t.Count &&
                t[index].TokenType is TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier &&
                !SqlEngine.IsPlaceholder(t[index].Text);

            private static int MatchingParenthesis(List<TSqlParserToken> t, int open)
            {
                var depth = 0;
                for (var i = open; i < t.Count; i++)
                {
                    if (t[i].TokenType == TSqlTokenType.LeftParenthesis)
                    {
                        depth++;
                    }
                    else if (t[i].TokenType == TSqlTokenType.RightParenthesis && --depth == 0)
                    {
                        return i;
                    }
                }

                return t.Count;
            }

            private static string Unquote(string text) =>
                text.Length >= 2 && (text[0] == '[' || text[0] == '"') ? text.Substring(1, text.Length - 2) : text;
        }
    }
}
