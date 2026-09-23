using System.Collections.Immutable;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using SqlLense.Analyzers;
using SqlLense.CodeFixes;
using SqlLense.Core.Tests;
using SqlLense.Schema;
using SqlLense.Sql;

BenchmarkSwitcher.FromAssembly(typeof(AnalyzerBenchmarks).Assembly).Run(args);

/// <summary>
/// A 20k-line file with 2,000 methods: one SQL string, several non-SQL strings (UI text, log messages,
/// paths) and some numeric arithmetic each, which is representative of data-access heavy code.
/// </summary>
[MemoryDiagnoser]
public class AnalyzerBenchmarks
{
    private CSharpCompilation _compilation = null!;
    private AnalyzerOptions _options = null!;
    private SyntaxNode _root = null!;

    [GlobalSetup]
    public void Setup()
    {
        var sb = new StringBuilder("namespace Bench;\npublic class Repo\n{\n");
        for (var i = 0; i < 2000; i++)
        {
            sb.Append($$""""
                    public string M{{i}}(int id, string name)
                    {
                        var label = "Select an item";
                        var log = "delete failed for user " + name;
                        var path = "C:\\temp\\file{{i}}.txt";
                        var total = id + {{i}} * 2;
                        return i{{i % 4}}(id) + """
                            SELECT c.Id, c.Name, o.Total
                            FROM Customers c
                            JOIN Orders o ON o.CustomerId = c.Id
                            WHERE c.Id = @id AND o.Total > {{i}}
                            """;
                    }

                """");
        }

        sb.Append("static string i0(int x) => \"\"; static string i1(int x) => \"\"; static string i2(int x) => \"\"; static string i3(int x) => \"\";\n}\n");
        var tree = CSharpSyntaxTree.ParseText(sb.ToString(), path: "/bench/Repo.cs");
        _root = tree.GetRoot();
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) is "System.Runtime.dll" or "System.Private.CoreLib.dll")
            .Select(p => MetadataReference.CreateFromFile(p));
        _compilation = CSharpCompilation.Create("Bench", new[] { tree }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var schemaPath = Path.Combine(Path.GetTempPath(), "sqllense-bench.json");
        SchemaSnapshotSerializer.Save(TestSchema.Shop, schemaPath);
        _options = new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty, new Options(schemaPath));
    }

    /// <summary>Full analyzer pass over the file (parse caches warm after the first iteration).</summary>
    [Benchmark]
    public int AnalyzeFile() =>
        _compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new SqlLenseAnalyzer()), _options)
            .GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult().Length;

    /// <summary>
    /// Baseline: an analyzer that subscribes to the same syntax kinds but does nothing, i.e. the cost
    /// Roslyn itself pays (binding the file, walking the tree). SqlLense's overhead is the difference.
    /// </summary>
    [Benchmark(Baseline = true)]
    public int AnalyzeFile_NoOpBaseline() =>
        _compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new NoOpAnalyzer()), _options)
            .GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult().Length;

    /// <summary>What the editor does after each (debounced) edit for highlighting.</summary>
    [Benchmark]
    public int ComputeHighlightRegions() => SqlEditorServices.ComputeRegions(_root, CancellationToken.None).Candidates.Count;

    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class NoOpAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => Descriptors.All;

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterSyntaxNodeAction(_ => { }, SyntaxKind.StringLiteralExpression, SyntaxKind.InterpolatedStringExpression, SyntaxKind.AddExpression);
        }
    }

    private sealed class Options : AnalyzerConfigOptionsProvider
    {
        private readonly Global _global;

        public Options(string path) => _global = new Global(path);

        public override AnalyzerConfigOptions GlobalOptions => _global;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _global;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _global;

        private sealed class Global : AnalyzerConfigOptions
        {
            private readonly string _path;

            public Global(string path) => _path = path;

            public override bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
            {
                value = key == SchemaResolver.SchemaPathProperty ? _path : null;
                return value != null;
            }
        }
    }
}

/// <summary>Cost of analysing one statement: uncached (parse + validate) vs. cached.</summary>
[MemoryDiagnoser]
public class EngineBenchmarks
{
    private const string Sql = """
        WITH recent AS (SELECT CustomerId, SUM(Total) AS Spend FROM Orders WHERE PlacedAt > @since GROUP BY CustomerId)
        SELECT c.Id, c.Name, r.Spend
        FROM Customers c
        JOIN recent r ON r.CustomerId = c.Id
        WHERE c.Email LIKE @pattern
        ORDER BY r.Spend DESC
        """;

    private int _counter;

    [Benchmark]
    public int AnalyzeUncached() => SqlEngine.Analyze(Sql + " -- " + _counter++, TestSchema.Shop).Diagnostics.Count;

    [Benchmark]
    public int AnalyzeCached() => SqlEngine.Analyze(Sql, TestSchema.Shop).Diagnostics.Count;

    [Benchmark]
    public bool HeuristicRejectsProse() => SqlHeuristics.LooksLikeSql("Select an item from the list", knownSqlContext: false);
}
