using SqlLense.Sql;
using Xunit;

namespace SqlLense.Core.Tests;

public class SqlEngineTests
{
    private static IReadOnlyList<SqlDiagnostic> Analyze(string sql) => SqlEngine.Analyze(sql, TestSchema.Shop).Diagnostics;

    private static void AssertClean(string sql)
    {
        var diagnostics = Analyze(sql);
        Assert.True(diagnostics.Count == 0, string.Join("\n", diagnostics));
    }

    private static SqlDiagnostic AssertSingle(string sql, SqlDiagnosticKind kind, string markedText)
    {
        var diagnostics = Analyze(sql);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(kind, diagnostic.Kind);
        Assert.Equal(markedText, sql.Substring(diagnostic.Start, diagnostic.Length));
        return diagnostic;
    }

    [Theory]
    [InlineData("SELECT Id, Name FROM Customers WHERE Id = @id")]
    [InlineData("SELECT c.Id, o.Total FROM dbo.Customers c JOIN Orders AS o ON o.CustomerId = c.Id")]
    [InlineData("SELECT * FROM Customers")]
    [InlineData("SELECT c.* FROM Customers c")]
    [InlineData("SELECT Name AS DisplayName FROM Customers ORDER BY DisplayName")]
    [InlineData("SELECT COUNT(*) FROM Orders GROUP BY CustomerId HAVING SUM(Total) > 10")]
    [InlineData("SELECT DATEADD(day, 1, PlacedAt) FROM Orders")]
    [InlineData("SELECT Id FROM Customers c WHERE EXISTS (SELECT 1 FROM Orders o WHERE o.CustomerId = c.Id AND Total > 5)")]
    [InlineData("SELECT x.Id FROM (SELECT Id, Name FROM Customers) x")]
    [InlineData("WITH recent AS (SELECT CustomerId, Total FROM Orders) SELECT CustomerId FROM recent")]
    [InlineData("WITH t (A, B) AS (SELECT 1, 2) SELECT A, B FROM t")]
    [InlineData("INSERT INTO Customers (Name, Email) VALUES (@name, @email)")]
    [InlineData("INSERT INTO Customers (Name) OUTPUT inserted.Id VALUES (@name)")]
    [InlineData("UPDATE Customers SET Name = @name WHERE Id = @id")]
    [InlineData("UPDATE c SET c.Name = o.Total FROM Customers c JOIN Orders o ON o.CustomerId = c.Id")]
    [InlineData("DELETE FROM Orders WHERE PlacedAt < @cutoff")]
    [InlineData("DELETE o FROM Orders o JOIN Customers c ON c.Id = o.CustomerId WHERE c.Name = 'x'")]
    [InlineData("SELECT RegionName FROM sales.Regions")]
    [InlineData("SELECT Id FROM ActiveCustomers")]
    [InlineData("SELECT * FROM OtherDb.dbo.Whatever")]
    [InlineData("SELECT Foo FROM #temp")]
    [InlineData("DECLARE @t TABLE (A int); SELECT A FROM @t")]
    [InlineData("CREATE TABLE #x (A int); SELECT A FROM #x")]
    [InlineData("SELECT value FROM STRING_SPLIT(@list, ',')")]
    [InlineData("SELECT o.OrderId FROM dbo.fnOrdersFor(@id) o")]
    [InlineData("SELECT dbo.fnTax(Total) FROM Orders")]
    [InlineData("EXEC GetCustomer @id = 5")]
    [InlineData("EXEC sp_executesql @sql")]
    [InlineData("MERGE INTO Customers AS t USING (SELECT @id AS Id, @name AS Name) AS s ON t.Id = s.Id WHEN MATCHED THEN UPDATE SET Name = s.Name WHEN NOT MATCHED THEN INSERT (Name) VALUES (s.Name);")]
    [InlineData("SELECT Id FROM Customers WHERE Name = @__sqllense_p0")]
    [InlineData("SELECT * FROM @__sqllense_p0")]
    [InlineData("SELECT __sqllense_p0 FROM Customers")]
    [InlineData("SELECT Id FROM Customers UNION SELECT Id FROM Orders ORDER BY Id")]
    [InlineData("SELECT Id FROM CUSTOMERS WHERE name = 'a'")]
    public void ValidSql_ProducesNoDiagnostics(string sql) => AssertClean(sql);

    [Fact]
    public void UnknownTable_IsReportedOnName_WithSuggestion()
    {
        var d = AssertSingle("SELECT * FROM Custmers", SqlDiagnosticKind.InvalidObject, "Custmers");
        Assert.Equal("Customers", d.Suggestions[0]);
        Assert.Contains("Did you mean 'Customers'?", d.Message);
    }

    [Fact]
    public void TableInOtherSchema_SuggestsQualifiedName()
    {
        var d = AssertSingle("SELECT * FROM Regions", SqlDiagnosticKind.InvalidObject, "Regions");
        Assert.Equal("sales.Regions", d.Suggestions[0]);
    }

