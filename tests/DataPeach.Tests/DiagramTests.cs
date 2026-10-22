using DataPeach.Core.Db;
using DataPeach.Core.Diagram;

namespace DataPeach.Tests;

public sealed class DiagramTests
{
    // users ← orders ← lines → products ← stock; audit references nothing; v_users is a view.
    private static readonly SchemaSnapshot Shop = new()
    {
        Schemas = new() { "sales", "stock" },
        Tables =
        {
            Table("sales", "audit"), Table("sales", "lines"), Table("sales", "orders"), Table("sales", "users"),
            new DbTable { Schema = "sales", Name = "v_users", Kind = DbObjectKind.View },
            Table("stock", "products"), Table("stock", "stock"),
        },
    };

    private static readonly List<DbForeignKey> Keys = new()
    {
        Key("sales", "orders", "sales", "users"),
        Key("sales", "lines", "sales", "orders"),
        Key("sales", "lines", "stock", "products"),
        Key("stock", "stock", "stock", "products"),
        Key("sales", "orders", "sales", "missing"), // points outside what was read: left out
    };

    private static DbTable Table(string schema, string name) => new() { Schema = schema, Name = name };
    private static DbForeignKey Key(string s, string t, string rs, string rt) => new("fk_" + t + "_" + rt, s, t, new[] { "x" }, rs, rt, new[] { "id" });
    private static string[] Names(DiagramGraph g) => g.Tables.Select(t => t.Name).ToArray();

    [Fact]
    public void ShowsTheTablesOfASchemaAndTheKeysBetweenThem()
    {
        var g = DiagramGraph.Build(Shop, Keys, "sales");
        Assert.Equal(new[] { "audit", "lines", "orders", "users" }, Names(g));
        Assert.Equal(new[] { "lines→orders", "orders→users" }, g.Edges.Select(e => e.From.Name + "→" + e.To.Name).OrderBy(x => x));
    }

    [Fact]
    public void WithoutASchemaShowsEveryTableButNotTheViews()
    {
        var g = DiagramGraph.Build(Shop, Keys);
        Assert.Equal(6, g.Tables.Count);
        Assert.DoesNotContain(g.Tables, t => t.Kind == DbObjectKind.View);
        Assert.Equal(4, g.Edges.Count);
    }

    [Fact]
    public void AroundATableFollowsKeysBothWaysAndAcrossSchemas()
    {
        var orders = Shop.Find("sales", "orders")!;
        Assert.Equal(new[] { "lines", "orders", "users" }, Names(DiagramGraph.Build(Shop, Keys, focus: orders, hops: 1)));
        Assert.Equal(new[] { "lines", "orders", "users", "products" }, Names(DiagramGraph.Build(Shop, Keys, focus: orders, hops: 2)));
        Assert.Equal(new[] { "lines", "orders", "users", "products", "stock" }, Names(DiagramGraph.Build(Shop, Keys, focus: orders, hops: 0)));
        Assert.Equal(new[] { "audit" }, Names(DiagramGraph.Build(Shop, Keys, focus: Shop.Find("sales", "audit"), hops: 0)));
    }

    private static bool Overlap((double X, double Y) a, (double W, double H) sa, (double X, double Y) b, (double W, double H) sb) =>
        a.X < b.X + sb.W && b.X < a.X + sa.W && a.Y < b.Y + sb.H && b.Y < a.Y + sa.H;

    private static void NoOverlaps((double X, double Y)[] pos, IReadOnlyList<(double W, double H)> sizes)
    {
        for (int i = 0; i < pos.Length; i++)
            for (int j = i + 1; j < pos.Length; j++)
                Assert.False(Overlap(pos[i], sizes[i], pos[j], sizes[j]), $"boxes {i} and {j} overlap");
    }

    [Fact]
    public void PutsAReferencedTableLeftOfTheOnesThatPointAtIt()
    {
        // 0 users, 1 orders → 0, 2 lines → 1 and 3, 3 products, 4 audit (alone).
        var sizes = new[] { (120.0, 80.0), (140.0, 100.0), (130.0, 90.0), (150.0, 60.0), (100.0, 40.0) };
        var pos = DiagramLayout.Arrange(sizes, new[] { (1, 0), (2, 1), (2, 3) });
        Assert.True(pos[0].X + sizes[0].Item1 < pos[1].X);
        Assert.True(pos[1].X + sizes[1].Item1 < pos[2].X);
        Assert.True(pos[3].X + sizes[3].Item1 < pos[2].X);
        Assert.All(pos, p => Assert.True(p.X >= 0 && p.Y >= 0));
        NoOverlaps(pos, sizes);
    }

    [Fact]
    public void SurvivesCyclesAndSelfReferences()
    {
        var sizes = Enumerable.Repeat((100.0, 50.0), 3).ToArray();
        var pos = DiagramLayout.Arrange(sizes, new[] { (0, 1), (1, 2), (2, 0), (1, 1) });
        NoOverlaps(pos, sizes);
    }

    [Fact]
    public void ManyLoneTablesFillRowsInsteadOfOneLongLine()
    {
        var sizes = Enumerable.Repeat((100.0, 60.0), 100).ToArray();
        var pos = DiagramLayout.Arrange(sizes, Array.Empty<(int, int)>());
        NoOverlaps(pos, sizes);
        double w = pos.Max(p => p.X) + 100, h = pos.Max(p => p.Y) + 60;
        Assert.InRange(w / h, 0.5, 3);
    }

    [Fact]
    public void NothingToPlace() => Assert.Empty(DiagramLayout.Arrange(Array.Empty<(double, double)>(), Array.Empty<(int, int)>()));
}
