using System;
using System.Collections.Generic;
using System.Threading;

namespace SqlLense.Schema
{
    public enum SchemaObjectKind
    {
        Table,
        View,
        Procedure,
        ScalarFunction,
        TableFunction,
        Synonym,
    }

    public sealed class SchemaColumn
    {
        public SchemaColumn(string name, string dataType, bool isNullable)
        {
            Name = name;
            DataType = dataType;
            IsNullable = isNullable;
        }

        public string Name { get; }

        /// <summary>Display type, e.g. <c>nvarchar(100)</c> or <c>decimal(18,2)</c>.</summary>
        public string DataType { get; }

        public bool IsNullable { get; }

        public override string ToString() => $"{Name} {DataType}{(IsNullable ? " NULL" : " NOT NULL")}";
    }

    public sealed class SchemaParameter
    {
        public SchemaParameter(string name, string dataType, bool isOutput)
        {
            Name = name;
            DataType = dataType;
            IsOutput = isOutput;
        }

        /// <summary>Parameter name including the leading <c>@</c>.</summary>
        public string Name { get; }

        public string DataType { get; }

        public bool IsOutput { get; }
    }

    public sealed class SchemaObject
    {
        private Dictionary<string, SchemaColumn>? _columnLookup;

        public SchemaObject(
            string schema,
            string name,
            SchemaObjectKind kind,
            IReadOnlyList<SchemaColumn>? columns = null,
            IReadOnlyList<SchemaParameter>? parameters = null)
        {
            Schema = schema;
            Name = name;
            Kind = kind;
            Columns = columns ?? Array.Empty<SchemaColumn>();
            Parameters = parameters ?? Array.Empty<SchemaParameter>();
        }

        public string Schema { get; }

        public string Name { get; }

        public SchemaObjectKind Kind { get; }

        public IReadOnlyList<SchemaColumn> Columns { get; }

        public IReadOnlyList<SchemaParameter> Parameters { get; }

        public string QualifiedName => Schema + "." + Name;

        /// <summary>True when the object exposes rows (tables, views, synonyms, TVFs).</summary>
        public bool IsRowSource => Kind is SchemaObjectKind.Table or SchemaObjectKind.View
            or SchemaObjectKind.TableFunction or SchemaObjectKind.Synonym;

        /// <summary>
        /// True when <see cref="Columns"/> is authoritative. Synonyms and objects captured without
        /// column metadata cannot be used to reject column names.
        /// </summary>
        public bool HasKnownColumns => Columns.Count > 0 && Kind != SchemaObjectKind.Synonym;

        public SchemaColumn? FindColumn(string name, StringComparer comparer)
        {
            var lookup = _columnLookup;
            if (lookup == null)
            {
                lookup = new Dictionary<string, SchemaColumn>(comparer);
                foreach (var column in Columns)
                {
                    lookup[column.Name] = column;
                }

                Interlocked.CompareExchange(ref _columnLookup, lookup, null);
                lookup = _columnLookup;
            }

            return lookup!.TryGetValue(name, out var result) ? result : null;
        }

        public override string ToString() => QualifiedName;
    }

    /// <summary>
    /// An immutable, indexed snapshot of a database schema. Instances are shared across threads
    /// and used as cache keys for validation results, so they must never be mutated.
    /// </summary>
    public sealed class DatabaseSchema
    {
        private readonly Dictionary<string, SchemaObject> _byQualifiedName;
        private readonly Dictionary<string, List<SchemaObject>> _byName;
        private readonly HashSet<string> _schemaNames;

        public DatabaseSchema(
            string databaseName,
            IEnumerable<SchemaObject> objects,
            string defaultSchema = "dbo",
            bool caseSensitive = false,
            DateTime? generatedUtc = null,
            string? server = null)
        {
            DatabaseName = databaseName;
            DefaultSchema = string.IsNullOrEmpty(defaultSchema) ? "dbo" : defaultSchema;
            CaseSensitive = caseSensitive;
            GeneratedUtc = generatedUtc;
            Server = server;
            Comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

            _byQualifiedName = new Dictionary<string, SchemaObject>(Comparer);
            _byName = new Dictionary<string, List<SchemaObject>>(Comparer);
            _schemaNames = new HashSet<string>(Comparer);
            var all = new List<SchemaObject>();

            foreach (var obj in objects)
            {
                all.Add(obj);
                _byQualifiedName[obj.QualifiedName] = obj;
                _schemaNames.Add(obj.Schema);
                if (!_byName.TryGetValue(obj.Name, out var list))
                {
                    _byName[obj.Name] = list = new List<SchemaObject>(1);
                }

                list.Add(obj);
            }

            Objects = all;
        }

        public string DatabaseName { get; }

        public string? Server { get; }

        public string DefaultSchema { get; }

        public bool CaseSensitive { get; }

        public DateTime? GeneratedUtc { get; }

        public StringComparer Comparer { get; }

        public IReadOnlyList<SchemaObject> Objects { get; }

        public IEnumerable<string> SchemaNames => _schemaNames;

        public bool HasSchema(string schema) => _schemaNames.Contains(schema);

        /// <summary>
        /// Resolves an object the way SQL Server does: an explicit schema is used as-is; otherwise the
        /// caller's default schema is tried first, then <c>dbo</c>.
        /// </summary>
        public SchemaObject? Find(string? schema, string name)
        {
            if (!string.IsNullOrEmpty(schema))
            {
                return _byQualifiedName.TryGetValue(schema + "." + name, out var qualified) ? qualified : null;
            }

            if (!_byName.TryGetValue(name, out var candidates))
            {
                return null;
            }

            if (candidates.Count == 1)
            {
                var only = candidates[0];
                return Comparer.Equals(only.Schema, DefaultSchema) || Comparer.Equals(only.Schema, "dbo") ? only : null;
            }

            foreach (var candidate in candidates)
            {
                if (Comparer.Equals(candidate.Schema, DefaultSchema))
                {
                    return candidate;
                }
            }

            foreach (var candidate in candidates)
            {
                if (Comparer.Equals(candidate.Schema, "dbo"))
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>All objects with the given name in any schema.</summary>
        public IReadOnlyList<SchemaObject> FindAnySchema(string name) =>
            _byName.TryGetValue(name, out var list) ? list : (IReadOnlyList<SchemaObject>)Array.Empty<SchemaObject>();
    }
}
