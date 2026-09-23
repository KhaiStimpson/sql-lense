using SqlLense.Sql;
using Xunit;

namespace SqlLense.Core.Tests;

public class SqlHeuristicsTests
{
    [Theory]
    [InlineData("SELECT * FROM Users")]
    [InlineData("select id from users")]
    [InlineData("  \n  SELECT Id\n  FROM Users")]
    [InlineData("SELECT 1")]
    [InlineData("SELECT @@VERSION")]
    [InlineData("SELECT COUNT(*) FROM X")]
    [InlineData("SELECT TOP 10 Name FROM X")]
    [InlineData("SELECT SCOPE_IDENTITY()")]
    [InlineData("INSERT INTO Users (Name) VALUES (@n)")]
    [InlineData("UPDATE Users SET Name = @n")]
    [InlineData("DELETE FROM Users WHERE Id = 1")]
    [InlineData("MERGE Users AS t USING Src AS s ON t.Id = s.Id WHEN MATCHED THEN DELETE;")]
    [InlineData("WITH cte AS (SELECT 1 AS A) SELECT A FROM cte")]
    [InlineData(";WITH cte (A) AS (SELECT 1) SELECT A FROM cte")]
    [InlineData("EXEC dbo.GetUser @id")]
    [InlineData("EXEC dbo.Cleanup")]
    [InlineData("EXECUTE sp_executesql N'SELECT 1'")]
    [InlineData("CREATE TABLE #t (Id int)")]
    [InlineData("CREATE OR ALTER PROCEDURE dbo.X AS SELECT 1")]
    [InlineData("DROP TABLE IF EXISTS #t")]
    [InlineData("TRUNCATE TABLE Logs")]
    [InlineData("DECLARE @x int = 1; SELECT @x")]
    [InlineData("SET NOCOUNT ON; SELECT 1")]
    [InlineData("IF EXISTS (SELECT 1 FROM X) SELECT 1")]
    [InlineData("BEGIN TRANSACTION")]
    [InlineData("-- comment\nSELECT * FROM X")]
    [InlineData("/* c */ SELECT * FROM X")]
    [InlineData("(SELECT * FROM X)")]
    public void DetectsSql(string text) => Assert.True(SqlHeuristics.LooksLikeSql(text, knownSqlContext: false), text);

    [Theory]
    [InlineData("Select an item")]
    [InlineData("Select a file, then click OK")]
    [InlineData("Select from the list below")]
    [InlineData("select an item")]
    [InlineData("Update available")]
    [InlineData("update failed")]
    [InlineData("Delete this file?")]
    [InlineData("delete failed for user")]
    [InlineData("Insert coin")]
    [InlineData("insert the disk")]
    [InlineData("Create new project")]
    [InlineData("create a new account")]
    [InlineData("execute the plan now")]
    [InlineData("With great power")]
    [InlineData("with regards")]
    [InlineData("Set the value")]
    [InlineData("set the value")]
    [InlineData("If you want")]
    [InlineData("begin the process")]
    [InlineData("drop it")]
    [InlineData("Selected")]
    [InlineData("SELECTED")]
    [InlineData("SELECT")]
    [InlineData("")]
    [InlineData("hello world")]
    [InlineData("C:\\temp\\select")]
    public void RejectsProse(string text) => Assert.False(SqlHeuristics.LooksLikeSql(text, knownSqlContext: false), text);

    [Fact]
    public void KnownSqlContext_OnlyRequiresKeyword()
    {
        Assert.True(SqlHeuristics.LooksLikeSql("Select Name From Users", knownSqlContext: true));
        Assert.False(SqlHeuristics.LooksLikeSql("hello", knownSqlContext: true));
    }
}
