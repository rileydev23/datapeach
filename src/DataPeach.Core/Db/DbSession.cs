using System.Data.Common;
using System.Diagnostics;
using DataPeach.Core.Sql;

namespace DataPeach.Core.Db;

public sealed record ResultColumn(string Name, string TypeName, Type ClrType);

/// <summary>Receives a running query's output as it arrives. Called on a background thread.</summary>
public interface IResultSink
{
    /// <summary>A new result set begins (a statement can return several).</summary>
    void OnResult(IReadOnlyList<ResultColumn> columns);
    void OnRows(IReadOnlyList<object?[]> rows);
    /// <summary>End of a statement: rows changed (-1 when it does not apply) and whether the row limit cut the output.</summary>
    void OnEnd(long recordsAffected, bool truncated);
}

/// <summary>A statement with its parameter values (named @p0, @p1…).</summary>
public sealed record SqlCommandSpec(string Sql, IReadOnlyList<object?> Parameters);

/// <summary>
/// One open connection tab. Consoles share the main connection (so <c>SET</c>, temporary tables and session
/// variables behave like in a terminal); structure is read on separate short connections so it never waits for a
/// long query.
/// </summary>
public sealed class DbSession : IAsyncDisposable
{
    private readonly DbProviderFactory _factory;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DbConnection? _main;

    public ConnectionEntry Entry { get; }
    public Dialect Dialect { get; }
    public string ServerVersion { get; private set; } = "";
    public bool ReadOnly => Entry.Mode == ConnectionMode.ReadOnly;

    public DbSession(ConnectionEntry entry, string? password, DbProviderFactory factory)
    {
        Entry = entry;
        Dialect = Dialect.For(entry.Kind);
        _factory = factory;
        var b = factory.CreateConnectionStringBuilder() ?? new DbConnectionStringBuilder();
        _connectionString = Dialect.ConnectionString(b, entry, password);
    }

