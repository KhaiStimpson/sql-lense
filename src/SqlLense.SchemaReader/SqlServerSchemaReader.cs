using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using SqlLense.Schema;

namespace SqlLense.SchemaReader
{
    /// <summary>
    /// Reads tables, views, functions, procedures and synonyms (with columns and parameters) from a
    /// SQL Server database using three catalog queries.
    /// </summary>
    public static class SqlServerSchemaReader
    {
        internal const string DatabaseInfoQuery = """
            SELECT DB_NAME(), SCHEMA_NAME(), @@SERVERNAME,
                   CAST(CASE WHEN 'a' = 'A' THEN 0 ELSE 1 END AS bit)
            """;

        internal const string ObjectsQuery = """
            SELECT s.name, o.name, RTRIM(o.type),
                   c.name, TYPE_NAME(c.user_type_id), c.max_length, c.precision, c.scale, c.is_nullable
            FROM sys.objects AS o
            JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            LEFT JOIN sys.columns AS c ON c.object_id = o.object_id AND o.type IN ('U', 'V', 'IF', 'TF', 'FT')
            WHERE o.type IN ('U', 'V', 'P', 'PC', 'FN', 'FS', 'IF', 'TF', 'FT', 'SN') AND o.is_ms_shipped = 0
            ORDER BY s.name, o.name, c.column_id
            """;

        internal const string ParametersQuery = """
            SELECT s.name, o.name, p.name, TYPE_NAME(p.user_type_id), p.max_length, p.precision, p.scale, p.is_output
            FROM sys.parameters AS p
            JOIN sys.objects AS o ON o.object_id = p.object_id
            JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            WHERE o.type IN ('P', 'PC') AND o.is_ms_shipped = 0 AND p.parameter_id > 0
            ORDER BY s.name, o.name, p.parameter_id
            """;

        /// <summary>Reads the schema. The connection is opened if needed and left open.</summary>
        public static async Task<DatabaseSchema> ReadAsync(DbConnection connection, CancellationToken cancellationToken = default)
        {
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            }

            string database = connection.Database;
            string defaultSchema = "dbo";
            string? server = null;
            var caseSensitive = false;

            using (var command = Create(connection, DatabaseInfoQuery))
            using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    database = reader.IsDBNull(0) ? database : reader.GetString(0);
                    defaultSchema = reader.IsDBNull(1) ? defaultSchema : reader.GetString(1);
                    server = reader.IsDBNull(2) ? null : reader.GetString(2);
                    caseSensitive = !reader.IsDBNull(3) && reader.GetBoolean(3);
                }
            }

            var builders = new List<ObjectBuilder>();
            var byName = new Dictionary<string, ObjectBuilder>(StringComparer.Ordinal);

            using (var command = Create(connection, ObjectsQuery))
            using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var schema = reader.GetString(0);
                    var name = reader.GetString(1);
                    var key = schema + "." + name;
                    if (!byName.TryGetValue(key, out var builder))
                    {
                        builder = new ObjectBuilder(schema, name, MapKind(reader.GetString(2)));
                        byName[key] = builder;
                        builders.Add(builder);
                    }

                    if (!reader.IsDBNull(3))
                    {
                        builder.Columns.Add(new SchemaColumn(
                            reader.GetString(3),
                            FormatType(reader.IsDBNull(4) ? "sql_variant" : reader.GetString(4), GetInt(reader, 5), GetInt(reader, 6), GetInt(reader, 7)),
                            !reader.IsDBNull(8) && reader.GetBoolean(8)));
                    }
                }
            }

            using (var command = Create(connection, ParametersQuery))
            using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (byName.TryGetValue(reader.GetString(0) + "." + reader.GetString(1), out var builder))
                    {
                        builder.Parameters.Add(new SchemaParameter(
                            reader.GetString(2),
                            FormatType(reader.IsDBNull(3) ? "sql_variant" : reader.GetString(3), GetInt(reader, 4), GetInt(reader, 5), GetInt(reader, 6)),
                            !reader.IsDBNull(7) && reader.GetBoolean(7)));
                    }
                }
            }

            var objects = new List<SchemaObject>(builders.Count);
            foreach (var builder in builders)
            {
                objects.Add(new SchemaObject(builder.Schema, builder.Name, builder.Kind, builder.Columns, builder.Parameters));
            }

            return new DatabaseSchema(database, objects, defaultSchema, caseSensitive, DateTime.UtcNow, server);
        }

        /// <summary>Formats a catalog type as it would be written in DDL, e.g. <c>nvarchar(100)</c>.</summary>
        public static string FormatType(string type, int maxLength, int precision, int scale)
        {
            switch (type.ToLowerInvariant())
            {
                case "nvarchar":
                case "nchar":
                    return maxLength == -1 ? type + "(max)" : type + "(" + (maxLength / 2).ToString(CultureInfo.InvariantCulture) + ")";
                case "varchar":
                case "char":
                case "varbinary":
                case "binary":
                    return maxLength == -1 ? type + "(max)" : type + "(" + maxLength.ToString(CultureInfo.InvariantCulture) + ")";
                case "decimal":
                case "numeric":
                    return type + "(" + precision.ToString(CultureInfo.InvariantCulture) + "," + scale.ToString(CultureInfo.InvariantCulture) + ")";
                case "datetime2":
                case "datetimeoffset":
                case "time":
                    return scale == 7 ? type : type + "(" + scale.ToString(CultureInfo.InvariantCulture) + ")";
                default:
                    return type;
            }
        }

        internal static SchemaObjectKind MapKind(string type) => type switch
        {
            "V" => SchemaObjectKind.View,
            "P" or "PC" => SchemaObjectKind.Procedure,
            "FN" or "FS" => SchemaObjectKind.ScalarFunction,
            "IF" or "TF" or "FT" => SchemaObjectKind.TableFunction,
            "SN" => SchemaObjectKind.Synonym,
            _ => SchemaObjectKind.Table,
        };

        private static int GetInt(DbDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

        private static DbCommand Create(DbConnection connection, string sql)
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 120;
            return command;
        }

        private sealed class ObjectBuilder
        {
            public ObjectBuilder(string schema, string name, SchemaObjectKind kind)
            {
                Schema = schema;
                Name = name;
                Kind = kind;
            }

            public string Schema { get; }

            public string Name { get; }

            public SchemaObjectKind Kind { get; }

            public List<SchemaColumn> Columns { get; } = new List<SchemaColumn>();

            public List<SchemaParameter> Parameters { get; } = new List<SchemaParameter>();
        }
    }
}