    [Fact]
    public void UnknownColumn_Unqualified()
    {
        var d = AssertSingle("SELECT Nmae FROM Customers", SqlDiagnosticKind.InvalidColumn, "Nmae");
        Assert.Equal("Name", d.Suggestions[0]);
    }

    [Fact]
    public void UnknownColumn_Qualified() =>
        AssertSingle("SELECT c.Total FROM Customers c", SqlDiagnosticKind.InvalidColumn, "Total");

    [Fact]
    public void UnknownAlias() =>
        AssertSingle("SELECT x.Id FROM Customers c", SqlDiagnosticKind.UnboundIdentifier, "x");

    [Fact]
    public void TableNameCannotBeUsedOnceAliased() =>
        AssertSingle("SELECT Customers.Id FROM Customers c", SqlDiagnosticKind.UnboundIdentifier, "Customers");

    [Fact]
    public void AmbiguousColumn() =>
        AssertSingle("SELECT Id FROM Customers c JOIN Orders o ON o.CustomerId = c.Id", SqlDiagnosticKind.AmbiguousColumn, "Id");

    [Fact]
    public void UnknownColumn_InWhereOfCorrelatedSubquery() =>
        AssertSingle("SELECT Id FROM Customers c WHERE EXISTS (SELECT 1 FROM Orders o WHERE o.Bogus = c.Id)", SqlDiagnosticKind.InvalidColumn, "Bogus");

    [Fact]
    public void UnknownColumn_InDerivedTable() =>
        AssertSingle("SELECT x.Email FROM (SELECT Id, Name FROM Customers) x", SqlDiagnosticKind.InvalidColumn, "Email");

    [Fact]
    public void UnknownColumn_InInsertList() =>
        AssertSingle("INSERT INTO Customers (Name, Phone) VALUES (@a, @b)", SqlDiagnosticKind.InvalidColumn, "Phone");

    [Fact]
    public void UnknownColumn_InUpdateSet() =>
        AssertSingle("UPDATE Customers SET Nam = @n WHERE Id = 1", SqlDiagnosticKind.InvalidColumn, "Nam");

    [Fact]
    public void UnknownProcedure() =>
        AssertSingle("EXEC GetCustomr @id = 1", SqlDiagnosticKind.UnknownProcedure, "GetCustomr");

    [Fact]
    public void UnknownProcedureParameter() =>
        AssertSingle("EXEC GetCustomer @customerId = 1", SqlDiagnosticKind.UnknownParameter, "@customerId");

    [Fact]
    public void UnknownScalarFunction() =>
        AssertSingle("SELECT dbo.fnTaks(Total) FROM Orders", SqlDiagnosticKind.InvalidObject, "fnTaks");

    [Fact]
    public void SyntaxError_PointsAtOffendingToken()
    {
        var d = AssertSingle("SELECT * FORM Customers", SqlDiagnosticKind.SyntaxError, "FORM");
        Assert.Equal("Incorrect syntax near 'FORM'.", d.Message);
    }

    [Fact]
    public void SyntaxError_AtEndOfInput_PointsAtLastToken() =>
        AssertSingle("SELECT * FROM Customers WHERE", SqlDiagnosticKind.SyntaxError, "WHERE");

    [Fact]
    public void SyntaxError_NextToPlaceholder_IsSuppressed() =>
        Assert.Empty(Analyze("SELECT * FROM Customers ORDER BY @__sqllense_p0 @__sqllense_p1"));

    [Fact]
    public void WithoutSchema_OnlySyntaxIsChecked()
    {
        Assert.Empty(SqlEngine.Analyze("SELECT Bogus FROM Nowhere", null).Diagnostics);
        Assert.Single(SqlEngine.Analyze("SELECT FROM", null).Diagnostics);
    }

    [Fact]
    public void Results_AreCached()
    {
        var first = SqlEngine.Analyze("SELECT Id FROM Customers WHERE Id = 42", TestSchema.Shop);
        var second = SqlEngine.Analyze("SELECT Id FROM Customers WHERE Id = 42", TestSchema.Shop);
        Assert.Same(first, second);
    }

    [Fact]
    public void Symbols_AreRecordedForQuickInfo()
    {
        const string sql = "SELECT c.Email FROM Customers c";
        var result = SqlEngine.Analyze(sql, TestSchema.Shop);
        var column = result.FindSymbolAt(sql.IndexOf("Email", StringComparison.Ordinal) + 1);
        Assert.NotNull(column);
        Assert.Equal(SqlSymbolKind.Column, column!.Kind);
        Assert.Equal("Email", column.Column!.Name);

        var table = result.FindSymbolAt(sql.IndexOf("Customers", StringComparison.Ordinal));
        Assert.Equal(SqlSymbolKind.Table, table!.Kind);
        Assert.Equal("dbo.Customers", table.Object.QualifiedName);
    }
}
