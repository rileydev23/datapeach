using DataPeach.Core;
using DataPeach.Core.Db;
using DataPeach.Core.History;
using DataPeach.Core.Staging;

namespace DataPeach.Tests;

public class DeletePlanTests
{
    private static SchemaSnapshot Schema()
    {
        var t = new DbTable { Schema = "public", Name = "users" };
        t.Columns.Add(new DbColumn("id", "integer", false, true, null));
        return new SchemaSnapshot { Schemas = { "public" }, Tables = { t }, DefaultSchema = "public" };
    }

    private static DeletePlan? Plan(string sql, DbKind kind = DbKind.Postgres) => DeletePlan.For(sql, kind, Dialect.For(kind), Schema());

    [Theory]
    [InlineData("DELETE FROM users WHERE id = 1", true)]
    [InlineData("delete users where id = 1", true)]
    [InlineData("TRUNCATE TABLE users", true)]
    [InlineData("WITH x AS (SELECT 1) DELETE FROM users", true)]
    [InlineData("SELECT * FROM deleted_rows", false)]
    [InlineData("UPDATE users SET deleted = true", false)]
    public void TellsDeletesApart(string sql, bool expected) => Assert.Equal(expected, DeletePlan.IsDelete(sql, DbKind.Postgres));

    [Fact]
    public void ReadsTheSameRowsFirst()
    {
        var p = Plan("DELETE FROM users WHERE id > 10;")!;
        Assert.True(p.CanBackup);
        Assert.Equal("public", p.Schema);
        Assert.Equal("users", p.Table);
        Assert.Equal("SELECT * FROM users WHERE id > 10", p.SelectSql);
        Assert.Equal("SELECT COUNT(*) FROM users WHERE id > 10", p.CountSql);
    }

    [Fact]
    public void KeepsTheAliasAndQualifiedNames()
    {
        var p = Plan("DELETE FROM public.\"users\" AS u WHERE u.id IN (SELECT id FROM users JOIN x USING (id))")!;
        Assert.Equal("users", p.Table);
        Assert.Equal("SELECT * FROM public.\"users\" AS u WHERE u.id IN (SELECT id FROM users JOIN x USING (id))", p.SelectSql);
    }

    [Fact]
    public void WithoutWhereEveryRowIsBackedUp()
    {
        Assert.Equal("SELECT * FROM users", Plan("DELETE FROM users")!.SelectSql);
        var t = Plan("TRUNCATE users RESTART IDENTITY")!;
        Assert.True(t.IsTruncate);
        Assert.Equal("SELECT * FROM users", t.SelectSql);
    }

    [Fact]
    public void MySqlLimitCountsThroughASubquery()
    {
        var p = Plan("DELETE FROM `users` WHERE id > 1 ORDER BY id LIMIT 5", DbKind.MySql)!;
        Assert.Equal("SELECT * FROM `users` WHERE id > 1 ORDER BY id LIMIT 5", p.SelectSql);
        Assert.Equal("SELECT COUNT(*) FROM (SELECT * FROM `users` WHERE id > 1 ORDER BY id LIMIT 5) dp_rows", p.CountSql);
    }

    [Theory]
    [InlineData("DELETE FROM users USING orders WHERE users.id = orders.user_id", DbKind.Postgres)]
    [InlineData("DELETE FROM users WHERE id = 1 RETURNING *", DbKind.Postgres)]
    [InlineData("DELETE u FROM users u JOIN orders o ON o.user_id = u.id", DbKind.MySql)]
    [InlineData("DELETE TOP (5) FROM users", DbKind.SqlServer)]
    [InlineData("WITH old AS (SELECT id FROM users) DELETE FROM users WHERE id IN (SELECT id FROM old)", DbKind.Postgres)]
    public void ComplexDeletesRunWithoutBackupAndSayWhy(string sql, DbKind kind)
    {
        var p = Plan(sql, kind)!;
        Assert.False(p.CanBackup);
        Assert.False(string.IsNullOrEmpty(p.Unsupported));
    }

    [Fact]
    public void ABracketNameIsUnquoted()
    {
        var p = Plan("DELETE FROM [dbo].[order items] WHERE id = 3", DbKind.SqlServer)!;
        Assert.Equal("dbo", p.Schema);
        Assert.Equal("order items", p.Table);
        Assert.Equal("SELECT * FROM [dbo].[order items] WHERE id = 3", p.SelectSql);
    }
}

