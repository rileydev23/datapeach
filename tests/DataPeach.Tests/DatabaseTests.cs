using DataPeach.Core;
using DataPeach.Core.Db;
using DataPeach.Core.History;
using DataPeach.Core.Staging;

namespace DataPeach.Tests;

/// <summary>Servers with several databases: names, USE, and what the tree and the history keep of each one.</summary>
public class DatabaseTests
{
    [Theory]
    [InlineData(DbKind.SqlServer, "USE ventas", "ventas")]
    [InlineData(DbKind.SqlServer, "use [mi base];", "mi base")]
    [InlineData(DbKind.MySql, "USE `tienda`", "tienda")]
    [InlineData(DbKind.Sybase, "-- cambio\nUSE testdb", "testdb")]
    [InlineData(DbKind.SqlServer, "USE ventas; SELECT 1", null)]
    [InlineData(DbKind.SqlServer, "SELECT 1", null)]
    [InlineData(DbKind.Postgres, "USE ventas", null)]
    public void UseSwitchesDatabaseWhereTheEngineHasIt(DbKind kind, string sql, string? expected) =>
        Assert.Equal(expected, Dialect.For(kind).UsedDatabase(sql));

    [Fact]
    public void OnlyServerEnginesHaveDatabases()
    {
        Assert.True(Dialect.For(DbKind.SqlServer).HasDatabases);
        Assert.True(Dialect.For(DbKind.Postgres).HasDatabases);
        Assert.True(Dialect.For(DbKind.MySql).HasDatabases);
        Assert.True(Dialect.For(DbKind.Sybase).HasDatabases);
        Assert.False(Dialect.For(DbKind.Oracle).HasDatabases);
        Assert.False(Dialect.For(DbKind.Sqlite).HasDatabases);
        Assert.False(Dialect.For(DbKind.DuckDb).HasDatabases);
        Assert.Equal("USE [a]]b]", Dialect.For(DbKind.SqlServer).UseSql("a]b"));
    }

    [Fact]
    public void StampMarksTablesAndRoutinesWithTheirDatabase()
    {
        var t = new DbTable { Schema = "dbo", Name = "clientes" };
        var r = new DbRoutine { Schema = "dbo", Name = "alta" };
        var s = new SchemaSnapshot { Tables = { t }, Routines = { r } }.Stamp("ventas");
        Assert.Equal("ventas", s.Database);
        Assert.Equal("ventas.dbo.clientes", t.FullName);
        Assert.Equal("ventas.dbo.alta", r.FullName);
        // MySQL: the schema is the database, so it is not repeated.
        var m = new DbTable { Schema = "tienda", Name = "productos", Database = "tienda" };
        Assert.Equal("tienda.productos", m.FullName);
        Assert.Equal("clientes", new DbTable { Name = "clientes" }.FullName);
    }

    [Fact]
    public void TheHistoryKeepsTheDatabaseOfADelete()
    {
        var t = new DbTable { Schema = "dbo", Name = "clientes", Database = "ventas" };
        var r = DeleteRecord.Create(new ConnectionEntry { Kind = DbKind.SqlServer }, "console", "dbo", "clientes", "DELETE FROM clientes", 1, null, t, null, DateTimeOffset.Now);
        Assert.Equal("ventas", r.Database);
        Assert.Equal("ventas.dbo.clientes", r.TableDisplay);
        var unknown = DeleteRecord.Create(new ConnectionEntry(), "console", "dbo", "x", "DELETE FROM x", 1, null, null, null, DateTimeOffset.Now, "rrhh");
        Assert.Equal("rrhh", unknown.Database);
        Assert.Equal("x", DeleteRecord.Create(new ConnectionEntry(), "console", null, "x", "", 0, null, null, null, DateTimeOffset.Now).TableDisplay);
    }

    [Fact]
    public void StagingTakesBackOnlyWhatWasApplied()
    {
        var a = new DbTable { Schema = "dbo", Name = "a", Database = "uno", Columns = { new DbColumn("id", "int", false, true, null) } };
        var b = new DbTable { Schema = "dbo", Name = "a", Database = "dos", Columns = { new DbColumn("id", "int", false, true, null) } };
        var staging = new StagingArea();
        staging.Delete(a, new RowKey(new object?[] { 1 }));
        staging.Delete(b, new RowKey(new object?[] { 1 }));
        int changed = 0;
        staging.Changed += () => changed++;
        staging.RemoveAll(staging.Changes.Where(c => c.Table.Database == "uno").ToList());
        Assert.Equal(1, changed);
        Assert.Equal("dos", Assert.Single(staging.Changes).Table.Database);
    }

    [Fact]
    public void ForDatabaseKeepsEverythingButTheDatabase()
    {
        var e = new ConnectionEntry { Kind = DbKind.Sqlite, FilePath = "x.db", Database = "", Mode = ConnectionMode.ReadOnly };
        var s = new DbSession(e, "secreto", Microsoft.Data.Sqlite.SqliteFactory.Instance).ForDatabase("otra");
        Assert.Equal("otra", s.Entry.Database);
        Assert.Equal(ConnectionMode.ReadOnly, s.Entry.Mode);
        Assert.Equal("", e.Database);
    }
}