    private async Task<DbConnection> NewConnectionAsync(CancellationToken ct)
    {
        if (Entry.IsFile && !File.Exists(Entry.FilePath)) throw new FileNotFoundException(Loc.F("db.fileMissing", Entry.FilePath));
        var c = _factory.CreateConnection() ?? throw new InvalidOperationException("CreateConnection");
        c.ConnectionString = _connectionString;
        try
        {
            await c.OpenAsync(ct);
            if (ReadOnly && Dialect.ReadOnlySessionSql is { } sql)
            {
                await using var cmd = c.CreateCommand();
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        catch
        {
            await c.DisposeAsync();
            throw;
        }
        return c;
    }

    public async Task OpenAsync(CancellationToken ct)
    {
        _main = await NewConnectionAsync(ct);
        ServerVersion = _main.ServerVersion;
    }

    /// <summary>Opens and closes a connection: "Probar conexión" in the connection dialog.</summary>
    public async Task<string> TestAsync(CancellationToken ct)
    {
        await using var c = await NewConnectionAsync(ct);
        return c.ServerVersion;
    }

    public async Task<SchemaSnapshot> LoadSchemaAsync(CancellationToken ct)
    {
        await using var c = await NewConnectionAsync(ct);
        var schema = await Dialect.LoadSchemaAsync(c, ct);
        // Routines are a bonus: a server that refuses to list them (old version, no rights) still shows its tables.
        try { schema.Routines.AddRange(await Dialect.LoadRoutinesAsync(c, ct)); }
        catch (Exception) when (!ct.IsCancellationRequested) { }
        return schema;
    }

    public async Task<string> LoadRoutineSourceAsync(DbRoutine r, CancellationToken ct)
    {
        await using var c = await NewConnectionAsync(ct);
        return await Dialect.LoadRoutineSourceAsync(c, r, ct);
    }

    public async Task<TableDetails> LoadDetailsAsync(DbTable t, CancellationToken ct)
    {
        await using var c = await NewConnectionAsync(ct);
        return await Dialect.LoadDetailsAsync(c, t, ct);
    }

    /// <summary>
    /// Runs one statement on the main connection and streams its output to <paramref name="sink"/> in batches: the
    /// first rows go out at once so they show up immediately, then about every 100 ms. Reading stops after
    /// <paramref name="maxRows"/> rows per result set. Cancelling the token cancels the statement on the server.
    /// </summary>
    public async Task RunAsync(string sql, IResultSink sink, int maxRows, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _main ??= await NewConnectionAsync(ct);
            await using var cmd = _main.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 0;
            await using var reg = ct.Register(() => { try { cmd.Cancel(); } catch { /* already finished */ } });
            if (Dialect.CountsAsResult && SqlStatements.IsWrite(sql, Entry.Kind) && !Returns(sql))
            {
                // DuckDB answers an UPDATE with a one-row "Count" result instead of RecordsAffected.
                var n = await cmd.ExecuteNonQueryAsync(ct);
                ct.ThrowIfCancellationRequested();
                sink.OnEnd(n, false);
                return;
            }
            await using var r = await cmd.ExecuteReaderAsync(ct);
            bool any = false;
            do
            {
                if (r.FieldCount == 0) continue;
                any = true;
                var cols = new ResultColumn[r.FieldCount];
                for (int i = 0; i < cols.Length; i++)
                {
                    Type clr;
                    try { clr = r.GetFieldType(i); } catch { clr = typeof(object); }
                    string type;
                    try { type = r.GetDataTypeName(i); } catch { type = ""; }
                    cols[i] = new ResultColumn(r.GetName(i), type, clr);
                }
                sink.OnResult(cols);
                var batch = new List<object?[]>(256);
                var clock = Stopwatch.StartNew();
                bool first = true, truncated = false;
                int count = 0;
                while (await r.ReadAsync(ct))
                {
                    if (count >= maxRows) { truncated = true; break; }
                    batch.Add(ReadRow(r, cols.Length));
                    count++;
                    if ((first && batch.Count >= 200) || clock.ElapsedMilliseconds >= (first ? 30 : 100) || batch.Count >= 10_000)
                    {
                        sink.OnRows(batch);
                        batch = new List<object?[]>(1024);
                        clock.Restart();
                        first = false;
                    }
                }
                if (batch.Count > 0) sink.OnRows(batch);
                sink.OnEnd(-1, truncated);
                if (truncated) break; // the rest of the output is not wanted; disposing the reader stops it
            } while (await r.NextResultAsync(ct));
            // Some servers end a cancelled statement without an error (MySQL's KILL QUERY makes SLEEP() return 1):
            // what arrived is not the real result.
            ct.ThrowIfCancellationRequested();
            // INSERT, UPDATE, DDL…: no rows, just how many were changed.
            if (!any) sink.OnEnd(r.RecordsAffected, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A write that still returns rows (INSERT … RETURNING).</summary>
    private bool Returns(string sql) =>
        SqlTokenizer.Tokenize(sql, Entry.Kind).Any(t => t.Kind is SqlTokenKind.Keyword or SqlTokenKind.Identifier
                                                        && t.Text(sql).Equals("RETURNING", StringComparison.OrdinalIgnoreCase));

    private static object?[] ReadRow(DbDataReader r, int n)
    {
        var row = new object?[n];
        try
        {
            r.GetValues(row!);
            for (int i = 0; i < n; i++) if (row[i] is DBNull) row[i] = null;
        }
        catch
        {
            // A value the driver cannot map (a PostgreSQL infinity date…): read the row one field at a time.
            for (int i = 0; i < n; i++)
            {
                try { row[i] = r.IsDBNull(i) ? null : r.GetValue(i); }
                catch
                {
                    try { row[i] = r.GetString(i); }
                    catch (Exception ex) { row[i] = "⚠ " + ex.Message; }
                }
            }
        }
        return row;
    }

    private DbCommand Command(SqlCommandSpec spec, DbTransaction? tx)
    {
        var cmd = _main!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = spec.Sql;
        for (int i = 0; i < spec.Parameters.Count; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = Dialect.ParamName(i);
            p.Value = spec.Parameters[i] ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    /// <summary>Reads at most <paramref name="max"/> rows of a SELECT; the flag says whether there were more.</summary>
    private static async Task<CapturedRows> ReadAllAsync(DbCommand cmd, int max, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var cols = new ResultColumn[r.FieldCount];
        for (int i = 0; i < cols.Length; i++)
        {
            Type clr;
            try { clr = r.GetFieldType(i); } catch { clr = typeof(object); }
            cols[i] = new ResultColumn(r.GetName(i), "", clr);
        }
        var rows = new List<object?[]>();
        bool more = false;
        while (await r.ReadAsync(ct))
        {
            if (rows.Count >= max) { more = true; break; }
            rows.Add(ReadRow(r, cols.Length));
        }
        return new CapturedRows(cols, rows, more);
    }

    /// <summary>
    /// Runs a console DELETE or TRUNCATE after reading the rows it removes with <paramref name="backupSql"/> (at most
    /// <paramref name="maxRows"/>). A DELETE runs in the same transaction as its read, so the backup is exactly what
    /// went; a TRUNCATE commits on its own in several engines, so it just runs right after. Without a backup query
    /// it only runs the statement. Reports the rows changed to <paramref name="sink"/>.
    /// </summary>
    public async Task<CapturedRows?> DeleteWithBackupAsync(string sql, string? backupSql, bool transactional, int maxRows, IResultSink sink, CancellationToken ct)
    {
        if (ReadOnly) throw new InvalidOperationException(Loc.T("db.readOnly"));
        await _gate.WaitAsync(ct);
        try
        {
            _main ??= await NewConnectionAsync(ct);
            DbTransaction? tx = null;
            // A transaction the user opened by hand in the console makes the driver refuse a new one: then no wrapper.
            if (transactional && backupSql is not null)
                try { tx = await _main.BeginTransactionAsync(ct); } catch { tx = null; }
            try
            {
                CapturedRows? backup = null;
                if (backupSql is not null)
                {
                    await using var read = Command(new SqlCommandSpec(backupSql, Array.Empty<object?>()), tx);
                    read.CommandTimeout = 0;
                    backup = await ReadAllAsync(read, maxRows, ct);
                }
                await using var cmd = Command(new SqlCommandSpec(sql, Array.Empty<object?>()), tx);
                cmd.CommandTimeout = 0;
                await using var reg = ct.Register(() => { try { cmd.Cancel(); } catch { /* already finished */ } });
                var n = await cmd.ExecuteNonQueryAsync(ct);
                if (tx is not null) await tx.CommitAsync(ct);
                sink.OnEnd(n, false);
                return backup;
            }
            catch
            {
                if (tx is not null) try { await tx.RollbackAsync(CancellationToken.None); } catch { /* connection gone */ }
                throw;
            }
            finally
            {
                if (tx is not null) await tx.DisposeAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs statements in one transaction on the main connection: all of them, or none if one fails. A statement
    /// with a capture (a staged DELETE) first reads its rows with it; they are returned by statement index.
    /// </summary>
    public async Task<long> ApplyAsync(IReadOnlyList<SqlCommandSpec> commands, CancellationToken ct,
        IReadOnlyList<SqlCommandSpec?>? captures = null, Dictionary<int, CapturedRows>? captured = null)
    {
        if (ReadOnly) throw new InvalidOperationException(Loc.T("db.readOnly"));
        await _gate.WaitAsync(ct);
        try
        {
            _main ??= await NewConnectionAsync(ct);
            await using var tx = await _main.BeginTransactionAsync(ct);
            long total = 0;
            try
            {
                for (int k = 0; k < commands.Count; k++)
                {
                    var spec = commands[k];
                    if (captures is not null && k < captures.Count && captures[k] is { } read && captured is not null)
                    {
                        await using var sel = Command(read, tx);
                        captured[k] = await ReadAllAsync(sel, int.MaxValue, ct);
                    }
                    await using var cmd = Command(spec, tx);
                    var n = await cmd.ExecuteNonQueryAsync(ct);
                    // An UPDATE or DELETE that touched nothing means the row changed or vanished meanwhile.
                    if (n == 0) throw new StaleRowException(spec.Sql);
                    total += Math.Max(n, 0);
                }
                await tx.CommitAsync(ct);
            }
            catch
            {
                try { await tx.RollbackAsync(CancellationToken.None); } catch { /* connection gone */ }
                throw;
            }
            return total;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_main is not null)
        {
            try { await _main.DisposeAsync(); } catch { /* closing anyway */ }
            _main = null;
        }
    }
}

/// <summary>Rows read before deleting them; <see cref="More"/> when the limit left some out.</summary>
public sealed record CapturedRows(IReadOnlyList<ResultColumn> Columns, List<object?[]> Rows, bool More);

public sealed class StaleRowException(string sql) : Exception(Loc.F("db.stale", sql))
{
}
