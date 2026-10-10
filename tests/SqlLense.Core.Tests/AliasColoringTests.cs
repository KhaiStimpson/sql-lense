using SqlLense.Sql;
using Xunit;

namespace SqlLense.Core.Tests;

public class AliasColoringTests
{
    /// <summary>Spans as "text=color" (role initial for clarity: d/r/o).</summary>
    private static string[] Render(string sql, SqlAliasParts parts = SqlAliasParts.All) =>
        SqlAliasResolver.Classify(sql, parts)
            .Select(s => $"{sql.Substring(s.Start, s.Length)}={s.ColorIndex}{char.ToLowerInvariant(s.Role.ToString()[0])}")
            .ToArray();

    [Fact]
    public void TableAliases_GetOneColorEach_InOrderOfDeclaration()
    {
        const string sql = "SELECT c.Name, o.Total FROM dbo.Customers c JOIN Orders AS o ON o.CustomerId = c.Id";
        Assert.Equal(
            new[] { "c=0r", "o=1r", "dbo.Customers=0a", "c=0d", "Orders=1a", "o=1d", "o=1r", "c=0r" },
            Render(sql));
    }

    [Fact]
    public void Parts_SelectWhichSpansAreColored()
    {
        const string sql = "SELECT c.Name FROM Customers c";
        Assert.Equal(new[] { "c=0r" }, Render(sql, SqlAliasParts.References));
        Assert.Equal(new[] { "c=0d" }, Render(sql, SqlAliasParts.Declarations));
        Assert.Equal(new[] { "Customers=0a" }, Render(sql, SqlAliasParts.AliasedObjects));
        Assert.Empty(Render(sql, SqlAliasParts.None));
    }

    [Fact]
    public void ColumnAndCteAliases_AreColoredWithReferences_AndCanBeTurnedOff()
    {
        const string sql = """
            WITH Recent AS (SELECT CustomerId, SUM(Total) AS Spent FROM Orders GROUP BY CustomerId)
            SELECT r.Spent FROM Recent r ORDER BY Spent
            """;
        Assert.Equal(
            new[] { "Recent=0d", "Spent=1d", "r=2r", "Spent=1r", "Recent=0r", "r=2d", "Spent=1r" },
            Render(sql));

        // Only the table alias is left, and it now takes the first color.
        Assert.Equal(
            new[] { "r=0r", "Recent=0a", "r=0d" },
            Render(sql, SqlAliasParts.All & ~SqlAliasParts.ColumnAndCteAliases));
    }

    [Fact]
    public void SelectAlias_InOrderBy_IsAReference_ButNotElsewhere()
    {
        const string sql = "SELECT Total * 2 AS Doubled FROM Orders WHERE Total > 1 ORDER BY Doubled";
        Assert.Equal(new[] { "Doubled=0d", "Doubled=0r" }, Render(sql));
    }

    [Fact]
    public void OrderByAfterUnion_SeesFirstQueryAliases()
    {
        const string sql = "SELECT Id AS Key1 FROM Customers UNION SELECT Id FROM Orders ORDER BY Key1";
        Assert.Equal(new[] { "Key1=0d", "Key1=0r" }, Render(sql));
    }

    [Fact]
    public void DerivedTable_ExposesItsColumnAliases()
    {
        const string sql = "SELECT t.Cnt, Cnt FROM (SELECT COUNT(*) AS Cnt FROM Orders) t";
        Assert.Equal(new[] { "t=1r", "Cnt=0r", "Cnt=0r", "Cnt=0d", "t=1d" }, Render(sql));
    }

    [Fact]
    public void NestedScopes_GiveRedeclaredAliasesTheirOwnColor()
    {
        const string sql = """
            SELECT c.Name FROM Customers c
            WHERE EXISTS (SELECT 1 FROM Orders c WHERE c.Total > 0)
            """;
        var spans = Render(sql, SqlAliasParts.References | SqlAliasParts.Declarations);
        Assert.Equal(new[] { "c=0r", "c=0d", "c=1d", "c=1r" }, spans);
    }

    [Fact]
    public void CorrelatedSubquery_ResolvesOuterAlias()
    {
        const string sql = "SELECT c.Name FROM Customers c WHERE EXISTS (SELECT 1 FROM Orders o WHERE o.CustomerId = c.Id)";
        Assert.Contains("c=0r", Render(sql, SqlAliasParts.References).Skip(2));
    }

    [Fact]
    public void UpdateAndDeleteTargets_ReferenceTheirAlias()
    {
        Assert.Equal(
            new[] { "c=0r", "c=0r", "Customers=0a", "c=0d" },
            Render("UPDATE c SET c.Name = 'x' FROM Customers c"));
        Assert.Equal(
            new[] { "c=0r", "Customers=0a", "c=0d" },
            Render("DELETE c FROM Customers c"));
    }

    [Fact]
    public void Merge_ColorsTargetAndSourceAliases()
    {
        const string sql = """
            MERGE INTO Customers AS t USING Staging AS s ON t.Id = s.Id
            WHEN MATCHED THEN UPDATE SET t.Name = s.Name;
            """;
        var spans = Render(sql);
        Assert.Contains("t=0d", spans);
        Assert.Contains("s=1d", spans);
        Assert.Equal(2, spans.Count(s => s == "t=0r"));
        Assert.Equal(2, spans.Count(s => s == "s=1r"));
    }

