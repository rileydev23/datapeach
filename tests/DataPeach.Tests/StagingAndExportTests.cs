using System.Xml.Linq;
using DataPeach.Core;
using DataPeach.Core.Data;
using DataPeach.Core.Db;
using DataPeach.Core.Drivers;
using DataPeach.Core.Staging;

namespace DataPeach.Tests;

public class StagingTests
{
    private static DbTable Users()
    {
        var t = new DbTable { Schema = "public", Name = "users" };
        t.Columns.Add(new DbColumn("id", "integer", false, true, null));
        t.Columns.Add(new DbColumn("name", "text", true, false, null));
        t.Columns.Add(new DbColumn("Active", "boolean", true, false, null));
        return t;
    }

    [Fact]
    public void EditsOfOneRowBecomeOneUpdate()
    {
        var s = new StagingArea();
        var t = Users();
        var key = new RowKey(new object?[] { 7 });
        s.Edit(t, key, "name", "Ana", "Ana María");
        s.Edit(t, key, "Active", true, false);
        var st = Assert.Single(s.Build(PostgresDialect.Instance));
        Assert.Equal("UPDATE users SET name = 'Ana María', \"Active\" = FALSE WHERE id = 7;", st.Preview);
        Assert.Equal("UPDATE users SET name = CAST(@p0 AS text), \"Active\" = @p1 WHERE id = @p2", st.Command.Sql);
        Assert.Equal(new object?[] { "Ana María", false, 7 }, st.Command.Parameters);
    }

    [Fact]
    public void SettingTheOriginalValueBackDropsTheEdit()
    {
        var s = new StagingArea();
        var t = Users();
        var key = new RowKey(new object?[] { 1 });
        s.Edit(t, key, "name", "a", "b");
        s.Edit(t, key, "name", "a", "a");
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void DeletingARowDropsItsEdits()
    {
        var s = new StagingArea();
        var t = Users();
        var key = new RowKey(new object?[] { 1 });
        s.Edit(t, key, "name", "a", "b");
        s.Delete(t, key);
        Assert.Equal("DELETE FROM public.users WHERE id = 1;", Assert.Single(s.Build(MySqlDialect.Instance)).Preview);
    }

    [Fact]
    public void InsertsUseOnlyTheColumnsTyped()
    {
        var s = new StagingArea();
        var t = Users();
        var ins = s.AddInsert(t);
        Assert.Equal("INSERT INTO users DEFAULT VALUES;", s.Build(PostgresDialect.Instance)[0].Preview);
        s.EditInsert(ins, "name", null);
        Assert.Equal("INSERT INTO users (name) VALUES (NULL);", s.Build(PostgresDialect.Instance)[0].Preview);
    }

    [Fact]
    public void TablesWithoutPrimaryKeyCannotBeEdited()
    {
        var t = new DbTable { Name = "log" };
        t.Columns.Add(new DbColumn("msg", "text", true, false, null));
        Assert.Throws<InvalidOperationException>(() => new StagingArea().Delete(t, new RowKey(new object?[] { "x" })));
    }
}

public class ExportTests
{
    private static readonly ResultColumn[] Cols =
    {
        new("id", "int4", typeof(int)), new("name", "text", typeof(string)), new("paid", "bool", typeof(bool)),
    };

    private static readonly object?[][] Rows = { new object?[] { 1, "a,b", true }, new object?[] { 2, null, false } };

    [Fact]
    public void Csv() => Assert.Equal("id,name,paid\r\n1,\"a,b\",true\r\n2,,false\r\n", Exporters.Separated(Cols, Rows, ',', true));

    [Fact]
    public void Json()
    {
        var json = Exporters.Json(Cols, Rows);
        Assert.Contains("\"name\": \"a,b\"", json);
        Assert.Contains("\"name\": null", json);
        Assert.Contains("\"paid\": true", json);
    }

    [Fact]
    public void Markdown() => Assert.StartsWith("| id | name | paid |\n| ---: | --- | --- |\n| 1 | a,b | true |", Exporters.Markdown(Cols, Rows));

