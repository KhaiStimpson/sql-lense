using SqlLense.Schema;
using SqlLense.Sql;
using Xunit;

namespace SqlLense.Core.Tests;

public class CompletionAndClassificationTests
{
    private static SqlCompletionResult Complete(string markup)
    {
        var offset = markup.IndexOf('|');
        var sql = markup.Remove(offset, 1);
        return SqlCompletionEngine.GetCompletions(sql, offset, TestSchema.Shop)!;
    }

    private static string[] Labels(SqlCompletionResult result) => result.Items.Select(i => i.DisplayText).ToArray();

    [Fact]
    public void AfterFrom_OffersTables()
    {
        var labels = Labels(Complete("SELECT * FROM Cu|"));
        Assert.Contains("Customers", labels);
        Assert.Contains("ActiveCustomers", labels);
        Assert.Contains("sales.Regions", labels);
        Assert.DoesNotContain("GetCustomer", labels);
    }

    [Fact]
    public void AfterJoin_OffersTables() =>
        Assert.Contains("Orders", Labels(Complete("SELECT * FROM Customers c JOIN |")));

    [Fact]
    public void AfterCommaInFromList_OffersTables() =>
        Assert.Contains("Orders", Labels(Complete("SELECT * FROM Customers c, |")));

    [Fact]
    public void AfterAliasDot_OffersThatTablesColumns()
    {
        var result = Complete("SELECT o.| FROM Customers c JOIN Orders o ON 1 = 1");
        var labels = Labels(result);
        Assert.Contains("Total", labels);
        Assert.DoesNotContain("Email", labels);
    }

    [Fact]
    public void AfterSchemaDot_OffersObjectsInSchema() =>
        Assert.Equal(new[] { "Regions" }, Labels(Complete("SELECT * FROM sales.|")));

    [Fact]
    public void InSelectList_OffersColumnsOfReferencedTables()
    {
        var result = Complete("SELECT Na| FROM Customers");
        Assert.Contains("Name", Labels(result));
        Assert.Equal("SELECT ".Length, result.ReplaceStart);
        Assert.Equal(2, result.ReplaceLength);
    }

    [Fact]
    public void AfterExec_OffersProcedures() =>
        Assert.Contains("GetCustomer", Labels(Complete("EXEC |")));

    [Fact]
    public void InsideSqlStringLiteral_OffersNothing() =>
        Assert.Null(SqlCompletionEngine.GetCompletions("SELECT * FROM Customers WHERE Name = 'ab'", 40, TestSchema.Shop));

    [Fact]
    public void FindSymbol_FallsBackToTokensForInvalidSql()
    {
        const string sql = "SELECT c.Email FROM Customers c WHERE";
        var symbol = SqlCompletionEngine.FindSymbol(sql, sql.IndexOf("Email", StringComparison.Ordinal), TestSchema.Shop);
        Assert.Equal("Email", symbol!.Column!.Name);
    }

    [Fact]
    public void Classifier_ClassifiesTokens()
    {
        const string sql = "SELECT COUNT(*), Name FROM Customers WHERE Id = @id AND Name = N'x' -- note";
        var spans = SqlClassifier.Classify(sql);
        string ClassOf(string text) => spans.Single(s => sql.Substring(s.Start, s.Length) == text).Class.ToString();

        Assert.Equal("Keyword", ClassOf("SELECT"));
        Assert.Equal("Function", ClassOf("COUNT"));
        Assert.Equal("Identifier", ClassOf("Customers"));
        Assert.Equal("Variable", ClassOf("@id"));
        Assert.Equal("String", ClassOf("N'x'"));
        Assert.Equal("Comment", ClassOf("-- note"));
    }

    [Fact]
    public void Snapshot_RoundTrips()
    {
        var json = SchemaSnapshotSerializer.Serialize(TestSchema.Shop);
        var copy = SchemaSnapshotSerializer.Deserialize(json);
        Assert.Equal(TestSchema.Shop.Objects.Count, copy.Objects.Count);
        var customers = copy.Find(null, "customers")!;
        Assert.Equal("nvarchar(100)", customers.FindColumn("NAME", copy.Comparer)!.DataType);
        Assert.Equal("@id", copy.Find("dbo", "GetCustomer")!.Parameters[0].Name);
        Assert.Equal(json, SchemaSnapshotSerializer.Serialize(copy));
    }

    [Fact]
    public void Locator_IsStablePerSolutionAndDatabase()
    {
        var a = SchemaLocator.GetSnapshotPath(null, "/src/app/App.sln", null, null);
        var b = SchemaLocator.GetSnapshotPath(null, "/SRC/app/App.sln", "/src/app/", "default");
        var c = SchemaLocator.GetSnapshotPath(null, "/src/app/App.sln", null, "Reporting");
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.EndsWith("Reporting.json", c);
        Assert.Null(SchemaLocator.GetSnapshotPath(null, null, null, null));
        Assert.Equal("/x.json", SchemaLocator.GetSnapshotPath("/x.json", "/src/app/App.sln", null, null));
    }
}
