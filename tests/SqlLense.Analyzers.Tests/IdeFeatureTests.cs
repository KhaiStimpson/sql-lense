using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using SqlLense.CodeFixes;
using Xunit;

namespace SqlLense.Analyzers.Tests;

public class IdeFeatureTests
{
    private static readonly MefHostServices s_host = MefHostServices.Create(
        MefHostServices.DefaultAssemblies
            .Add(typeof(Microsoft.CodeAnalysis.Completion.CompletionService).Assembly)
            .Add(System.Reflection.Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features"))
            .Add(typeof(SqlCompletionProvider).Assembly)
            .Distinct());

    private static Document CreateDocument(string source)
    {
        var workspace = new AdhocWorkspace(s_host);
        var project = workspace.AddProject(ProjectInfo.Create(
                ProjectId.CreateNewId(), VersionStamp.Create(), "Test", "Test", LanguageNames.CSharp,
                filePath: "/test/Test.csproj",
                metadataReferences: AnalyzerHarness.References,
                parseOptions: new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest)));
        project = project.AddAnalyzerConfigDocument(
            ".globalconfig",
            SourceText.From($"is_global = true\n{SchemaResolver.SchemaPathProperty} = {AnalyzerHarness.SchemaPath}\n"),
            filePath: "/test/.globalconfig").Project;
        return project.AddDocument("Test.cs", SourceText.From(source), filePath: "/test/Test.cs");
    }

    private static async Task<string> ApplyFixAsync(string source, string title)
    {
        var document = CreateDocument(source);
        var diagnostics = await AnalyzerHarness.GetDiagnosticsAsync(source);
        var diagnostic = Assert.Single(diagnostics);

        var actions = new List<CodeAction>();
        var context = new CodeFixContext(document, diagnostic, (a, _) => actions.Add(a), CancellationToken.None);
        await new SqlLenseCodeFixProvider().RegisterCodeFixesAsync(context);

        var action = actions.SingleOrDefault(a => a.Title == title);
        Assert.True(action != null, "Available fixes: " + string.Join(", ", actions.Select(a => a.Title)));
        var operation = (await action!.GetOperationsAsync(CancellationToken.None)).OfType<ApplyChangesOperation>().Single();
        var changed = operation.ChangedSolution.GetDocument(document.Id)!;
        return (await changed.GetTextAsync()).ToString();
    }

    [Fact]
    public async Task DidYouMean_ReplacesTableName() =>
        Assert.Equal(
            """class C { string s = "SELECT * FROM Customers WHERE Id = 1"; }""",
            await ApplyFixAsync("""class C { string s = "SELECT * FROM Custmers WHERE Id = 1"; }""", "Change to 'Customers'"));

    [Fact]
    public async Task DidYouMean_KeepsBrackets() =>
        Assert.Equal(
            """class C { string s = "SELECT [Name] FROM Customers"; }""",
            await ApplyFixAsync("""class C { string s = "SELECT [Nmae] FROM Customers"; }""", "Change to 'Name'"));

    [Fact]
    public async Task DidYouMean_CanQualifyWithSchema() =>
        Assert.Equal(
            """class C { string s = "SELECT * FROM sales.Regions"; }""",
            await ApplyFixAsync("""class C { string s = "SELECT * FROM Regions"; }""", "Change to 'sales.Regions'"));

    [Fact]
    public async Task IgnoreFix_AddsComment() =>
        Assert.Equal(
            """class C { string s = /* sqllense:ignore */ "SELECT * FORM Customers"; }""",
            await ApplyFixAsync("""class C { string s = "SELECT * FORM Customers"; }""", "Ignore SQL in this string"));

    private static async Task<ImmutableArray<CompletionItem>> CompleteAsync(string markup)
    {
        var position = markup.IndexOf('|', StringComparison.Ordinal);
        var source = markup.Remove(position, 1);
        var document = CreateDocument(source);
        var service = CompletionService.GetService(document)!;
        var completions = await service.GetCompletionsAsync(document, position);
        return completions.ItemsList.Where(i => i.Properties.ContainsKey("SqlLense.InsertText")).ToImmutableArray();
    }

    [Fact]
    public async Task Completion_OffersTablesAfterFrom()
    {
        var items = await CompleteAsync("""class C { string s = "SELECT * FROM Cu|"; }""");
        Assert.Contains(items, i => i.DisplayText == "Customers");
        Assert.DoesNotContain(items, i => i.DisplayText == "GetCustomer");
    }

    [Fact]
    public async Task Completion_OffersAliasColumns_InRawString()
    {
        var items = await CompleteAsync("class C { string s = \"\"\"\n    SELECT o.| FROM Orders o\n    \"\"\"; }");
        Assert.Contains(items, i => i.DisplayText == "Total");
    }

    [Fact]
    public async Task Completion_WorksInsideInterpolatedString()
    {
        var items = await CompleteAsync("""class C { string M(int id) => $"SELECT Na| FROM Customers WHERE Id = {id}"; }""");
        Assert.Contains(items, i => i.DisplayText == "Name");
    }

    [Fact]
    public async Task Completion_NothingOutsideSql()
    {
        Assert.Empty(await CompleteAsync("""class C { string s = "Hello wor|ld"; }"""));
        Assert.Empty(await CompleteAsync("""class C { void M() { var x = 1; x| } }"""));
    }
}
