using Loadstone.Manifests;

namespace Loadstone.Tests;

/// <summary>A small orders dataset (Order → Line) shared across the test suite.</summary>
internal static class TestData
{
    public static DatasetManifest Orders() => new()
    {
        Name = "orders",
        Description = "Test dataset",
        Root = new EntityDefinition
        {
            Name = "Order",
            Table = "Orders",
            KeyColumn = "OrderId",
            NaturalKey = ["OrderNumber"],
            Fields =
            [
                new FieldDefinition { Name = "OrderNumber", Required = true, MaxLength = 20 },
                new FieldDefinition { Name = "OrderDate", Type = FieldKind.Date },
                new FieldDefinition { Name = "Country", Lookup = new LookupSettings { List = "countries" } },
                new FieldDefinition { Name = "Total", Type = FieldKind.Decimal },
            ],
            Children =
            [
                new EntityDefinition
                {
                    Name = "Line",
                    Table = "OrderLines",
                    KeyColumn = "LineId",
                    ParentKeyColumn = "OrderId",
                    NaturalKey = ["LineNumber"],
                    Fields =
                    [
                        new FieldDefinition { Name = "LineNumber", Type = FieldKind.Int32, Required = true },
                        new FieldDefinition { Name = "Sku" },
                        new FieldDefinition { Name = "Quantity", Type = FieldKind.Int32 },
                    ],
                },
            ],
        },
    };

    public static Stream AsStream(string content) =>
        new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
}