    [Fact]
    public void QuotedAliases_AndStarQualifiers()
    {
        Assert.Equal(new[] { "[c]=0r", "Customers=0a", "[c]=0d" }, Render("SELECT [c].* FROM Customers [c]"));
    }

    [Fact]
    public void PlaceholderTableName_KeepsAliasButIsNotTinted()
    {
        var sql = "SELECT t.Id FROM " + SqlEngine.PlaceholderMarker + "0 t";
        Assert.Equal(new[] { "t=0r", "t=0d" }, Render(sql));
    }

    [Fact]
    public void ManyAliases_NeverShareAColor()
    {
        var joins = string.Join(" ", Enumerable.Range(1, 30).Select(i => $"JOIN Orders a{i} ON a{i}.Id = a0.Id"));
        var spans = SqlAliasResolver.Classify("SELECT 1 FROM Orders a0 " + joins, SqlAliasParts.Declarations);
        Assert.Equal(Enumerable.Range(0, 31), spans.Select(s => s.ColorIndex));
    }

    // ---------------------------------------------------------------- invalid SQL (token fallback)

    [Fact]
    public void IncompleteSql_StillColorsTableAliases()
    {
        const string sql = "SELECT c.Name, o. FROM Customers c JOIN Orders o ON o.CustomerId = c.Id WHERE";
        Assert.True(SqlParser.Parse(sql).HasErrors);
        Assert.Equal(
            new[] { "c=0r", "o=1r", "Customers=0a", "c=0d", "Orders=1a", "o=1d", "o=1r", "c=0r" },
            Render(sql));
    }

    [Fact]
    public void IncompleteSql_ColorsCtesColumnAliasesAndDerivedTables()
    {
        const string sql = """
            WITH Recent (Id, Spent) AS (SELECT CustomerId, SUM(Total) FROM Orders GROUP BY CustomerId)
            SELECT CAST(r.Spent AS int) AS Amount, x.Id FROM Recent r JOIN (SELECT Id FROM Customers) x ON WHERE
            """;
        Assert.True(SqlParser.Parse(sql).HasErrors);
        var spans = Render(sql);
        Assert.Contains("Recent=0d", spans);
        Assert.Contains("Spent=2d", spans);
        Assert.Contains("Amount=3d", spans);
        Assert.Contains("Recent=0r", spans);
        Assert.Contains("r=4d", spans);
        Assert.Contains("r=4r", spans);
        Assert.Contains("x=5d", spans);
        Assert.DoesNotContain(spans, s => s.StartsWith("int=", StringComparison.Ordinal));
    }

    [Fact]
    public void IncompleteSql_DoesNotTreatKeywordsAsAliases()
    {
        const string sql = "SELECT * FROM Customers WITH (NOLOCK) CROSS APPLY dbo.Fn(Id) f WHERE";
        Assert.Equal(new[] { "dbo.Fn=0a", "f=0d" }, Render(sql));
    }

    // ---------------------------------------------------------------- palette and presets

    [Fact]
    public void Palette_IsDistinct_AndReadableOnLightAndDarkBackgrounds()
    {
        foreach (var background in new[] { SqlRgb.FromHex("FFFFFF"), SqlRgb.FromHex("1E1E1E"), SqlRgb.FromHex("F5F5F5"), SqlRgb.FromHex("002B36") })
        {
            var colors = Enumerable.Range(0, 64).Select(i => SqlAliasPalette.GetColor(i, background)).ToList();
            Assert.Equal(colors.Count, colors.Distinct().Count());
            Assert.All(colors, c => Assert.True(SqlRgb.Contrast(c, background) >= 4.5, $"{c} on {background}"));
        }
    }

    [Fact]
    public void Palette_FirstColorsAreFarApart()
    {
        var white = SqlRgb.FromHex("FFFFFF");
        var first = Enumerable.Range(0, 6).Select(i => SqlAliasPalette.GetColor(i, white)).ToList();
        for (var i = 0; i < first.Count; i++)
        {
            for (var j = i + 1; j < first.Count; j++)
            {
                var distance = Math.Abs(first[i].R - first[j].R) + Math.Abs(first[i].G - first[j].G) + Math.Abs(first[i].B - first[j].B);
                Assert.True(distance > 60, $"{first[i]} vs {first[j]}");
            }
        }
    }

    [Fact]
    public void Presets_DefineEveryStyledClass_ForBothThemes()
    {
        foreach (var preset in Enum.GetValues<SqlColorPreset>().Where(p => p != SqlColorPreset.Custom))
        {
            foreach (var dark in new[] { false, true })
            {
                var scheme = SqlColorPresets.GetScheme(preset, dark);
                Assert.NotNull(scheme);
                Assert.Equal(SqlColorPresets.StyledClasses.OrderBy(c => c), scheme!.Keys.OrderBy(c => c));
            }
        }

        Assert.Null(SqlColorPresets.GetScheme(SqlColorPreset.Custom, false));
    }

    [Fact]
    public void Rgb_HexRoundTrips()
    {
        Assert.Equal("#3E8EDE", SqlRgb.FromHex("3e8ede").ToString());
        Assert.True(SqlRgb.FromHex("1E1E1E").IsDark);
        Assert.False(SqlRgb.FromHex("FFFFFF").IsDark);
    }
}