public class DeleteHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dp-history-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static DeleteRecord Record(DbKind kind = DbKind.Postgres, DateTimeOffset? at = null)
    {
        var backup = new CapturedRows(
            new[] { new ResultColumn("id", "", typeof(int)), new ResultColumn("name", "", typeof(string)), new ResultColumn("born", "", typeof(DateTime)) },
            new List<object?[]> { new object?[] { 1, "Ana O'Neil", new DateTime(1990, 5, 1) }, new object?[] { 2, null, null } },
            false);
        var t = new DbTable { Schema = kind == DbKind.Postgres ? "public" : "dbo", Name = "users" };
        t.Columns.Add(new DbColumn("id", "integer", false, true, null, IsIdentity: kind is DbKind.SqlServer or DbKind.Sybase));
        t.Columns.Add(new DbColumn("name", "text", true, false, null));
        t.Columns.Add(new DbColumn("born", "date", true, false, null));
        var e = new ConnectionEntry { Id = "c1", Name = "local", Kind = kind };
        return DeleteRecord.Create(e, "console", t.Schema, "users", "DELETE FROM users WHERE id < 3", 2, backup, t, null, at ?? DateTimeOffset.Now);
    }

    [Fact]
    public void ValuesKeepTheirType()
    {
        object?[] values = { null, "x", true, 42, 42L, 3.5m, 2.25, new DateTime(2024, 1, 2, 3, 4, 5), new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.FromHours(-3)),
            new DateOnly(2024, 1, 2), new TimeOnly(10, 30), TimeSpan.FromMinutes(90), Guid.Parse("8f1c2a5e-0000-4000-8000-000000000001"), new byte[] { 0xCA, 0xFE } };
        foreach (var v in values)
        {
            var back = StoredValue.Decode(StoredValue.Encode(v));
            if (v is int i) Assert.Equal((long)i, back);
            else Assert.Equal(v, back);
        }
    }

    [Fact]
    public void SavesListsAndPurgesByAge()
    {
        var h = new DeleteHistory(_dir);
        var now = DateTimeOffset.Now;
        h.Add(Record(at: now.AddDays(-40)));
        h.Add(Record(at: now.AddDays(-1)));
        var list = h.List("c1");
        Assert.Equal(2, list.Count);
        Assert.True(list[0].Time > list[1].Time);
        Assert.Equal("Ana O'Neil", StoredValue.Decode(list[0].Rows[0][1]));

        Assert.Equal(0, h.Purge(0, now));
        Assert.Equal(1, h.Purge(30, now));
        Assert.Single(h.List("c1"));
        Assert.Equal(1, h.Count("c1"));
        h.Remove(h.List("c1")[0]);
        Assert.Empty(h.List("c1"));
    }

    [Fact]
    public void TheInverseInsertsEveryRow()
    {
        var r = Record();
        var script = DeleteInverse.Script(r, PostgresDialect.Instance);
        Assert.StartsWith("-- ", script);
        Assert.Contains("INSERT INTO users (id, name, born) VALUES (1, 'Ana O''Neil', '1990-05-01');", script);
        Assert.Contains("INSERT INTO users (id, name, born) VALUES (2, NULL, NULL);", script);

        var cmds = DeleteInverse.Commands(r, PostgresDialect.Instance);
        Assert.Equal(2, cmds.Count);
        Assert.Equal("INSERT INTO users (id, name, born) VALUES (@p0, CAST(@p1 AS text), @p2)", cmds[0].Sql);
        Assert.Equal(new object?[] { 1L, "Ana O'Neil", new DateTime(1990, 5, 1) }, cmds[0].Parameters);
    }

    [Theory]
    [InlineData(DbKind.SqlServer)]
    [InlineData(DbKind.Sybase)]
    public void IdentityColumnsAreInsertedExplicitly(DbKind kind)
    {
        var r = Record(kind);
        var cmds = DeleteInverse.Commands(r, Dialect.For(kind));
        Assert.Equal("SET IDENTITY_INSERT users ON", cmds[0].Sql);
        Assert.Equal("SET IDENTITY_INSERT users OFF", cmds[^1].Sql);
        Assert.Equal(4, cmds.Count);
        var script = DeleteInverse.Script(r, Dialect.For(kind));
        Assert.Contains("SET IDENTITY_INSERT users ON;", script);
        Assert.Contains("SET IDENTITY_INSERT users OFF;", script);
    }

    [Fact]
    public void AStagedDeleteReadsItsRowWithTheSameKey()
    {
        var t = new DbTable { Schema = "public", Name = "users" };
        t.Columns.Add(new DbColumn("id", "integer", false, true, null));
        var s = new StagingArea();
        s.Delete(t, new RowKey(new object?[] { 7 }));
        var st = Assert.Single(s.Build(PostgresDialect.Instance));
        Assert.Equal("DELETE FROM users WHERE id = @p0", st.Command.Sql);
        Assert.Equal("SELECT * FROM users WHERE id = @p0", st.Capture!.Sql);
        Assert.Equal(new object?[] { 7 }, st.Capture.Parameters);
    }

    [Fact]
    public void WithoutABackupTheRecordSaysWhy()
    {
        var e = new ConnectionEntry { Id = "c1", Name = "local", Kind = DbKind.Postgres };
        var r = DeleteRecord.Create(e, "console", null, "users", "DELETE FROM users USING x", 5, null, null, "usa USING", DateTimeOffset.Now);
        Assert.False(r.HasBackup);
        Assert.Equal("usa USING", r.Note);
        Assert.Empty(DeleteInverse.Commands(r, PostgresDialect.Instance));
    }
}
