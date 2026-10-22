using DataPeach.Core;
using DataPeach.Core.Db;
using DataPeach.Core.Drivers;
using DataPeach.Core.Sql;
using DataPeach.Core.Staging;

namespace DataPeach.Tests.Engines;

/// <summary>
/// The same checks as <see cref="SqliteSessionTests"/>, against a real server of each engine with the driver the app
/// downloads. Only with DATAPEACH_ENGINE_TESTS=1 and the servers of tests/docker/compose.yml running.
/// </summary>
public abstract class EngineTests : IAsyncLifetime
{
    public static bool Enabled => Environment.GetEnvironmentVariable("DATAPEACH_ENGINE_TESTS") == "1";

    /// <summary>Drivers are downloaded once per test run and machine, not once per test.</summary>
    private static readonly DriverManager Drivers = new(Path.Combine(Path.GetTempPath(), "datapeach-engine-drivers"));
    private static readonly Dictionary<DbKind, Task> Installs = new();

    protected abstract ConnectionEntry Entry { get; }
    protected virtual string Password => "peach";

    /// <summary>
    /// Builds, from scratch, the same database for every engine: users (3000 rows, index on name), orders (foreign key
    /// to users), log (no primary key) and the view v_users.
    /// </summary>
    protected abstract string Setup { get; }

    /// <summary>Schema the test tables live in, as the structure shows it.</summary>
    protected abstract string? Schema { get; }

    private DbSession _s = null!;

    private static async Task<System.Data.Common.DbProviderFactory> FactoryAsync(DbKind kind)
    {
        Task install;
        lock (Installs)
        {
            if (!Installs.TryGetValue(kind, out install!))
                Installs[kind] = install = Drivers.IsInstalled(kind) && !Drivers.HasUpdate(kind)
                    ? Task.CompletedTask
                    : Drivers.InstallAsync(kind, null, null, CancellationToken.None);
        }
        await install;
        return Drivers.Load(kind);
    }

    protected async Task<DbSession> OpenAsync(ConnectionMode mode = ConnectionMode.Normal)
    {
        var e = Entry.Clone();
        e.Mode = mode;
        var s = new DbSession(e, Password, await FactoryAsync(e.Kind));
        await s.OpenAsync(CancellationToken.None);
        return s;
    }

    public async Task InitializeAsync()
    {
        if (!Enabled) return;
        await PrepareAsync(await FactoryAsync(Entry.Kind));
        _s = await OpenAsync();
        foreach (var st in SqlStatements.Split(Setup, Entry.Kind))
            await _s.RunAsync(st.Text, new Sink(), 10, CancellationToken.None);
    }

    /// <summary>Runs before the first connection: file engines create their database here.</summary>
    protected virtual Task PrepareAsync(System.Data.Common.DbProviderFactory factory) => Task.CompletedTask;

    public virtual async Task DisposeAsync()
    {
        if (_s is not null) await _s.DisposeAsync();
    }

    protected sealed class Sink : IResultSink
    {
        public List<ResultColumn> Columns = new();
        public List<object?[]> Rows = new();
        public long Affected = -2;
        public bool Truncated;
        public void OnResult(IReadOnlyList<ResultColumn> columns) => Columns = columns.ToList();
        public void OnRows(IReadOnlyList<object?[]> rows) => Rows.AddRange(rows);
        public void OnEnd(long recordsAffected, bool truncated) { Affected = recordsAffected; Truncated |= truncated; }
    }

    private async Task<Sink> RunAsync(string sql, int max = 100)
    {
        var sink = new Sink();
        await _s.RunAsync(sql, sink, max, CancellationToken.None);
        return sink;
    }

    [Fact]
    public async Task Connects()
    {
        if (!Enabled) return;
        Assert.False(string.IsNullOrEmpty(_s.ServerVersion));
    }