    [Fact]
    public void Inserts() =>
        Assert.Equal("INSERT INTO t (id, name, paid) VALUES (1, 'a,b', 1);\nINSERT INTO t (id, name, paid) VALUES (2, NULL, 0);\n",
            Exporters.Inserts(Cols, Rows, SqliteDialect.Instance, "t"));

    [Fact]
    public void TypeScript() =>
        Assert.Equal("export interface Category {\n  id: number | null;\n  name: string | null;\n  paid: boolean | null;\n}\n",
            Exporters.TypeScript(Cols, "public.categories"));

    [Theory]
    [InlineData("hola\nmundo", "hola⏎mundo")]
    [InlineData(null, null)]
    public void CellsAreOneLine(string? value, string? expected) => Assert.Equal(expected, ValueText.Cell(value));

    [Fact]
    public void StructsReadAsKeyAndValue()
    {
        var row = new Dictionary<string, object?> { ["vip"] = true, ["nivel"] = 2, ["tags"] = new List<string> { "a", "b" }, ["nota"] = null };
        Assert.Equal("{vip: true, nivel: 2, tags: {a, b}, nota: NULL}", ValueText.Full(row));
    }
}

public class NuGetTests
{
    [Theory]
    [InlineData("net10.0", 10, 20)]
    [InlineData("net8.0", 10, 18)]
    [InlineData("net11.0", 10, -1)]
    [InlineData("netstandard2.0", 10, 2)]
    [InlineData(".NETStandard2.1", 10, 3)]
    [InlineData("net462", 10, -1)]
    [InlineData("net8.0-windows7.0", 10, -1)]
    public void RanksFrameworks(string tfm, int runtime, int rank) => Assert.Equal(rank, NuGetPackage.Rank(tfm, runtime));

    [Theory]
    [InlineData("[2.1.10, )", "2.1.10")]
    [InlineData("8.0.0", "8.0.0")]
    [InlineData("[1.0.0]", "1.0.0")]
    public void ReadsTheLowestVersionOfARange(string range, string v) => Assert.Equal(v, NuGetPackage.MinVersion(range));

    [Fact]
    public void PicksTheBestDependencyGroup()
    {
        var doc = XDocument.Parse("""
            <package><metadata><dependencies>
              <group targetFramework=".NETStandard2.0"><dependency id="Old" version="1.0.0" /></group>
              <group targetFramework="net8.0"><dependency id="New" version="[2.0.0, )" /></group>
              <group targetFramework="net462"><dependency id="Framework" version="1.0.0" /></group>
            </dependencies></metadata></package>
            """);
        Assert.Equal(new[] { new PackageRef("New", "2.0.0") }, NuGetPackage.Dependencies(doc, 10));
    }

    [Fact]
    public void CopiesTheBestLibFolderAndTheNativeLibraryOfThisMachine()
    {
        var files = NuGetPackage.FilesToCopy(new[]
        {
            "lib/netstandard2.0/A.dll", "lib/net8.0/A.dll", "lib/net8.0/A.xml",
            "runtimes/win-x64/native/e_sqlite3.dll", "runtimes/linux-x64/native/libe_sqlite3.so",
        }, 10, new[] { "win-x64", "win" });
        Assert.Equal(new[] { ("lib/net8.0/A.dll", "A.dll"), ("runtimes/win-x64/native/e_sqlite3.dll", "e_sqlite3.dll") }, files);
    }

    [Fact]
    public void ComparesVersions()
    {
        Assert.True(NuGetPackage.CompareVersions("10.0.3", "9.0.4") > 0);
        Assert.True(NuGetPackage.CompareVersions("2.0.0", "2.0.0-preview1") > 0);
        Assert.Equal("8.0.0", NuGetPackage.NormalizeVersion("8.0.0.0"));
    }
}

public class GroupingTests
{
    [Fact]
    public void DeletingAGroupKeepsItsConnectionsUngrouped()
    {
        var s = new AppState();
        var g = ConnectionGrouping.Ensure(s, "Work");
        var c = new ConnectionEntry { Name = "db", Group = "Work" };
        s.Connections.Add(c);
        ConnectionGrouping.Delete(s, g, removeConnections: false);
        Assert.Null(c.Group);
        Assert.Single(ConnectionGrouping.Build(s, _ => true, hideEmpty: false));
    }
}
