using Xunit;
using static SqlLense.Analyzers.Tests.AnalyzerHarness;

namespace SqlLense.Analyzers.Tests;

public class AnalyzerTests
{
    private const string Prelude = """
        using System.Data;
        static class Dapperish
        {
            public static int Execute(this IDbConnection c, string sql, object? param = null) => 0;
            public static int Query(this IDbConnection c, string sql, object? param = null) => 0;
            public static T ExecuteScalar<T>(this IDbConnection c, string sql, object? param = null) => default!;
        }
        """;

    private static string Wrap(string body) => Prelude + "\nclass C\n{\n" + body + "\n}\n";

    [Fact]
    public async Task ValidSql_NoDiagnostics() => await VerifyAsync(Wrap("""
        const string A = "SELECT Id, Name FROM Customers WHERE Id = @id";
        string B = @"
            SELECT c.Name, o.Total
            FROM Customers c
            JOIN Orders o ON o.CustomerId = c.Id";
        string R = \"\"\"
            UPDATE Customers
            SET Name = @name
            WHERE Id = @id
            \"\"\";
        """.Replace("\\\"", "\"")));

    [Fact]
    public async Task SyntaxError_InRegularString() => await VerifyAsync(Wrap("""
        string s = "SELECT * [|FORM|] Customers";
        """), Descriptors.SyntaxErrorId);

