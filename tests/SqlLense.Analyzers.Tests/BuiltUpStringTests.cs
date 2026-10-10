using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using SqlLense.CodeFixes;
using SqlLense.Sql;
using Xunit;
using static SqlLense.Analyzers.Tests.AnalyzerHarness;

namespace SqlLense.Analyzers.Tests;

/// <summary>Format strings, strings built up over several statements, and inlined locals.</summary>
public class BuiltUpStringTests
{
    private const string Prelude = """
        using System;
        using System.Data;
        using System.Globalization;
        using System.Text;
        using System.Collections.Generic;
        static class Ext
        {
            public static int Execute(this IDbConnection c, string sql, object? param = null) => 0;
            public static int FromSqlRaw(this object set, string sql, params object[] parameters) => 0;
            public static int ExecuteSqlRaw(this object db, string sql, params object[] parameters) => 0;
        }
        """;

    private static string Wrap(string body) => Prelude + "\nclass C\n{\n" + body + "\n}\n";

    // ------------------------------------------------------------------ format strings

    [Fact]
    public async Task StringFormat_ItemsAreParameters() => await VerifyAsync(Wrap("""
        string M(int id) => string.Format("SELECT Id FROM Customers WHERE Id = {0} AND Name = '{1}'", id, "x");
        """));

    [Fact]
    public async Task StringFormat_ErrorsAreMappedExactly() => await VerifyAsync(Wrap("""
        string M(int id) => string.Format("SELECT '{{x}}' AS a, [|Nmae|] FROM Customers WHERE Id = {0,5:N0}", id);
        """), Descriptors.InvalidColumnId);

    [Fact]
    public async Task StringFormat_TableItem_IsNotValidated() => await VerifyAsync(Wrap("""
        string M(string table) => string.Format("SELECT Whatever FROM {0} WHERE X = 1", table);
        """));

    [Fact]
    public async Task StringFormat_WithProvider() => await VerifyAsync(Wrap("""
        string M(int id) => string.Format(CultureInfo.InvariantCulture, "SELECT * FROM [|Custmers|] WHERE Id = {0}", id);
        """), Descriptors.InvalidObjectId);

    [Fact]
    public async Task StringFormat_ConstantArgument_IsSubstituted() => await VerifyAsync(Wrap("""
        const string Columns = "Id, Nmae";
        string M(int id) => string.Format("SELECT {0} FROM Customers WHERE Id = {1}", Columns, id);
        """).Replace("{0} FROM", "[|{0}|] FROM"), Descriptors.InvalidColumnId);

    [Fact]
    public async Task StringFormat_InConcatenation() => await VerifyAsync(Wrap("""
        string M(int id) => string.Format("SELECT Id FROM Customers WHERE Id = {0}", id) + " ORDER BY [|Nmae|]";
        """), Descriptors.InvalidColumnId);

    [Fact]
    public async Task StringConcat_IsOneStatement() => await VerifyAsync(Wrap("""
        string s = string.Concat("SELECT Id ", "FROM [|Custmers|]");
        """), Descriptors.InvalidObjectId);

    [Fact]
    public async Task EfRawSql_ItemsAreParameters() => await VerifyAsync(Wrap("""
        void M(object set, string name)
        {
            set.FromSqlRaw("SELECT * FROM Customers WHERE Name = {0} AND [|Bogus|] = 1", name);
            set.ExecuteSqlRaw(@"UPDATE Customers SET Name = {0} WHERE Id = {1}", name, 1);
        }
        """), Descriptors.InvalidColumnId);

    [Fact]
    public async Task FormatLikeConstant_IsNotASyntaxError() => await VerifyAsync(Wrap("""
        const string Sql = "SELECT * FROM Customers WHERE Id = {0}";
        string M(int id) => string.Format(Sql, id);
        """));

