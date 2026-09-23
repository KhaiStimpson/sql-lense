using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using SqlLense.Core.Tests;
using SqlLense.Schema;
using Xunit;

namespace SqlLense.Analyzers.Tests;

/// <summary>Compiles C# snippets and runs the analyzer against the shared test schema.</summary>
internal static class AnalyzerHarness
{
    private static readonly Lazy<string> s_schemaPath = new(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "sqllense-tests-" + Guid.NewGuid().ToString("N") + ".json");
        SchemaSnapshotSerializer.Save(TestSchema.Shop, path);
        return path;
    });

    public static readonly ImmutableArray<MetadataReference> References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) is "System.Runtime.dll" or "System.Private.CoreLib.dll" or "netstandard.dll"
                or "System.Data.Common.dll" or "System.Linq.dll" or "System.Collections.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToImmutableArray();

    public static string SchemaPath => s_schemaPath.Value;

    public static CSharpCompilation Compile(string source, IEnumerable<MetadataReference>? extraReferences = null) =>
        CSharpCompilation.Create(
            "Test",
            new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), path: "/test/Test.cs") },
            References.AddRange(extraReferences ?? Enumerable.Empty<MetadataReference>()),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    public static AnalyzerOptions Options(bool withSchema, IReadOnlyDictionary<string, string>? buildProperties = null)
    {
        var global = new Dictionary<string, string> { [SchemaResolver.SchemaPathProperty] = withSchema ? SchemaPath : "/does/not/exist.json" };
        foreach (var (key, value) in buildProperties ?? new Dictionary<string, string>())
        {
            global[key] = value;
        }

        return new(ImmutableArray<AdditionalText>.Empty, new TestOptionsProvider(global));
    }

    public static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(
        string source,
        bool withSchema = true,
        IReadOnlyDictionary<string, string>? buildProperties = null,
        IEnumerable<MetadataReference>? extraReferences = null)
    {
        var compilation = Compile(source, extraReferences);
        var compileErrors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(compileErrors.Count == 0, "Test source does not compile:\n" + string.Join("\n", compileErrors));

        var exceptions = new List<Exception>();
        var diagnostics = await compilation
            .WithAnalyzers(
                ImmutableArray.Create<DiagnosticAnalyzer>(new SqlLenseAnalyzer()),
                new CompilationWithAnalyzersOptions(Options(withSchema, buildProperties), (ex, _, _) => exceptions.Add(ex), concurrentAnalysis: true, logAnalyzerExecutionTime: false))
            .GetAnalyzerDiagnosticsAsync();
        Assert.True(exceptions.Count == 0, "Analyzer threw:\n" + string.Join("\n", exceptions));
        return diagnostics.OrderBy(d => d.Location.SourceSpan.Start).ToImmutableArray();
    }

    /// <summary>
    /// Verifies that diagnostics are reported exactly on the <c>[|...|]</c> spans of the markup.
    /// Optional ids list the expected diagnostic ids in span order.
    /// </summary>
    public static async Task VerifyAsync([StringSyntax("C#")] string markup, params string[] ids) =>
        await VerifyAsync(markup, withSchema: true, ids);

    public static async Task VerifyAsync(string markup, bool withSchema, params string[] ids)
    {
        var (source, spans) = ParseMarkup(markup);
        var diagnostics = await GetDiagnosticsAsync(source, withSchema);
        var actual = diagnostics.Select(d => $"{d.Id} '{source.Substring(d.Location.SourceSpan.Start, d.Location.SourceSpan.Length)}' {d.GetMessage()}").ToList();
        Assert.True(spans.Count == diagnostics.Length, $"Expected {spans.Count} diagnostics, got:\n{string.Join("\n", actual)}");
        for (var i = 0; i < spans.Count; i++)
        {
            Assert.True(spans[i] == diagnostics[i].Location.SourceSpan,
                $"Diagnostic {i}: expected '{source.Substring(spans[i].Start, spans[i].Length)}' but got {actual[i]}");
            if (ids.Length > i)
            {
                Assert.Equal(ids[i], diagnostics[i].Id);
            }
        }
    }

    public static (string Source, List<TextSpan> Spans) ParseMarkup(string markup)
    {
        var spans = new List<TextSpan>();
        var sb = new System.Text.StringBuilder();
        var starts = new Stack<int>();
        for (var i = 0; i < markup.Length; i++)
        {
            if (markup[i] == '[' && i + 1 < markup.Length && markup[i + 1] == '|')
            {
                starts.Push(sb.Length);
                i++;
            }
            else if (markup[i] == '|' && i + 1 < markup.Length && markup[i + 1] == ']')
            {
                var start = starts.Pop();
                spans.Add(TextSpan.FromBounds(start, sb.Length));
                i++;
            }
            else
            {
                sb.Append(markup[i]);
            }
        }

        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        return (sb.ToString(), spans);
    }

    private sealed class TestOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private readonly DictionaryOptions _global;

        public TestOptionsProvider(Dictionary<string, string> global) => _global = new DictionaryOptions(global);

        public override AnalyzerConfigOptions GlobalOptions => _global;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => DictionaryOptions.Empty;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => DictionaryOptions.Empty;
    }

    private sealed class DictionaryOptions : AnalyzerConfigOptions
    {
        public static readonly DictionaryOptions Empty = new(new Dictionary<string, string>());

        private readonly Dictionary<string, string> _values;

        public DictionaryOptions(Dictionary<string, string> values) => _values = values;

        public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value) => _values.TryGetValue(key, out value);
    }
}