    [Fact]
    public async Task UnknownTable_InVerbatimString() => await VerifyAsync(Wrap(""""
        string s = @"SELECT * FROM [|Custmers|] WHERE Name = 'a""b'";
        """"), Descriptors.InvalidObjectId);

    [Fact]
    public async Task UnknownColumn_AfterEscapes_IsMappedExactly() => await VerifyAsync(Wrap("""
        string s = "SELECT Name,\n\t'say \"hi\"\u0021' AS q, [|Emial|] FROM Customers";
        """), Descriptors.InvalidColumnId);

    [Fact]
    public async Task UnknownColumn_InMultiLineRawString_IsMappedExactly() => await VerifyAsync(Wrap(
        "    string s = \"\"\"\n" +
        "        SELECT Id,\n" +
        "            [|Nmae|]\n" +
        "        FROM Customers\n" +
        "        \"\"\";\n"), Descriptors.InvalidColumnId);

    [Fact]
    public async Task Interpolation_HolesAreParameters() => await VerifyAsync(Wrap("""
        string M(int id, string name) => $"SELECT Id FROM Customers WHERE Id = {id} AND Name = '{name}' AND [|Bogus|] = 1";
        """), Descriptors.InvalidColumnId);

    [Fact]
    public async Task Interpolation_TableNameHole_IsNotValidated() => await VerifyAsync(Wrap("""
        string M(string table) => $"SELECT Whatever FROM {table} WHERE X = 1";
        """));

    [Fact]
    public async Task Concatenation_IsValidatedAsOneStatement() => await VerifyAsync(Wrap("""
        string s = "SELECT Id " +
                   "FROM Customers " +
                   "WHERE [|Nam|] = @n";
        """), Descriptors.InvalidColumnId);

    [Fact]
    public async Task Concatenation_WithConstants() => await VerifyAsync(Wrap("""
        const string Columns = "Id, Name";
        const string Select = "SELECT " + Columns + " FROM Customers";
        string s = Select + " WHERE [|Emal|] = @e";
        """), Descriptors.InvalidColumnId);

    [Fact]
    public async Task Concatenation_WithNonConstantOperand() => await VerifyAsync(Wrap("""
        string M(string orderBy) => "SELECT Id FROM Customers ORDER BY " + orderBy;
        """));

    [Fact]
    public async Task ProseIsIgnored() => await VerifyAsync(Wrap("""
        string a = "Select an item";
        string b = "Update available";
        string c = "delete failed for user";
        string d = "Select a file, then click OK";
        """));

    [Fact]
    public async Task SentenceCase_IsAnalyzed_WhenPassedToSqlApi() => await VerifyAsync(Wrap("""
        void M(IDbConnection c) => c.Query("Select * From [|Custmers|]");
        """), Descriptors.InvalidObjectId);

    [Fact]
    public async Task SentenceCase_IsAnalyzed_WhenAssignedToCommandText() => await VerifyAsync(Wrap("""
        void M(IDbCommand cmd) => cmd.CommandText = "Select * [|Form|] Customers";
        """), Descriptors.SyntaxErrorId);

    [Fact]
    public async Task LangSqlComment_ForcesAnalysis() => await VerifyAsync(Wrap("""
        string s = /*lang=sql*/ "Select * From [|Nope|]";
        """), Descriptors.InvalidObjectId);

    [Fact]
    public async Task IgnoreComment_SuppressesAnalysis() => await VerifyAsync(Wrap("""
        // sqllense:ignore
        string s = "SELECT * FORM Customers";
        """));

    [Fact]
    public async Task WithoutSchema_OnlySyntaxErrors() => await VerifyAsync(Wrap("""
        string a = "SELECT * FROM Nowhere";
        string b = "SELECT * [|FORM|] Nowhere";
        """), withSchema: false, Descriptors.SyntaxErrorId);

    [Fact]
    public async Task Suggestions_AreAttachedAsProperties()
    {
        var diagnostics = await GetDiagnosticsAsync(Wrap("""string s = "SELECT * FROM Custmers";"""));
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("Customers", diagnostic.Properties[Descriptors.SuggestionsProperty]);
        Assert.Contains("Did you mean 'Customers'?", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ReadmeExamples() => await VerifyAsync(Wrap("""
        const string Sql = \"\"\"
            SELECT c.Name, o.[|Totl|]
            FROM Customers c
            JOIN Orders o ON o.CustomerId = c.Id
            \"\"\";

        int M(IDbConnection conn, string region) =>
            conn.ExecuteScalar<int>($"SELECT COUNT(*) FROM [|Custmers|] WHERE Region = {region}");
        """.Replace("\\\"", "\"")), Descriptors.InvalidColumnId, Descriptors.InvalidObjectId);

    [Fact]
    public async Task Utf8Strings_AreIgnored() => await VerifyAsync(Wrap("""
        System.ReadOnlySpan<byte> M() => "SELECT * FORM X"u8;
        """));

    private static readonly string InvalidSql = Wrap("""string s = "SELECT * FORM Custmers";""");

    [Fact]
    public async Task TestProject_ByMsBuildProperty_IsSkipped()
    {
        var diagnostics = await GetDiagnosticsAsync(InvalidSql, buildProperties: new Dictionary<string, string>
        {
            [TestProjectDetector.IsTestProjectProperty] = "true",
        });
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("xunit.core")]
    [InlineData("nunit.framework")]
    [InlineData("Microsoft.VisualStudio.TestPlatform.TestFramework")]
    public async Task TestProject_ByFrameworkReference_IsSkipped(string assemblyName)
    {
        var diagnostics = await GetDiagnosticsAsync(InvalidSql, extraReferences: new[] { FakeAssembly(assemblyName) });
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task TestProject_WithAnalyzeTests_IsAnalyzed()
    {
        var diagnostics = await GetDiagnosticsAsync(
            InvalidSql,
            buildProperties: new Dictionary<string, string>
            {
                [TestProjectDetector.IsTestProjectProperty] = "true",
                [TestProjectDetector.AnalyzeTestsProperty] = "true",
            },
            extraReferences: new[] { FakeAssembly("xunit.core") });
        Assert.Equal(Descriptors.SyntaxErrorId, Assert.Single(diagnostics).Id);
    }

    [Fact]
    public async Task IsTestProjectFalse_IsAnalyzed()
    {
        var diagnostics = await GetDiagnosticsAsync(InvalidSql, buildProperties: new Dictionary<string, string>
        {
            [TestProjectDetector.IsTestProjectProperty] = "false",
        });
        Assert.Equal(Descriptors.SyntaxErrorId, Assert.Single(diagnostics).Id);
    }

    private static Microsoft.CodeAnalysis.MetadataReference FakeAssembly(string name) =>
        Microsoft.CodeAnalysis.CSharp.CSharpCompilation
            .Create(name, references: References, options: new(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary))
            .ToMetadataReference();
}
