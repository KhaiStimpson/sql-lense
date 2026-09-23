using SqlLense.Schema;

namespace SqlLense.Core.Tests;

internal static class TestSchema
{
    public static readonly DatabaseSchema Shop = new(
        "Shop",
        new[]
        {
            Table("dbo", "Customers", ("Id", "int"), ("Name", "nvarchar(100)"), ("Email", "nvarchar(256)"), ("CreatedAt", "datetime2")),
            Table("dbo", "Orders", ("Id", "int"), ("CustomerId", "int"), ("Total", "decimal(18,2)"), ("PlacedAt", "datetime2")),
            Table("dbo", "OrderLines", ("OrderId", "int"), ("ProductId", "int"), ("Quantity", "int")),
            Table("sales", "Regions", ("RegionId", "int"), ("RegionName", "nvarchar(50)")),
            new SchemaObject("dbo", "ActiveCustomers", SchemaObjectKind.View, new[] { new SchemaColumn("Id", "int", false), new SchemaColumn("Name", "nvarchar(100)", true) }),
            new SchemaObject("dbo", "GetCustomer", SchemaObjectKind.Procedure, parameters: new[] { new SchemaParameter("@id", "int", false) }),
            new SchemaObject("dbo", "fnTax", SchemaObjectKind.ScalarFunction),
            new SchemaObject("dbo", "fnOrdersFor", SchemaObjectKind.TableFunction, new[] { new SchemaColumn("OrderId", "int", false) }),
        });

    private static SchemaObject Table(string schema, string name, params (string Name, string Type)[] columns) =>
        new(schema, name, SchemaObjectKind.Table, columns.Select(c => new SchemaColumn(c.Name, c.Type, c.Name != "Id")).ToArray());
}
