using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SqlLense.Text;

namespace SqlLense.Schema
{
    /// <summary>
    /// Reads and writes the schema snapshot file (<c>*.json</c>) produced by "Refresh schema".
    /// </summary>
    public static class SchemaSnapshotSerializer
    {
        public const int FormatVersion = 1;

        public static string Serialize(DatabaseSchema schema)
        {
            var sb = new StringBuilder(64 * 1024);
            sb.Append("{\n  \"version\": ").Append(FormatVersion);
            sb.Append(",\n  \"database\": ");
            MiniJson.WriteString(sb, schema.DatabaseName);
            sb.Append(",\n  \"server\": ");
            MiniJson.WriteString(sb, schema.Server);
            sb.Append(",\n  \"defaultSchema\": ");
            MiniJson.WriteString(sb, schema.DefaultSchema);
            sb.Append(",\n  \"caseSensitive\": ").Append(schema.CaseSensitive ? "true" : "false");
            sb.Append(",\n  \"generatedUtc\": ");
            MiniJson.WriteString(sb, schema.GeneratedUtc?.ToString("o", CultureInfo.InvariantCulture));
            sb.Append(",\n  \"objects\": [");

            var first = true;
            foreach (var obj in schema.Objects)
            {
                sb.Append(first ? "\n    {" : ",\n    {");
                first = false;
                sb.Append("\"schema\": ");
                MiniJson.WriteString(sb, obj.Schema);
                sb.Append(", \"name\": ");
                MiniJson.WriteString(sb, obj.Name);
                sb.Append(", \"kind\": ");
                MiniJson.WriteString(sb, KindToString(obj.Kind));

                if (obj.Columns.Count > 0)
                {
                    sb.Append(", \"columns\": [");
                    for (var i = 0; i < obj.Columns.Count; i++)
                    {
                        var c = obj.Columns[i];
                        sb.Append(i == 0 ? "\n      [" : ",\n      [");
                        MiniJson.WriteString(sb, c.Name);
                        sb.Append(", ");
                        MiniJson.WriteString(sb, c.DataType);
                        sb.Append(c.IsNullable ? ", true]" : ", false]");
                    }

                    sb.Append("\n    ]");
                }

                if (obj.Parameters.Count > 0)
                {
                    sb.Append(", \"parameters\": [");
                    for (var i = 0; i < obj.Parameters.Count; i++)
                    {
                        var p = obj.Parameters[i];
                        sb.Append(i == 0 ? "\n      [" : ",\n      [");
                        MiniJson.WriteString(sb, p.Name);
                        sb.Append(", ");
                        MiniJson.WriteString(sb, p.DataType);
                        sb.Append(p.IsOutput ? ", true]" : ", false]");
                    }

                    sb.Append("\n    ]");
                }

                sb.Append('}');
            }

            sb.Append("\n  ]\n}\n");
            return sb.ToString();
        }

        public static DatabaseSchema Deserialize(string json)
        {
            if (MiniJson.Parse(json) is not Dictionary<string, object?> root)
            {
                throw new FormatException("Schema snapshot must be a JSON object.");
            }

            var version = root.TryGetValue("version", out var v) && v is double d ? (int)d : 0;
            if (version != FormatVersion)
            {
                throw new FormatException($"Unsupported schema snapshot version {version}; refresh the schema.");
            }

            var objects = new List<SchemaObject>();
            if (root.TryGetValue("objects", out var rawObjects) && rawObjects is List<object?> list)
            {
                foreach (var item in list)
                {
                    if (item is not Dictionary<string, object?> o)
                    {
                        continue;
                    }

                    var schemaName = GetString(o, "schema") ?? "dbo";
                    var name = GetString(o, "name");
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    objects.Add(new SchemaObject(
                        schemaName,
                        name!,
                        ParseKind(GetString(o, "kind")),
                        ReadTuples(o, "columns", (n, t, flag) => new SchemaColumn(n, t, flag)),
                        ReadTuples(o, "parameters", (n, t, flag) => new SchemaParameter(n, t, flag))));
                }
            }

            DateTime? generated = null;
            if (GetString(root, "generatedUtc") is { } g &&
                DateTime.TryParse(g, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                generated = parsed;
            }

            return new DatabaseSchema(
                GetString(root, "database") ?? string.Empty,
                objects,
                GetString(root, "defaultSchema") ?? "dbo",
                root.TryGetValue("caseSensitive", out var cs) && cs is true,
                generated,
                GetString(root, "server"));
        }

        public static DatabaseSchema Load(string path) =>
            Deserialize(File.ReadAllText(path, Encoding.UTF8));

        /// <summary>Writes atomically so a concurrently running analyzer never sees a half-written file.</summary>
        public static void Save(DatabaseSchema schema, string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, Serialize(schema), new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }

        private static IReadOnlyList<T>? ReadTuples<T>(Dictionary<string, object?> o, string key, Func<string, string, bool, T> factory)
        {
            if (!o.TryGetValue(key, out var raw) || raw is not List<object?> items || items.Count == 0)
            {
                return null;
            }

            var result = new List<T>(items.Count);
            foreach (var item in items)
            {
                if (item is List<object?> tuple && tuple.Count >= 2 && tuple[0] is string name)
                {
                    result.Add(factory(name, tuple[1] as string ?? string.Empty, tuple.Count > 2 && tuple[2] is true));
                }
            }

            return result;
        }

        private static string? GetString(Dictionary<string, object?> o, string key) =>
            o.TryGetValue(key, out var value) ? value as string : null;

        private static string KindToString(SchemaObjectKind kind) => kind switch
        {
            SchemaObjectKind.Table => "table",
            SchemaObjectKind.View => "view",
            SchemaObjectKind.Procedure => "procedure",
            SchemaObjectKind.ScalarFunction => "scalarFunction",
            SchemaObjectKind.TableFunction => "tableFunction",
            SchemaObjectKind.Synonym => "synonym",
            _ => "table",
        };

        private static SchemaObjectKind ParseKind(string? kind) => kind switch
        {
            "view" => SchemaObjectKind.View,
            "procedure" => SchemaObjectKind.Procedure,
            "scalarFunction" => SchemaObjectKind.ScalarFunction,
            "tableFunction" => SchemaObjectKind.TableFunction,
            "synonym" => SchemaObjectKind.Synonym,
            _ => SchemaObjectKind.Table,
        };
    }
}
