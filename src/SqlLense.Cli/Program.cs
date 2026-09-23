using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SqlLense.Schema;
using SqlLense.SchemaReader;

namespace SqlLense.Cli;

internal static class Program
{
    private const string Usage = """
        sqllense - SQL schema snapshots for the SqlLense analyzer

        Usage:
          sqllense refresh --connection <connection string> [--solution <dir>] [--database <name>] [--out <file>]
          sqllense where [--solution <dir>] [--database <name>]

        Options:
          --connection  SQL Server connection string (any Microsoft.Data.SqlClient auth mode, incl. Entra ID).
                        Defaults to the SQLLENSE_CONNECTION environment variable.
          --solution    Solution folder. Defaults to the nearest folder above the current one with a .sln/.slnx.
          --database    Snapshot name for multi-database solutions (MSBuild: SqlLenseDatabase). Default: "default".
          --out         Write the snapshot to this file instead of the per-user location.
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        Dictionary<string, string> options;
        try
        {
            options = ParseOptions(args.Skip(1).ToArray());
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        var solutionDir = options.GetValueOrDefault("solution") ?? SchemaLocator.FindSolutionDirectory(Environment.CurrentDirectory);
        var database = options.GetValueOrDefault("database");
        var path = SchemaLocator.GetSnapshotPath(options.GetValueOrDefault("out"), solutionDir, database);

        switch (args[0])
        {
            case "where":
                if (path == null)
                {
                    Console.Error.WriteLine("No solution found; pass --solution.");
                    return 1;
                }

                Console.WriteLine(path);
                return 0;

            case "refresh":
                var connectionString = options.GetValueOrDefault("connection") ?? Environment.GetEnvironmentVariable("SQLLENSE_CONNECTION");
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    Console.Error.WriteLine("Missing --connection (or SQLLENSE_CONNECTION).");
                    return 1;
                }

                if (path == null)
                {
                    Console.Error.WriteLine("No solution found; pass --solution or --out.");
                    return 1;
                }

                try
                {
                    await using var connection = new SqlConnection(connectionString);
                    var schema = await SqlServerSchemaReader.ReadAsync(connection);
                    SchemaSnapshotSerializer.Save(schema, path);
                    Console.WriteLine($"Wrote {schema.Objects.Count} objects from '{schema.DatabaseName}' to {path}");
                    return 0;
                }
                catch (Exception ex) when (ex is SqlException or InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
                {
                    Console.Error.WriteLine("Schema refresh failed: " + ex.Message);
                    return 2;
                }

            default:
                Console.Error.WriteLine($"Unknown command '{args[0]}'.");
                Console.WriteLine(Usage);
                return 1;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            {
                throw new ArgumentException($"Unexpected argument '{args[i]}'.");
            }

            result[args[i].Substring(2)] = args[++i];
        }

        return result;
    }
}
