using SqlLense.Schema;
using SqlLense.SchemaReader;
using SqlLense.Sql;
using Xunit;

namespace SqlLense.Core.Tests;

public class SchemaReaderTests
{
    public static IEnumerable<object[]> CatalogQueries() => new[]
    {
        new object[] { SqlServerSchemaReader.DatabaseInfoQuery },
        new object[] { SqlServerSchemaReader.ObjectsQuery },
        new object[] { SqlServerSchemaReader.ParametersQuery },
    };

    [Theory]
    [MemberData(nameof(CatalogQueries))]
    public void CatalogQueries_AreValidSql_AndSystemViewsAreNotFlagged(string sql) =>
        Assert.Empty(SqlEngine.Analyze(sql, TestSchema.Shop).Diagnostics);

    [Theory]
    [InlineData("nvarchar", 200, 0, 0, "nvarchar(100)")]
    [InlineData("nvarchar", -1, 0, 0, "nvarchar(max)")]
    [InlineData("varchar", 50, 0, 0, "varchar(50)")]
    [InlineData("decimal", 9, 18, 2, "decimal(18,2)")]
    [InlineData("datetime2", 8, 27, 7, "datetime2")]
    [InlineData("datetime2", 6, 23, 3, "datetime2(3)")]
    [InlineData("int", 4, 10, 0, "int")]
    public void FormatType(string type, int maxLength, int precision, int scale, string expected) =>
        Assert.Equal(expected, SqlServerSchemaReader.FormatType(type, maxLength, precision, scale));

    [Theory]
    [InlineData("U", SchemaObjectKind.Table)]
    [InlineData("V", SchemaObjectKind.View)]
    [InlineData("PC", SchemaObjectKind.Procedure)]
    [InlineData("IF", SchemaObjectKind.TableFunction)]
    [InlineData("FN", SchemaObjectKind.ScalarFunction)]
    [InlineData("SN", SchemaObjectKind.Synonym)]
    public void MapKind(string code, SchemaObjectKind expected) =>
        Assert.Equal(expected, SqlServerSchemaReader.MapKind(code));
}
