using DataPeach.Core;
using DataPeach.Core.Db;
using DataPeach.Core.Staging;
using Microsoft.Data.Sqlite;

namespace DataPeach.Tests;

/// <summary>The whole path against a real SQLite file: structure, streaming, cancel and applying the staging area.</summary>
public sealed class SqliteSessionTests : IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "datapeach-" + Guid.NewGuid().ToString("N") + ".db");
    private DbSession _s = null!;

    public async Task InitializeAsync()
    {
        await using (var c = new SqliteConnection($"Data Source={_file}"))
        {
            await c.OpenAsync();
            var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL, active INTEGER DEFAULT 1);
                CREATE INDEX ix_users_name ON users(name);
                CREATE TABLE orders (id INTEGER PRIMARY KEY, user_id INTEGER REFERENCES users(id), total REAL);
                CREATE VIEW v_users AS SELECT name FROM users;
                CREATE TABLE log (msg TEXT);
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 3000)
                INSERT INTO users (id, name) SELECT i, 'user ' || i FROM n;
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();
        _s = new DbSession(new ConnectionEntry { Kind = DbKind.Sqlite, FilePath = _file }, null, SqliteFactory.Instance);
        await _s.OpenAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _s.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_file); } catch { }
    }

    private sealed class Sink : IResultSink
    {
        public List<ResultColumn> Columns = new();
        public List<object?[]> Rows = new();
        public int Batches;
        public long Affected = -2;
        public bool Truncated;
        public void OnResult(IReadOnlyList<ResultColumn> columns) => Columns = columns.ToList();
        public void OnRows(IReadOnlyList<object?[]> rows) { Rows.AddRange(rows); Batches++; }
        public void OnEnd(long recordsAffected, bool truncated) { Affected = recordsAffected; Truncated |= truncated; }
    }

    [Fact]
    public async Task ReadsTheStructure()
    {
        var schema = await _s.LoadSchemaAsync(CancellationToken.None);
        Assert.Equal(new[] { "log", "orders", "users", "v_users" }, schema.Tables.Select(t => t.Name));
        var users = schema.Find(null, "users")!;
        Assert.True(users.CanEdit);
        Assert.False(schema.Find(null, "log")!.CanEdit);
        Assert.Equal(DbObjectKind.View, schema.Find(null, "v_users")!.Kind);
        var d = await _s.LoadDetailsAsync(users, CancellationToken.None);
        Assert.Contains(d.Indexes, i => i.Name == "ix_users_name" && i.Definition == "(name)");
        var od = await _s.LoadDetailsAsync(schema.Find(null, "orders")!, CancellationToken.None);
        Assert.Equal("(user_id) → users (id)", Assert.Single(od.ForeignKeys).Definition);
    }

    [Fact]
    public async Task ReadsEveryForeignKeyOfTheDatabase()
    {
        // A composite key, and one that names no columns: it points at the referenced table's primary key.
        foreach (var sql in new[]
                 {
                     "CREATE TABLE lines (order_id INTEGER REFERENCES orders, n INTEGER, PRIMARY KEY (order_id, n))",
                     "CREATE TABLE notes (order_id INTEGER, n INTEGER, body TEXT, FOREIGN KEY (order_id, n) REFERENCES lines (order_id, n))",
                 })
            await _s.RunAsync(sql, new Sink(), 10, CancellationToken.None);

        var keys = await _s.LoadForeignKeysAsync(CancellationToken.None);
        Assert.Equal(new[] { "(order_id) → orders (id)", "(order_id, n) → lines (order_id, n)", "(user_id) → users (id)" },
                     keys.Select(k => k.Definition));
        Assert.Equal(new[] { "lines", "notes", "orders" }, keys.Select(k => k.Table));
        Assert.All(keys, k => Assert.Null(k.Schema));
    }

    [Fact]
    public void RecognizesTheFileByItsHeader()
    {
        // ".duckdb" is ignored: the header says SQLite. A file with other content is neither.
        var renamed = Path.ChangeExtension(_file, ".duckdb");
        File.Copy(_file, renamed);
        var other = Path.ChangeExtension(_file, ".txt");
        File.WriteAllText(other, "hola");
        try
        {
            Assert.Equal(DbKind.Sqlite, DbKinds.DetectFile(renamed));
            Assert.Null(DbKinds.DetectFile(other));
        }
        finally { File.Delete(renamed); File.Delete(other); }
    }

    [Fact]
    public async Task StreamsRowsUpToTheLimit()
    {
        var sink = new Sink();
        await _s.RunAsync("SELECT * FROM users ORDER BY id", sink, 2500, CancellationToken.None);
        Assert.Equal(new[] { "id", "name", "active" }, sink.Columns.Select(c => c.Name));
        Assert.Equal(2500, sink.Rows.Count);
        Assert.True(sink.Truncated);
        Assert.Equal("user 1", sink.Rows[0][1]);
    }

    [Fact]
    public async Task ReportsChangedRows()
    {
        var sink = new Sink();
        await _s.RunAsync("UPDATE users SET active = 0 WHERE id <= 10", sink, 100, CancellationToken.None);
        Assert.Equal(10, sink.Affected);
    }

    [Fact]
    public async Task AppliesTheStagingAreaInOneTransaction()
    {
        var schema = await _s.LoadSchemaAsync(CancellationToken.None);
        var users = schema.Find(null, "users")!;
        var staging = new StagingArea();
        staging.Edit(users, new RowKey(new object?[] { 1L }), "name", "user 1", "Ana");
        staging.Delete(users, new RowKey(new object?[] { 2L }));
        var ins = staging.AddInsert(users);
        staging.EditInsert(ins, "id", 9001L);
        staging.EditInsert(ins, "name", "nuevo");
        var cmds = staging.Build(_s.Dialect).Select(x => x.Command).ToList();
        Assert.Equal(3, await _s.ApplyAsync(cmds, CancellationToken.None));

        var sink = new Sink();
        await _s.RunAsync("SELECT id, name FROM users WHERE id IN (1, 2, 9001) ORDER BY id", sink, 10, CancellationToken.None);
        Assert.Equal(new object?[] { 1L, "Ana" }, sink.Rows[0]);
        Assert.Equal(new object?[] { 9001L, "nuevo" }, sink.Rows[1]);
        Assert.Equal(2, sink.Rows.Count);
    }

    [Fact]
    public async Task AStaleRowRollsEverythingBack()
    {
        var schema = await _s.LoadSchemaAsync(CancellationToken.None);
        var users = schema.Find(null, "users")!;
        var staging = new StagingArea();
        staging.Edit(users, new RowKey(new object?[] { 3L }), "name", "user 3", "cambiado");
        staging.Delete(users, new RowKey(new object?[] { 999999L }));
        await Assert.ThrowsAsync<StaleRowException>(() => _s.ApplyAsync(staging.Build(_s.Dialect).Select(x => x.Command).ToList(), CancellationToken.None));
        var sink = new Sink();
        await _s.RunAsync("SELECT name FROM users WHERE id = 3", sink, 10, CancellationToken.None);
        Assert.Equal("user 3", sink.Rows[0][0]);
    }
}
