using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlLense.Schema;
using SqlLense.Text;

namespace SqlLense.Sql
{
    /// <summary>
    /// Resolves object and column references in a parsed script against a <see cref="DatabaseSchema"/>.
    /// Resolution is deliberately conservative: whenever a row source's columns cannot be known
    /// (table-valued functions, temp tables created elsewhere, PIVOT, OPENJSON, cross-database names,
    /// interpolation placeholders...) the validator stays silent rather than risk a false positive.
    /// </summary>
    internal sealed class SqlSemanticValidator : TSqlFragmentVisitor
    {
        private static readonly HashSet<string> s_datePartFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DATEADD", "DATEDIFF", "DATEDIFF_BIG", "DATENAME", "DATEPART", "DATETRUNC", "DATE_BUCKET",
        };

        private readonly DatabaseSchema _schema;
        private readonly StringComparer _cmp;
        private readonly List<SqlDiagnostic> _diagnostics = new List<SqlDiagnostic>();
        private readonly List<SqlSymbol> _symbols = new List<SqlSymbol>();
        private readonly HashSet<TSqlFragment> _handled = new HashSet<TSqlFragment>(ReferenceComparer.Instance);
        private readonly Dictionary<TableReference, RowSource> _sources = new Dictionary<TableReference, RowSource>(ReferenceComparer.Instance);
        private readonly DeclarationCollector _declarations;
        private Scope? _scope;
        private int _suppressColumns;

        private SqlSemanticValidator(DatabaseSchema schema, DeclarationCollector declarations)
        {
            _schema = schema;
            _cmp = schema.Comparer;
            _declarations = declarations;
        }

        public static void Validate(TSqlFragment fragment, DatabaseSchema schema, List<SqlDiagnostic> diagnostics, List<SqlSymbol> symbols)
        {
            var declarations = new DeclarationCollector(schema.Comparer);
            fragment.Accept(declarations);

            var validator = new SqlSemanticValidator(schema, declarations);
            fragment.Accept(validator);

            diagnostics.AddRange(validator._diagnostics);
            symbols.AddRange(validator._symbols);
        }

