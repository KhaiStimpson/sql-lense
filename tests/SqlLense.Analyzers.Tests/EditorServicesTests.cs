using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using SqlLense.CodeFixes;
using SqlLense.Core.Tests;
using SqlLense.Sql;
using Xunit;

namespace SqlLense.Analyzers.Tests;

public class EditorServicesTests
{
    private static SqlDocumentRegions Regions(string source) =>
        SqlEditorServices.ComputeRegions(CSharpSyntaxTree.ParseText(source).GetRoot(), CancellationToken.None);

    [Fact]
    public void Regions_FindOnlySql()
    {
        var regions = Regions("""
            class C
            {
                string a = "SELECT Id FROM Customers";
                string b = "Select an item";
                string c = "UPDATE Customers " + "SET Name = @n";
            }
            """);
        Assert.Equal(2, regions.Candidates.Count);
    }

    [Fact]
    public void Classify_MapsTokensToSource_AndSkipsConcatenationBoundaries()
    {
        const string source = """class C { string s = "SELECT Id " + "FROM Customers WHERE Id = @id"; }""";
        var regions = Regions(source);
        var spans = SqlEditorServices.Classify(regions, new TextSpan(0, source.Length))
            .Select(s => (Text: source.Substring(s.Span.Start, s.Span.Length), s.Class))
            .ToList();

        Assert.Contains(("SELECT", SqlTokenClass.Keyword), spans);
        Assert.Contains(("FROM", SqlTokenClass.Keyword), spans);
        Assert.Contains(("Customers", SqlTokenClass.Identifier), spans);
        Assert.Contains(("@id", SqlTokenClass.Variable), spans);
        Assert.DoesNotContain(spans, s => s.Text.Contains('"'));
    }

    [Fact]
    public void Classify_WorksInVerbatimAndRawStrings()
    {
        const string source = "class C { string a = @\"SELECT \"\"x\"\" FROM T\"; string b = \"\"\"\n    SELECT 1\n    FROM T\n    \"\"\"; }";
        var spans = SqlEditorServices.Classify(Regions(source), new TextSpan(0, source.Length))
            .Select(s => source.Substring(s.Span.Start, s.Span.Length))
            .ToList();
        Assert.Contains("\"\"x\"\"", spans);
        Assert.Equal(2, spans.Count(s => s == "FROM"));
    }

    [Fact]
    public void QuickInfo_DescribesColumn()
    {
        const string source = """class C { string s = "SELECT c.Email FROM Customers c"; }""";
        var position = source.IndexOf("Email", StringComparison.Ordinal) + 2;
        var info = SqlEditorServices.GetQuickInfo(Regions(source), position, TestSchema.Shop);
        Assert.NotNull(info);
        Assert.Equal("Email", source.Substring(info!.Span.Start, info.Span.Length));
        Assert.Equal("(column) dbo.Customers.Email nvarchar(256) NULL", info.Description);
    }

    [Fact]
    public void QuickInfo_DescribesTable()
    {
        const string source = """class C { string s = "SELECT * FROM dbo.Orders"; }""";
        var info = SqlEditorServices.GetQuickInfo(Regions(source), source.IndexOf("Orders", StringComparison.Ordinal), TestSchema.Shop);
        Assert.StartsWith("(table) dbo.Orders\n  Id int NOT NULL\n  CustomerId int NULL", info!.Description);
    }
}