    [Fact]
    public async Task ReadsTheStructure()
    {
        if (!Enabled) return;
        var schema = await _s.LoadSchemaAsync(CancellationToken.None);
        // Oracle keeps unquoted names in upper case.
        var mine = schema.Tables.Where(t => string.Equals(t.Schema, Schema, StringComparison.OrdinalIgnoreCase)).Select(t => t.Name.ToLowerInvariant()).ToList();
        Assert.Equal(new[] { "log", "orders", "users", "v_users" }, mine.Where(n => n is "log" or "orders" or "users" or "v_users"));
        var users = schema.Find(Schema, "users")!;
        Assert.True(users.CanEdit);
        Assert.Equal(new[] { "id", "name", "active" }, users.Columns.Select(c => c.Name.ToLowerInvariant()));
        Assert.False(users.Column("name")!.Nullable);
        Assert.False(schema.Find(Schema, "log")!.CanEdit);
        Assert.Equal(DbObjectKind.View, schema.Find(Schema, "v_users")!.Kind);
        var d = await _s.LoadDetailsAsync(users, CancellationToken.None);
        Assert.Contains(d.Indexes, i => i.Name.Equals("ix_users_name", StringComparison.OrdinalIgnoreCase) && i.Definition.Contains("name", StringComparison.OrdinalIgnoreCase));
        var od = await _s.LoadDetailsAsync(schema.Find(Schema, "orders")!, CancellationToken.None);
        Assert.Contains("users", Assert.Single(od.ForeignKeys).Definition, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StreamsRowsUpToTheLimit()
    {
        if (!Enabled) return;
        var sink = await RunAsync("SELECT * FROM users ORDER BY id", 2500);
        Assert.Equal(new[] { "id", "name", "active" }, sink.Columns.Select(c => c.Name.ToLowerInvariant()));
        Assert.Equal(2500, sink.Rows.Count);
        Assert.True(sink.Truncated);
        Assert.Equal("user 1", sink.Rows[0][1]);
    }

    [Fact]
    public async Task ReportsChangedRows()
    {
        if (!Enabled) return;
        var sink = await RunAsync("UPDATE users SET active = 0 WHERE id <= 10");
        Assert.Equal(10, sink.Affected);
    }

    [Fact]
    public async Task CancelsALongQuery()
    {
        if (!Enabled) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(() => _s.RunAsync(SlowQuery, new Sink(), 10, cts.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"cancel took {clock.Elapsed}");
        // The connection is still usable afterwards.
        Assert.Single((await RunAsync("SELECT 1")).Rows);
    }

    /// <summary>A statement that takes about 10 seconds.</summary>
    protected abstract string SlowQuery { get; }

    [Fact]
    public async Task AppliesTheStagingAreaInOneTransaction()
    {
        if (!Enabled) return;
        var schema = await _s.LoadSchemaAsync(CancellationToken.None);
        var users = schema.Find(Schema, "users")!;
        var staging = new StagingArea();
        staging.Edit(users, new RowKey(new object?[] { 1 }), users.Column("name")!.Name, "user 1", "Ana");
        staging.Delete(users, new RowKey(new object?[] { 2 }));
        var ins = staging.AddInsert(users);
        staging.EditInsert(ins, users.Column("id")!.Name, 9001);
        staging.EditInsert(ins, users.Column("name")!.Name, "nuevo");
        var cmds = staging.Build(_s.Dialect).Select(x => x.Command).ToList();
        Assert.Equal(3, await _s.ApplyAsync(cmds, CancellationToken.None));

        var sink = await RunAsync("SELECT id, name FROM users WHERE id IN (1, 2, 9001) ORDER BY id");
        Assert.Equal(2, sink.Rows.Count);
        Assert.Equal("Ana", sink.Rows[0][1]);
        Assert.Equal("nuevo", sink.Rows[1][1]);
    }

    [Fact]
    public async Task AStaleRowRollsEverythingBack()
    {
        if (!Enabled) return;
        var schema = await _s.LoadSchemaAsync(CancellationToken.None);
        var users = schema.Find(Schema, "users")!;
        var staging = new StagingArea();
        staging.Edit(users, new RowKey(new object?[] { 3 }), users.Column("name")!.Name, "user 3", "cambiado");
        staging.Delete(users, new RowKey(new object?[] { 999999 }));
        await Assert.ThrowsAsync<StaleRowException>(() => _s.ApplyAsync(staging.Build(_s.Dialect).Select(x => x.Command).ToList(), CancellationToken.None));
        Assert.Equal("user 3", (await RunAsync("SELECT name FROM users WHERE id = 3")).Rows[0][0]);
    }

    [Fact]
    public async Task AReadOnlyConnectionRefusesWrites()
    {
        if (!Enabled) return;
        // Without a read-only session in the engine (SQL Server), only the app stands between the user and a write.
        Assert.True(SqlStatements.IsWrite("UPDATE users SET name = 'x' WHERE id = 4", Entry.Kind));
        if (_s.Dialect.ReadOnlySessionSql is null) return;
        await using var ro = await OpenAsync(ConnectionMode.ReadOnly);
        await Assert.ThrowsAnyAsync<Exception>(() => ro.RunAsync("UPDATE users SET name = 'x' WHERE id = 4", new Sink(), 10, CancellationToken.None));
        Assert.Equal("user 4", (await RunAsync("SELECT name FROM users WHERE id = 4")).Rows[0][0]);
    }
}