        // ---------------------------------------------------------------- scopes

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
                if (element is SelectScalarExpression { ColumnName.Value: { } alias })
                {
                    scope.AddSelectAlias(alias, _cmp);
                }
            }

            WithScope(scope, () => base.ExplicitVisit(node));
        }

        public override void ExplicitVisit(UpdateSpecification node)
        {
            var scope = new Scope(_scope);
            var target = BuildDmlTarget(scope, node.Target, node.FromClause);
            foreach (var clause in node.SetClauses)
            {
                if (clause is AssignmentSetClause { Column: { } column })
                {
                    ValidateTargetColumn(column, target);
                }
            }

            WithScope(scope, () => base.ExplicitVisit(node));
        }

        public override void ExplicitVisit(DeleteSpecification node)
        {
            var scope = new Scope(_scope);
            BuildDmlTarget(scope, node.Target, node.FromClause);
            WithScope(scope, () => base.ExplicitVisit(node));
        }

        public override void ExplicitVisit(InsertSpecification node)
        {
            var scope = new Scope(_scope);
            var target = BuildSource(node.Target);
            AddPseudoSources(scope, target);
            foreach (var column in node.Columns)
            {
                ValidateTargetColumn(column, target);
            }

            WithScope(scope, () => base.ExplicitVisit(node));
        }

        public override void ExplicitVisit(MergeSpecification node)
        {
            var scope = new Scope(_scope);
            var target = BuildSource(node.Target);
            if (node.TableAlias != null)
            {
                target = target.WithAlias(node.TableAlias.Value);
            }

            scope.Sources.Add(target);
            if (node.TableReference != null)
            {
                AddSources(scope, node.TableReference);
            }

            AddPseudoSources(scope, target);
            foreach (var clause in node.ActionClauses)
            {
                switch (clause.Action)
                {
                    case InsertMergeAction insert:
                        foreach (var column in insert.Columns)
                        {
                            ValidateTargetColumn(column, target);
                        }

                        break;
                    case UpdateMergeAction update:
                        foreach (var set in update.SetClauses)
                        {
                            if (set is AssignmentSetClause { Column: { } column })
                            {
                                ValidateTargetColumn(column, target);
                            }
                        }

                        break;
                }
            }

            WithScope(scope, () => base.ExplicitVisit(node));
        }

        private void WithScope(Scope scope, Action visit)
        {
            var saved = _scope;
            _scope = scope;
            try
            {
                visit();
            }
            finally
            {
                _scope = saved;
            }
        }

        private RowSource BuildDmlTarget(Scope scope, TableReference target, FromClause? from)
        {
            if (from != null)
            {
                foreach (var tableReference in from.TableReferences)
                {
                    AddSources(scope, tableReference);
                }
            }

            RowSource? resolved = null;

            // "UPDATE u SET ... FROM Users u": the target names an alias (or table) from the FROM clause.
            if (target is NamedTableReference { Alias: null } named && named.SchemaObject.Count <= 2)
            {
                var qualifier = named.SchemaObject.Identifiers.Select(i => i.Value).ToList();
                resolved = scope.Sources.FirstOrDefault(s => s.MatchesQualifier(qualifier, _cmp));
                if (resolved != null)
                {
                    _sources[target] = resolved;
                }
            }

            if (resolved == null)
            {
                resolved = BuildSource(target);
                scope.Sources.Add(resolved);
            }

            AddPseudoSources(scope, resolved);
            return resolved;
        }

        private static void AddPseudoSources(Scope scope, RowSource target)
        {
            scope.Sources.Add(target.AsPseudo("inserted"));
            scope.Sources.Add(target.AsPseudo("deleted"));
        }

        private void AddSources(Scope scope, TableReference tableReference)
        {
            switch (tableReference)
            {
                case JoinTableReference join:
                    AddSources(scope, join.FirstTableReference);
                    AddSources(scope, join.SecondTableReference);
                    break;
                case JoinParenthesisTableReference parenthesis:
                    AddSources(scope, parenthesis.Join);
                    break;
                default:
                    scope.Sources.Add(BuildSource(tableReference));
                    break;
            }
        }

        // ---------------------------------------------------------------- row sources

        public override void ExplicitVisit(NamedTableReference node)
        {
            BuildSource(node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SchemaObjectFunctionTableReference node)
        {
            BuildSource(node);
            base.ExplicitVisit(node);
        }

        private RowSource BuildSource(TableReference tableReference)
        {
            if (_sources.TryGetValue(tableReference, out var existing))
            {
                return existing;
            }

            var alias = (tableReference as TableReferenceWithAlias)?.Alias?.Value;
            var source = tableReference switch
            {
                NamedTableReference named => BuildNamedSource(named, alias),
                SchemaObjectFunctionTableReference function => BuildFunctionSource(function, alias),
                VariableTableReference variable => new RowSource(alias, variable.Variable.Name, null,
                    _declarations.TableVariables.TryGetValue(variable.Variable.Name, out var variableColumns) ? variableColumns : null),
                QueryDerivedTable derived => new RowSource(alias, null, null,
                    derived.Columns.Count > 0 ? ToSet(derived.Columns.Select(c => c.Value)) : OutputColumns(derived.QueryExpression)),
                InlineDerivedTable inline => new RowSource(alias, null, null,
                    inline.Columns.Count > 0 ? ToSet(inline.Columns.Select(c => c.Value)) : null),
                _ => RowSource.Unknown(alias),
            };

            _sources[tableReference] = source;
            return source;
        }

        private RowSource BuildNamedSource(NamedTableReference node, string? alias)
        {
            var name = node.SchemaObject;
            var baseName = name.BaseIdentifier.Value;

            if (IsExternal(name) || name.Identifiers.Any(i => SqlEngine.IsPlaceholder(i.Value)))
            {
                return new RowSource(alias, baseName, null, null);
            }

            if (baseName.StartsWith("#", StringComparison.Ordinal))
            {
                return new RowSource(alias, baseName, null,
                    _declarations.TempTables.TryGetValue(baseName, out var tempColumns) ? tempColumns : null);
            }

            if (name.SchemaIdentifier == null && _declarations.Ctes.TryGetValue(baseName, out var cte))
            {
                var columns = cte.Columns.Count > 0 ? ToSet(cte.Columns.Select(c => c.Value)) : OutputColumns(cte.QueryExpression);
                return new RowSource(alias, baseName, null, columns);
            }

            var obj = _schema.Find(name.SchemaIdentifier?.Value, baseName);
            if (obj == null || !obj.IsRowSource)
            {
                ReportInvalidObject(name, o => o.IsRowSource);
                return new RowSource(alias, baseName, null, null);
            }

            AddSymbol(SqlSymbolKind.Table, name.BaseIdentifier, obj);
            return new RowSource(alias, baseName, obj, null);
        }

        private RowSource BuildFunctionSource(SchemaObjectFunctionTableReference node, string? alias)
        {
            var name = node.SchemaObject;
            var baseName = name.BaseIdentifier.Value;
            if (IsExternal(name) || name.Identifiers.Any(i => SqlEngine.IsPlaceholder(i.Value)))
            {
                return new RowSource(alias, baseName, null, null);
            }

            var obj = _schema.Find(name.SchemaIdentifier?.Value, baseName);
            if (obj == null || obj.Kind != SchemaObjectKind.TableFunction)
            {
                ReportInvalidObject(name, o => o.Kind == SchemaObjectKind.TableFunction);
                return new RowSource(alias, baseName, null, null);
            }

            AddSymbol(SqlSymbolKind.Function, name.BaseIdentifier, obj);
            var explicitColumns = node.Columns.Count > 0 ? ToSet(node.Columns.Select(c => c.Value)) : null;
            return new RowSource(alias, baseName, explicitColumns == null ? obj : null, explicitColumns);
        }

        /// <summary>
        /// Names that cannot be checked against this schema: server- or other-database-qualified names
        /// and the system catalog (sys.*, INFORMATION_SCHEMA.*), which snapshots do not capture.
        /// </summary>
        private bool IsExternal(SchemaObjectName name) =>
            name.ServerIdentifier != null ||
            (name.DatabaseIdentifier != null && !_cmp.Equals(name.DatabaseIdentifier.Value, _schema.DatabaseName)) ||
            (name.SchemaIdentifier != null &&
             (string.Equals(name.SchemaIdentifier.Value, "sys", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(name.SchemaIdentifier.Value, "INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase)));

        private HashSet<string>? OutputColumns(QueryExpression? query)
        {
            switch (query)
            {
                case QuerySpecification specification:
                    var result = new HashSet<string>(_cmp);
                    foreach (var element in specification.SelectElements)
                    {
                        switch (element)
                        {
                            case SelectStarExpression:
                                return null;
                            case SelectScalarExpression scalar:
                                var name = scalar.ColumnName?.Value
                                    ?? (scalar.Expression as ColumnReferenceExpression)?.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value;
                                if (name != null)
                                {
                                    result.Add(name);
                                }

                                break;
                        }
                    }

                    return result;
                case BinaryQueryExpression binary:
                    return OutputColumns(binary.FirstQueryExpression);
                case QueryParenthesisExpression parenthesis:
                    return OutputColumns(parenthesis.QueryExpression);
                default:
                    return null;
            }
        }

        private HashSet<string> ToSet(IEnumerable<string> names) => new HashSet<string>(names, _cmp);

        // ---------------------------------------------------------------- columns

        public override void ExplicitVisit(FunctionCall node)
        {
            // DATEADD(day, ...) parses "day" as a column reference; it is a date part keyword.
            if (s_datePartFunctions.Contains(node.FunctionName.Value) && node.Parameters.Count > 0 &&
                node.Parameters[0] is ColumnReferenceExpression datePart)
            {
                _handled.Add(datePart);
            }

            if (node.CallTarget is MultiPartIdentifierCallTarget { MultiPartIdentifier.Count: 1 } target)
            {
                ValidateScalarFunction(target.MultiPartIdentifier.Identifiers[0], node.FunctionName);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ExecuteSpecification node)
        {
            if (node.ExecutableEntity is ExecutableProcedureReference procedure)
            {
                ValidateProcedure(procedure);
            }

            _suppressColumns++;
            try
            {
                base.ExplicitVisit(node);
            }
            finally
            {
                _suppressColumns--;
            }
        }

        public override void ExplicitVisit(ColumnReferenceExpression node)
        {
            ResolveColumn(node);
            base.ExplicitVisit(node);
        }

        private void ValidateTargetColumn(ColumnReferenceExpression column, RowSource target)
        {
            var identifiers = column.MultiPartIdentifier?.Identifiers;
            if (identifiers == null || identifiers.Count != 1)
            {
                // Qualified targets ("SET u.Name = ...") go through normal scope resolution.
                return;
            }

            _handled.Add(column);
            var identifier = identifiers[0];
            if (SqlEngine.IsPlaceholder(identifier.Value))
            {
                return;
            }

            var has = target.HasColumn(identifier.Value, _cmp);
            if (has == false)
            {
                ReportInvalidColumn(identifier, target.ColumnNames);
            }
            else if (has == true && target.Object != null)
            {
                AddSymbol(SqlSymbolKind.Column, identifier, target.Object, target.Object.FindColumn(identifier.Value, _cmp));
            }
        }

        private void ResolveColumn(ColumnReferenceExpression node)
        {
            if (_scope == null || _suppressColumns > 0 || _handled.Contains(node))
            {
                return;
            }

            if (node.ColumnType != ColumnType.Regular && node.ColumnType != ColumnType.Wildcard)
            {
                return;
            }

            var identifiers = node.MultiPartIdentifier?.Identifiers;
            if (identifiers == null || identifiers.Count == 0 || identifiers.Any(i => SqlEngine.IsPlaceholder(i.Value)))
            {
                return;
            }

            var isWildcard = node.ColumnType == ColumnType.Wildcard;
            var qualifierCount = isWildcard ? identifiers.Count : identifiers.Count - 1;
            var column = isWildcard ? null : identifiers[identifiers.Count - 1];

            if (qualifierCount == 0)
            {
                ResolveUnqualified(column!);
                return;
            }

            var qualifier = new List<string>(qualifierCount);
            for (var i = 0; i < qualifierCount; i++)
            {
                qualifier.Add(identifiers[i].Value);
            }

            RowSource? source = null;
            for (var scope = _scope; scope != null && source == null; scope = scope.Parent)
            {
                source = scope.Sources.FirstOrDefault(s => s.MatchesQualifier(qualifier, _cmp));
            }

            if (source == null)
            {
                if (qualifier.Count == 1 &&
                    (_cmp.Equals(qualifier[0], "inserted") || _cmp.Equals(qualifier[0], "deleted")))
                {
                    return; // trigger bodies and OUTPUT clauses outside a DML scope
                }

                var text = string.Join(".", identifiers.Take(qualifierCount).Select(i => i.Value)) + (column == null ? string.Empty : "." + column.Value);
                var first = identifiers[0];
                var last = identifiers[qualifierCount - 1];
                var suggestions = NameSuggester.Suggest(qualifier[qualifier.Count - 1], AllScopes().SelectMany(s => s.Sources).Where(s => !s.IsPseudo).Select(s => s.ExposedName).Where(n => n != null)!);
                _diagnostics.Add(new SqlDiagnostic(
                    SqlDiagnosticKind.UnboundIdentifier,
                    first.StartOffset,
                    last.StartOffset + last.FragmentLength - first.StartOffset,
                    $"The multi-part identifier '{text}' could not be bound.",
                    qualifier[qualifier.Count - 1],
                    suggestions));
                return;
            }

            if (column == null)
            {
                return;
            }

            var has = source.HasColumn(column.Value, _cmp);
            if (has == false)
            {
                ReportInvalidColumn(column, source.ColumnNames);
            }
            else if (has == true && source.Object != null)
            {
                AddSymbol(SqlSymbolKind.Column, column, source.Object, source.Object.FindColumn(column.Value, _cmp));
            }
        }

        private void ResolveUnqualified(Identifier column)
        {
            var name = column.Value;
            for (var scope = _scope; scope != null; scope = scope.Parent)
            {
                RowSource? match = null;
                var matchCount = 0;
                var anyUnknown = false;
                foreach (var source in scope.Sources)
                {
                    if (source.IsPseudo)
                    {
                        continue;
                    }

                    var has = source.HasColumn(name, _cmp);
                    if (has == null)
                    {
                        anyUnknown = true;
                    }
                    else if (has == true)
                    {
                        match ??= source;
                        matchCount++;
                    }
                }

                if (matchCount == 1)
                {
                    if (match!.Object != null)
                    {
                        AddSymbol(SqlSymbolKind.Column, column, match.Object, match.Object.FindColumn(name, _cmp));
                    }

                    return;
                }

                if (matchCount > 1)
                {
                    if (!anyUnknown)
                    {
                        _diagnostics.Add(new SqlDiagnostic(
                            SqlDiagnosticKind.AmbiguousColumn, column.StartOffset, column.FragmentLength,
                            $"Ambiguous column name '{name}'.", name));
                    }

                    return;
                }

                if (anyUnknown || scope.HasSelectAlias(name))
                {
                    return;
                }
            }

            var candidates = AllScopes().SelectMany(s => s.Sources).Where(s => !s.IsPseudo).SelectMany(s => s.ColumnNames);
            ReportInvalidColumn(column, candidates);
        }

        private IEnumerable<Scope> AllScopes()
        {
            for (var scope = _scope; scope != null; scope = scope.Parent)
            {
                yield return scope;
            }
        }

        // ---------------------------------------------------------------- procedures and functions

        private void ValidateProcedure(ExecutableProcedureReference procedure)
        {
            var name = procedure.ProcedureReference?.ProcedureReference?.Name;
            if (name == null || IsExternal(name) || name.Identifiers.Any(i => SqlEngine.IsPlaceholder(i.Value)))
            {
                return;
            }

            var baseName = name.BaseIdentifier.Value;
            var obj = _schema.Find(name.SchemaIdentifier?.Value, baseName);
            if (obj == null || obj.Kind != SchemaObjectKind.Procedure)
            {
                // System procedures (sp_executesql, sp_rename, xp_...) live in master/sys and are not captured.
                if (baseName.StartsWith("sp_", StringComparison.OrdinalIgnoreCase) ||
                    baseName.StartsWith("xp_", StringComparison.OrdinalIgnoreCase) ||
                    (name.SchemaIdentifier != null && _cmp.Equals(name.SchemaIdentifier.Value, "sys")))
                {
                    return;
                }

                var suggestions = NameSuggester.Suggest(baseName, _schema.Objects.Where(o => o.Kind == SchemaObjectKind.Procedure).Select(o => o.Name));
                _diagnostics.Add(new SqlDiagnostic(
                    SqlDiagnosticKind.UnknownProcedure,
                    name.BaseIdentifier.StartOffset,
                    name.BaseIdentifier.FragmentLength,
                    AppendSuggestion($"Could not find stored procedure '{DisplayName(name)}'.", suggestions),
                    baseName,
                    suggestions));
                return;
            }

            AddSymbol(SqlSymbolKind.Procedure, name.BaseIdentifier, obj);
            if (obj.Parameters.Count == 0)
            {
                return;
            }

            foreach (var parameter in procedure.Parameters)
            {
                var variable = parameter.Variable;
                if (variable == null || obj.Parameters.Any(p => _cmp.Equals(p.Name, variable.Name)))
                {
                    continue;
                }

                var suggestions = NameSuggester.Suggest(variable.Name, obj.Parameters.Select(p => p.Name));
                _diagnostics.Add(new SqlDiagnostic(
                    SqlDiagnosticKind.UnknownParameter,
                    variable.StartOffset,
                    variable.FragmentLength,
                    AppendSuggestion($"'{variable.Name}' is not a parameter for procedure '{obj.QualifiedName}'.", suggestions),
                    variable.Name,
                    suggestions));
            }
        }

        private void ValidateScalarFunction(Identifier schemaIdentifier, Identifier functionName)
        {
            // Only "schema.fn(...)" where the schema is known: this cannot be a method call on a column.
            if (!_schema.HasSchema(schemaIdentifier.Value) || SqlEngine.IsPlaceholder(functionName.Value))
            {
                return;
            }

            var obj = _schema.Find(schemaIdentifier.Value, functionName.Value);
            if (obj != null && obj.Kind is SchemaObjectKind.ScalarFunction or SchemaObjectKind.TableFunction or SchemaObjectKind.Synonym)
            {
                AddSymbol(SqlSymbolKind.Function, functionName, obj);
                return;
            }

            var suggestions = NameSuggester.Suggest(
                functionName.Value,
                _schema.Objects.Where(o => o.Kind == SchemaObjectKind.ScalarFunction && _cmp.Equals(o.Schema, schemaIdentifier.Value)).Select(o => o.Name));
            _diagnostics.Add(new SqlDiagnostic(
                SqlDiagnosticKind.InvalidObject,
                functionName.StartOffset,
                functionName.FragmentLength,
                AppendSuggestion($"Cannot find user-defined function '{schemaIdentifier.Value}.{functionName.Value}'.", suggestions),
                functionName.Value,
                suggestions));
        }

        // ---------------------------------------------------------------- reporting

        private void ReportInvalidObject(SchemaObjectName name, Func<SchemaObject, bool> isApplicable)
        {
            var baseName = name.BaseIdentifier.Value;
            var suggestions = new List<string>();

            // Exact name in another schema: suggest the qualified name.
            if (name.SchemaIdentifier == null)
            {
                foreach (var candidate in _schema.FindAnySchema(baseName))
                {
                    if (isApplicable(candidate))
                    {
                        suggestions.Add(candidate.QualifiedName);
                    }
                }
            }

            var schemaFilter = name.SchemaIdentifier?.Value;
            var pool = _schema.Objects.Where(o => isApplicable(o) &&
                (schemaFilter != null
                    ? _cmp.Equals(o.Schema, schemaFilter)
                    : _cmp.Equals(o.Schema, _schema.DefaultSchema) || _cmp.Equals(o.Schema, "dbo")));
            foreach (var suggestion in NameSuggester.Suggest(baseName, pool.Select(o => o.Name)))
            {
                if (!suggestions.Contains(suggestion))
                {
                    suggestions.Add(suggestion);
                }
            }

            _diagnostics.Add(new SqlDiagnostic(
                SqlDiagnosticKind.InvalidObject,
                name.BaseIdentifier.StartOffset,
                name.BaseIdentifier.FragmentLength,
                AppendSuggestion($"Invalid object name '{DisplayName(name)}'.", suggestions),
                baseName,
                suggestions));
        }

        private void ReportInvalidColumn(Identifier column, IEnumerable<string> candidates)
        {
            var suggestions = NameSuggester.Suggest(column.Value, candidates);
            _diagnostics.Add(new SqlDiagnostic(
                SqlDiagnosticKind.InvalidColumn,
                column.StartOffset,
                column.FragmentLength,
                AppendSuggestion($"Invalid column name '{column.Value}'.", suggestions),
                column.Value,
                suggestions));
        }

        private static string AppendSuggestion(string message, IReadOnlyList<string> suggestions) =>
            suggestions.Count == 0 ? message : $"{message} Did you mean '{suggestions[0]}'?";

        private static string DisplayName(SchemaObjectName name) => string.Join(".", name.Identifiers.Select(i => i.Value));

        private void AddSymbol(SqlSymbolKind kind, Identifier identifier, SchemaObject obj, SchemaColumn? column = null)
        {
            if (kind == SqlSymbolKind.Column && column == null)
            {
                return;
            }

            _symbols.Add(new SqlSymbol(kind, identifier.StartOffset, identifier.FragmentLength, obj, column));
        }

        // ---------------------------------------------------------------- helper types

        private sealed class Scope
        {
            private HashSet<string>? _selectAliases;

            public Scope(Scope? parent) => Parent = parent;

            public Scope? Parent { get; }

            public List<RowSource> Sources { get; } = new List<RowSource>();

            public void AddSelectAlias(string alias, StringComparer comparer) =>
                (_selectAliases ??= new HashSet<string>(comparer)).Add(alias);

            public bool HasSelectAlias(string name) => _selectAliases?.Contains(name) == true;
        }

        private sealed class RowSource
        {
            public RowSource(string? alias, string? name, SchemaObject? obj, HashSet<string>? columns, bool isPseudo = false)
            {
                Alias = alias;
                Name = name;
                Object = obj;
                Columns = columns;
                IsPseudo = isPseudo;
            }

            public static RowSource Unknown(string? alias) => new RowSource(alias, null, null, null);

            public string? Alias { get; }

            public string? Name { get; }

            public SchemaObject? Object { get; }

            /// <summary>Explicitly known column names (derived tables, CTEs, temp tables); null when unknown.</summary>
            public HashSet<string>? Columns { get; }

            /// <summary>The <c>inserted</c>/<c>deleted</c> pseudo tables: only reachable by qualified names.</summary>
            public bool IsPseudo { get; }

            public string? ExposedName => Alias ?? Name;

            public IEnumerable<string> ColumnNames =>
                Object != null ? Object.Columns.Select(c => c.Name) : (IEnumerable<string>?)Columns ?? Array.Empty<string>();

            public RowSource WithAlias(string alias) => new RowSource(alias, Name, Object, Columns, IsPseudo);

            public RowSource AsPseudo(string name) => new RowSource(name, null, Object, Columns, isPseudo: true);

            /// <summary>True/false when the answer is known, null when this source's columns are unknown.</summary>
            public bool? HasColumn(string column, StringComparer comparer)
            {
                if (Object != null)
                {
                    return Object.HasKnownColumns ? Object.FindColumn(column, comparer) != null : null;
                }

                return Columns?.Contains(column);
            }

            public bool MatchesQualifier(IReadOnlyList<string> qualifier, StringComparer comparer)
            {
                var last = qualifier[qualifier.Count - 1];
                if (Alias != null)
                {
                    // Once aliased, a table can only be referenced through its alias.
                    return qualifier.Count == 1 && comparer.Equals(Alias, last);
                }

                if (Name == null || !comparer.Equals(Name, last))
                {
                    return false;
                }

                return qualifier.Count == 1 || Object == null || comparer.Equals(Object.Schema, qualifier[qualifier.Count - 2]);
            }
        }

        /// <summary>Pre-pass collecting names declared anywhere in the script (CTEs, temp tables, table variables).</summary>
        private sealed class DeclarationCollector : TSqlFragmentVisitor
        {
            private readonly StringComparer _cmp;

            public DeclarationCollector(StringComparer comparer)
            {
                _cmp = comparer;
                Ctes = new Dictionary<string, CommonTableExpression>(comparer);
                TempTables = new Dictionary<string, HashSet<string>?>(comparer);
                TableVariables = new Dictionary<string, HashSet<string>?>(comparer);
            }

            public Dictionary<string, CommonTableExpression> Ctes { get; }

            public Dictionary<string, HashSet<string>?> TempTables { get; }

            public Dictionary<string, HashSet<string>?> TableVariables { get; }

            public override void Visit(CommonTableExpression node) => Ctes[node.ExpressionName.Value] = node;

            public override void Visit(CreateTableStatement node)
            {
                var name = node.SchemaObjectName.BaseIdentifier.Value;
                if (name.StartsWith("#", StringComparison.Ordinal))
                {
                    TempTables[name] = Columns(node.Definition);
                }
            }

            public override void Visit(DeclareTableVariableBody node) =>
                TableVariables[node.VariableName.Value] = Columns(node.Definition);

            public override void Visit(SelectStatement node)
            {
                // SELECT ... INTO #t: columns depend on the query, treat as unknown.
                var into = node.Into?.BaseIdentifier.Value;
                if (into != null && into.StartsWith("#", StringComparison.Ordinal) && !TempTables.ContainsKey(into))
                {
                    TempTables[into] = null;
                }
            }

            private HashSet<string>? Columns(TableDefinition? definition) =>
                definition == null ? null : new HashSet<string>(definition.ColumnDefinitions.Select(c => c.ColumnIdentifier.Value), _cmp);
        }

        private sealed class ReferenceComparer : IEqualityComparer<TSqlFragment>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            public bool Equals(TSqlFragment? x, TSqlFragment? y) => ReferenceEquals(x, y);

            public int GetHashCode(TSqlFragment obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
