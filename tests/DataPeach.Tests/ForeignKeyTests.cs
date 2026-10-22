using DataPeach.Core.Db;

namespace DataPeach.Tests;

public sealed class ForeignKeyTests
{
    [Fact]
    public void GroupsOneRowPerColumnPairIntoKeys()
    {
        var keys = DbForeignKey.FromRows(new[]
        {
            new[] { "sales", "lines", "fk_order", "order_id", "sales", "orders", "id" },
            new[] { "sales", "lines", "fk_product", "product_id", "stock", "products", "id" },
            new[] { "sales", "lines", "fk_product", "variant", "stock", "products", "variant" },
            new[] { "sales", "orders", "fk_order", "user_id", "sales", "users", "id" },
        });

        Assert.Equal(3, keys.Count);
        Assert.Equal(new[] { "product_id", "variant" }, keys[1].Columns);
        Assert.Equal(new[] { "id", "variant" }, keys[1].RefColumns);
        // Same name on two tables: two keys.
        Assert.Equal(new[] { "lines", "orders" }, keys.Where(k => k.Name == "fk_order").Select(k => k.Table));
    }

    [Fact]
    public void ShowsTheReferencedSchemaOnlyWhenItIsAnother()
    {
        var keys = DbForeignKey.FromRows(new[]
        {
            new[] { "sales", "lines", "fk_order", "order_id", "sales", "orders", "id" },
            new[] { "sales", "lines", "fk_product", "product_id", "stock", "products", "id" },
            new string?[] { null, "orders", "fk0", "user_id", null, "users", "id" },
        });

        Assert.Equal(new[] { "(order_id) → orders (id)", "(product_id) → stock.products (id)", "(user_id) → users (id)" },
                     keys.Select(k => k.Definition));
    }

    [Fact]
    public void NoRowsNoKeys() => Assert.Empty(DbForeignKey.FromRows(Array.Empty<string?[]>()));
}
