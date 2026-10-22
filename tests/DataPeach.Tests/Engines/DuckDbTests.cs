using System.Data.Common;
using DataPeach.Core;

namespace DataPeach.Tests.Engines;

/// <summary>DuckDB is a file, like SQLite: no server, just the downloaded driver.</summary>
public sealed class DuckDbTests : EngineTests
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "datapeach-" + Guid.NewGuid().ToString("N") + ".duckdb");

    protected override ConnectionEntry Entry => new() { Kind = DbKind.DuckDb, FilePath = _file };
    protected override string? Schema => "main";
    protected override string SlowQuery => "SELECT sum(i) FROM range(100000000000) t(i)";

    protected override string Setup => """
        CREATE TABLE users (id INTEGER PRIMARY KEY, name VARCHAR NOT NULL, active INTEGER DEFAULT 1);
        CREATE INDEX ix_users_name ON users(name);
        CREATE TABLE orders (id INTEGER PRIMARY KEY, user_id INTEGER REFERENCES users(id), total DECIMAL(10,2));
        CREATE TABLE log (msg VARCHAR);
        CREATE VIEW v_users AS SELECT name FROM users;
        INSERT INTO users (id, name) SELECT i, 'user ' || i FROM range(1, 3001) t(i);
        """;

    /// <summary>DataPeach never creates a database file by mistake, so the test makes it first.</summary>
    protected override async Task PrepareAsync(DbProviderFactory factory)
    {
        await using var c = factory.CreateConnection()!;
        c.ConnectionString = "Data Source=" + _file;
        await c.OpenAsync();
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        try { File.Delete(_file); } catch { /* still open by the pool */ }
    }

    [Fact]
    public void RecognizesTheFileByItsHeader()
    {
        if (!Enabled) return;
        // While open, as when another program has it.
        Assert.Equal(DbKind.DuckDb, DbKinds.DetectFile(_file));
    }
}