    [Fact]
    public async Task VerbatimAndRawFormatStrings() => await VerifyAsync(Wrap(""""
        string A(int id) => string.Format(@"SELECT Id FROM Customers WHERE Name = 'a' AND Id = {0} AND [|Nam|] = 1", id);
        string B(int id) => string.Format("""
            SELECT Id
            FROM Customers
            WHERE Id = {0} AND [|Emial|] = 1
            """, id);
        """"), Descriptors.InvalidColumnId, Descriptors.InvalidColumnId);

    [Fact]
    public async Task OdbcEscapes_AreLeftAlone() => await VerifyAsync(Wrap("""
        string s = "SELECT {fn UCASE(Name)} FROM Customers";
        """));

    // ------------------------------------------------------------------ += and x = x + ...

    [Fact]
    public async Task PlusEquals_IsValidatedAsOneStatement() => await VerifyAsync(Wrap("""
        string M(string region)
        {
            var sql = "SELECT Id, Name FROM Customers WHERE 1 = 1";
            if (region != null)
                sql += " AND [|Regon|] = @region";
            sql += " ORDER BY Name";
            return sql;
        }
        """), Descriptors.InvalidColumnId);

    [Fact]
    public async Task IncompleteFirstPiece_IsNotASyntaxError() => await VerifyAsync(Wrap("""
        int M(IDbConnection c, int id)
        {
            var sql = "SELECT * FROM Customers WHERE";
            sql += " Id = @id";
            return c.Execute(sql);
        }
        """));

    [Fact]
    public async Task IfElse_FollowsFirstBranchOnly() => await VerifyAsync(Wrap("""
        string M(bool byName)
        {
            var sql = "SELECT Id, Name FROM Customers";
            if (byName) { sql += " ORDER BY Name"; }
            else { sql += " ORDER BY Id"; }
            return sql;
        }
        """));

    [Fact]
    public async Task SelfAppend_AndInterpolatedPieces() => await VerifyAsync(Wrap("""
        string M(int id, string sort)
        {
            string query = $"SELECT o.Id FROM Orders o WHERE o.CustomerId = {id}";
            query = query + $" AND o.[|Totl|] > {id}";
            query += $" ORDER BY {sort}";
            return query;
        }
        """), Descriptors.InvalidColumnId);

    [Fact]
    public async Task Loop_BecomesPlaceholder() => await VerifyAsync(Wrap("""
        string M(List<string> columns)
        {
            var sql = "SELECT ";
            foreach (var column in columns)
                sql += column + ", ";
            sql += "1 FROM [|Custmers|]";
            return sql;
        }
        """), Descriptors.InvalidObjectId);

    [Fact]
    public async Task Reassignment_StartsANewStatement() => await VerifyAsync(Wrap("""
        void M(IDbConnection c)
        {
            var sql = "SELECT Id FROM Customers";
            c.Execute(sql);
            sql = "SELECT Id FROM Orders";
            sql += " WHERE [|Name|] = 'x'";
            c.Execute(sql);
        }
        """), Descriptors.InvalidColumnId);

    [Fact]
    public async Task AssignedConcatenation_StartsAStatement() => await VerifyAsync(Wrap("""
        string M(string table)
        {
            string sql;
            sql = "SELECT Id " + "FROM Customers";
            sql += " WHERE [|Nmae|] = 1";
            return sql;
        }
        """), Descriptors.InvalidColumnId);

    [Fact]
    public async Task NonSqlStrings_BuiltTheSameWay_AreIgnored() => await VerifyAsync(Wrap("""
        string M(string name)
        {
            var log = "delete failed for user ";
            log += name;
            log += " at step 3";
            var sb = new StringBuilder("update available: ");
            sb.Append(name).AppendLine();
            return log + sb;
        }
        """));

    [Fact]
    public async Task ErrorInFirstPiece_IsReportedOnce() => await VerifyAsync(Wrap("""
        string M()
        {
            var sql = "SELECT Id FROM [|Custmers|]";
            sql += " WHERE Id = 1";
            return sql;
        }
        """), Descriptors.InvalidObjectId);

    [Fact]
    public async Task IgnoreComment_OnAnyPiece() => await VerifyAsync(Wrap("""
        string M()
        {
            var sql = "SELECT Id FROM Customers";
            // sqllense:ignore
            sql += " WHERE Nmae = 1";
            return sql;
        }
        """));

    [Fact]
    public async Task UnknownMutation_EndsTheStatement() => await VerifyAsync(Wrap("""
        string M()
        {
            var sql = "SELECT Id FROM Customers WHERE";
            sql = sql.Replace("WHERE", "");
            sql += " WHERE Nmae = 1";
            return sql;
        }
        """));

    [Fact]
    public async Task Directives_InTrailingComments() => await VerifyAsync(Wrap("""
        string a = // lang=sql
            "Select * From [|Nope|]";
        string b = /* sqllense:ignore */ "SELECT * FORM Customers";
        string c = string.Format(/*lang=sql*/ "Select {0} From [|Nope|]", 1);
        """), Descriptors.InvalidObjectId, Descriptors.InvalidObjectId);

    // ------------------------------------------------------------------ StringBuilder

    [Fact]
    public async Task StringBuilder_AppendCalls() => await VerifyAsync(Wrap("""
        string M(int id, decimal min)
        {
            var sb = new StringBuilder("SELECT o.Id, o.Total");
            sb.AppendLine(" FROM Orders o");
            sb.Append("JOIN Customers c ON c.Id = o.CustomerId ").Append("WHERE c.Id = @id");
            sb.AppendFormat(" AND o.[|Totl|] > {0}", min);
            return sb.ToString();
        }
        """), Descriptors.InvalidColumnId);

    [Fact]
    public async Task StringBuilder_EmptyConstructor_AndCapacity() => await VerifyAsync(Wrap("""
        string A()
        {
            var sb = new StringBuilder();
            sb.Append("SELECT Id ");
            sb.Append("FROM [|Custmers|]");
            return sb.ToString();
        }

        string B()
        {
            StringBuilder sb = new(256);
            sb.Append("SELECT Id FROM Customers WHERE");
            sb.Append(" Id = 1");
            return sb.ToString();
        }
        """), Descriptors.InvalidObjectId);

    // ------------------------------------------------------------------ inlined locals

    [Fact]
    public async Task InlinedLocal_InInterpolationHole() => await VerifyAsync(Wrap("""
        string M()
        {
            var where = "WHERE [|Nmae|] = @n";
            return $"SELECT Id FROM Customers {where}";
        }
        """), Descriptors.InvalidColumnId);

    [Fact]
    public async Task InlinedLocal_AsFirstOperand() => await VerifyAsync(Wrap("""
        string M()
        {
            var baseSql = "SELECT Id FROM Customers";
            return baseSql + " WHERE [|Nmae|] = 1";
        }
        """), Descriptors.InvalidColumnId);

    [Fact]
    public async Task InlinedLocal_ThatIsSqlItself_IsReportedOnce() => await VerifyAsync(Wrap("""
        string M()
        {
            var baseSql = "SELECT [|Nmae|] FROM Customers";
            return baseSql + " WHERE Id = 1";
        }
        """), Descriptors.InvalidColumnId);

    [Fact]
    public async Task ReassignedLocal_IsNotInlined() => await VerifyAsync(Wrap("""
        string M(bool b)
        {
            var where = "WHERE Nmae = 1";
            if (b) where = "WHERE Id = 1";
            return $"SELECT Id FROM Customers {where}";
        }
        """));

    [Fact]
    public async Task ReadmeExamples() => await VerifyAsync(Wrap("""
        string A(string region, string sortColumn, decimal min, int id)
        {
            var where = "WHERE [|Regon|] = @region";
            var q = $"SELECT Id FROM Customers {where}";

            var sql = "SELECT Id, Name FROM Customers WHERE 1 = 1";
            if (region != null)
                sql += " AND Name = @region";
            sql += string.Format(" ORDER BY {0}", sortColumn);

            var sb = new StringBuilder("SELECT o.Id FROM Orders o");
            sb.AppendLine(" WHERE o.Total > @min")
              .AppendFormat(" AND o.[|Customer|] = {0}", id);
            return q + sql + sb;
        }
        """), Descriptors.InvalidColumnId, Descriptors.InvalidColumnId);

    // ------------------------------------------------------------------ editor services

    private static SqlDocumentRegions Regions(string source) =>
        SqlEditorServices.ComputeRegions(CSharpSyntaxTree.ParseText(source).GetRoot(), CancellationToken.None);

    [Fact]
    public void Classify_ColorsEveryPiece_InOrder()
    {
        const string source = """
            class C
            {
                string M(bool b)
                {
                    var sql = "SELECT Id FROM Customers";
                    if (b) sql += " WHERE Id = @id";
                    var other = "SELECT 1 FROM Orders";
                    sql += " ORDER BY Name";
                    return sql + other;
                }
            }
            """;
        var regions = Regions(source);
        var spans = SqlEditorServices.Classify(regions, new TextSpan(0, source.Length)).ToList();
        var keywords = spans.Where(s => s.Class == SqlTokenClass.Keyword).Select(s => source.Substring(s.Span.Start, s.Span.Length)).ToList();
        Assert.Equal(new[] { "SELECT", "FROM", "WHERE", "SELECT", "FROM", "ORDER", "BY" }, keywords);
        Assert.Equal(spans.OrderBy(s => s.Span.Start).Select(s => s.Span), spans.Select(s => s.Span));
    }

    [Fact]
    public void Classify_SkipsFormatItems()
    {
        const string source = """class C { string M(int id) => string.Format("SELECT Id FROM T WHERE Id = {0}", id); }""";
        var spans = SqlEditorServices.Classify(Regions(source), new TextSpan(0, source.Length))
            .Select(s => source.Substring(s.Span.Start, s.Span.Length)).ToList();
        Assert.Contains("WHERE", spans);
        Assert.DoesNotContain(spans, s => s.Contains('{'));
    }

    [Fact]
    public void Classify_InlinedLocal_WhenOnlyItIsVisible()
    {
        const string source = """
            class C
            {
                string M()
                {
                    var where = "WHERE Id = @id";
                    return $"SELECT Name FROM Customers {where}";
                }
            }
            """;
        var start = source.IndexOf("\"WHERE", StringComparison.Ordinal);
        var spans = SqlEditorServices.Classify(Regions(source), new TextSpan(start, 10))
            .Select(s => source.Substring(s.Span.Start, s.Span.Length)).ToList();
        Assert.Equal(new[] { "WHERE", "Id", "=" }, spans);
    }

    [Fact]
    public void QuickInfo_InLaterPiece()
    {
        const string source = """
            class C
            {
                string M()
                {
                    var sql = "SELECT c.Id FROM Customers c";
                    sql += " WHERE c.Email = @e";
                    return sql;
                }
            }
            """;
        var info = SqlEditorServices.GetQuickInfo(Regions(source), source.IndexOf("Email", StringComparison.Ordinal) + 1, Core.Tests.TestSchema.Shop);
        Assert.NotNull(info);
        Assert.StartsWith("(column) dbo.Customers.Email", info!.Description);
    }
}
