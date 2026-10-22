using System.Globalization;
using System.Text;
using System.Text.Json;
using DataPeach.Core.Db;

namespace DataPeach.Core.History;

/// <summary>
/// A DELETE or TRUNCATE that ran, with the rows it removed (read just before, in the same transaction when the engine
/// allows) so they can be put back. Values are stored as tagged text (<see cref="StoredValue"/>).
/// </summary>
public sealed class DeleteRecord
{
    public string Id { get; set; } = "";
    public DateTimeOffset Time { get; set; }
    public string ConnectionId { get; set; } = "";
    public string ConnectionName { get; set; } = "";
    public DbKind Kind { get; set; }
    /// <summary>"console" or "staging".</summary>
    public string Source { get; set; } = "";
    public string? Schema { get; set; }
    public string Table { get; set; } = "";
    /// <summary>What ran, as the user saw it.</summary>
    public string Sql { get; set; } = "";
    /// <summary>Rows the server said it deleted (-1 when it does not say).</summary>
    public long Affected { get; set; }
    public List<string> Columns { get; set; } = new();
    /// <summary>Type of each column as the table declares it ("" when unknown); PostgreSQL casts with it on restore.</summary>
    public List<string> ColumnTypes { get; set; } = new();
    /// <summary>Columns the server numbers itself (SQL Server, SAP ASE): restoring them needs IDENTITY_INSERT.</summary>
    public List<string> IdentityColumns { get; set; } = new();
    public List<List<string?>> Rows { get; set; } = new();
    /// <summary>The backup holds fewer rows than were deleted (the limit was reached).</summary>
    public bool Partial { get; set; }
    /// <summary>Why there is no backup, when the statement could not be read first.</summary>
    public string? Note { get; set; }
    public DateTimeOffset? Restored { get; set; }

    public string TableDisplay => Schema is null ? Table : Schema + "." + Table;
    public bool HasBackup => Rows.Count > 0;

    /// <summary>
    /// A record of a delete that just ran. <paramref name="known"/> is the table as the structure describes it, for
    /// column types and identity columns; the rows' own columns win (they are what SELECT * gave).
    /// </summary>
    public static DeleteRecord Create(ConnectionEntry e, string source, string? schema, string table, string sql, long affected,
        CapturedRows? backup, DbTable? known, string? note, DateTimeOffset now)
    {
        var r = new DeleteRecord
        {
            Time = now, ConnectionId = e.Id, ConnectionName = e.Name, Kind = e.Kind, Source = source,
            Schema = known?.Schema ?? schema, Table = known?.Name ?? table, Sql = sql, Affected = affected, Note = note,
        };
        if (backup is null) return r;
        r.Columns = backup.Columns.Select(c => c.Name).ToList();
        r.ColumnTypes = r.Columns.Select(c => known?.Column(c)?.DataType ?? "").ToList();
        r.IdentityColumns = known?.Columns.Where(c => c.IsIdentity).Select(c => c.Name).ToList() ?? new();
        r.Rows = backup.Rows.Select(row => row.Select(StoredValue.Encode).ToList()).ToList();
        r.Partial = backup.More;
        return r;
    }
}

/// <summary>
/// Cell values as text with a type tag ("i:12", "s:hola", "d:2024-01-02T03:04:05"), so they survive JSON and come back
/// as the same .NET type for the parameters of the restore. Values of types it does not know (DuckDB lists and
/// structs…) come back as their text.
/// </summary>
public static class StoredValue
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string? Encode(object? v) => v switch
    {
        null or DBNull => null,
        string s => "s:" + s,
        bool b => "b:" + (b ? "1" : "0"),
        sbyte or byte or short or ushort or int or uint or long => "i:" + Convert.ToString(v, Inv),
        ulong u => "u:" + u.ToString(Inv),
        decimal m => "m:" + m.ToString(Inv),
        double d => "f:" + d.ToString("R", Inv),
        float f => "f:" + ((double)f).ToString("R", Inv),
        DateTime d => "d:" + d.ToString("O", Inv),
        DateTimeOffset o => "o:" + o.ToString("O", Inv),
        DateOnly d => "D:" + d.ToString("O", Inv),
        TimeOnly t => "T:" + t.ToString("O", Inv),
        TimeSpan t => "t:" + t.ToString("c", Inv),
        Guid g => "g:" + g.ToString("D"),
        byte[] bytes => "x:" + Convert.ToHexString(bytes),
        _ => "s:" + (Data.ValueText.Full(v) ?? ""),
    };

    public static object? Decode(string? s)
    {
        if (s is null || s.Length < 2 || s[1] != ':') return s;
        var t = s[2..];
        return s[0] switch
        {
            's' => t,
            'b' => t == "1",
            'i' => long.Parse(t, Inv),
            'u' => ulong.Parse(t, Inv),
            'm' => decimal.Parse(t, Inv),
            'f' => double.Parse(t, Inv),
            'd' => DateTime.Parse(t, Inv, DateTimeStyles.RoundtripKind),
            'o' => DateTimeOffset.Parse(t, Inv, DateTimeStyles.RoundtripKind),
            'D' => DateOnly.Parse(t, Inv),
            'T' => TimeOnly.Parse(t, Inv),
            't' => TimeSpan.Parse(t, Inv),
            'g' => Guid.Parse(t),
            'x' => Convert.FromHexString(t),
            _ => t,
        };
    }
}

/// <summary>
/// <c>history/&lt;connection id&gt;/*.json</c> in the settings folder: one file per delete, kept for
/// <see cref="AppState.DeleteHistoryDays"/> days. Like the consoles, it is plain text on this machine.
/// </summary>
public sealed class DeleteHistory
{
    private readonly string _root;

    public DeleteHistory(string dataFolder)
    {
        _root = Path.Combine(dataFolder, "history");
    }

    public string Root => _root;
    private string FolderOf(string connectionId) => Path.Combine(_root, connectionId);

    public void Add(DeleteRecord r)
    {
        if (r.Id.Length == 0) r.Id = r.Time.UtcDateTime.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
        Save(r);
    }

    public void Save(DeleteRecord r)
    {
        var dir = FolderOf(r.ConnectionId);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, r.Id + ".json");
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(r, CoreJson.Default.DeleteRecord));
        File.Move(tmp, file, true);
    }

    /// <summary>A connection's deletes, newest first. Unreadable files are skipped.</summary>
    public List<DeleteRecord> List(string connectionId)
    {
        var dir = FolderOf(connectionId);
        if (!Directory.Exists(dir)) return new();
        var list = new List<DeleteRecord>();
        foreach (var f in Directory.GetFiles(dir, "*.json"))
        {
            try { if (JsonSerializer.Deserialize(File.ReadAllText(f), CoreJson.Default.DeleteRecord) is { } r) list.Add(r); }
            catch { /* a broken file is not a reason to lose the rest */ }
        }
        return list.OrderByDescending(r => r.Time).ToList();
    }

    public int Count(string connectionId)
    {
        var dir = FolderOf(connectionId);
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").Length : 0;
    }

    public void Remove(DeleteRecord r)
    {
        try { File.Delete(Path.Combine(FolderOf(r.ConnectionId), r.Id + ".json")); } catch { /* already gone */ }
    }

    /// <summary>Removes what is older than <paramref name="days"/> (0 keeps everything). Returns how many went.</summary>
    public int Purge(int days, DateTimeOffset now)
    {
        if (days <= 0 || !Directory.Exists(_root)) return 0;
        var limit = now.AddDays(-days);
        int n = 0;
        foreach (var f in Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                var r = JsonSerializer.Deserialize(File.ReadAllText(f), CoreJson.Default.DeleteRecord);
                if (r is null || r.Time >= limit) continue;
                File.Delete(f);
                n++;
            }
            catch { /* left for the next purge */ }
        }
        return n;
    }
}

/// <summary>The INSERTs that put a delete's rows back: as a script to read or edit, and as commands to run. Pure.</summary>
public static class DeleteInverse
{
    private static DbTable TableOf(DeleteRecord r) => new() { Schema = r.Schema, Name = r.Table };

    private static (string On, string Off)? IdentityInsert(DeleteRecord r, Dialect d)
    {
        if (d.Kind is not (DbKind.SqlServer or DbKind.Sybase) || !r.IdentityColumns.Any(c => r.Columns.Contains(c))) return null;
        var t = d.NameOf(TableOf(r));
        return ($"SET IDENTITY_INSERT {t} ON", $"SET IDENTITY_INSERT {t} OFF");
    }

    /// <summary>The restore as a script, with a header saying what it undoes.</summary>
    public static string Script(DeleteRecord r, Dialect d)
    {
        var sb = new StringBuilder();
        sb.Append("-- ").Append(Loc.F("history.script.header", r.TableDisplay, r.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), r.Rows.Count)).Append('\n');
        if (r.Partial) sb.Append("-- ").Append(Loc.T("history.script.partial")).Append('\n');
        // The console splits at ';' and runs everything on one session, so IDENTITY_INSERT holds for the INSERTs.
        var id = IdentityInsert(r, d);
        if (id is { } on) sb.Append(on.On).Append(";\n");
        foreach (var (_, preview) in Statements(r, d)) sb.Append(preview).Append(";\n");
        if (id is { } off) sb.Append(off.Off).Append(";\n");
        return sb.ToString();
    }

    /// <summary>What runs on restore, in one transaction: IDENTITY_INSERT around the INSERTs when needed.</summary>
    public static List<SqlCommandSpec> Commands(DeleteRecord r, Dialect d)
    {
        var list = new List<SqlCommandSpec>();
        var id = IdentityInsert(r, d);
        if (id is { } on) list.Add(new SqlCommandSpec(on.On, Array.Empty<object?>()));
        list.AddRange(Statements(r, d).Select(s => s.Command));
        if (id is { } off) list.Add(new SqlCommandSpec(off.Off, Array.Empty<object?>()));
        return list;
    }

    /// <summary>One INSERT per row: it works on every engine (Oracle had no multi-row VALUES before 23ai).</summary>
    public static IEnumerable<(SqlCommandSpec Command, string Preview)> Statements(DeleteRecord r, Dialect d)
    {
        var table = d.NameOf(TableOf(r));
        var columns = r.Columns.Select((c, i) => new DbColumn(c, i < r.ColumnTypes.Count ? r.ColumnTypes[i] : "", true, false, null)).ToList();
        var names = string.Join(", ", columns.Select(c => d.Name(c.Name)));
        foreach (var row in r.Rows)
        {
            var args = new List<object?>();
            var exec = new StringBuilder("INSERT INTO ").Append(table).Append(" (").Append(names).Append(") VALUES (");
            var view = new StringBuilder(exec.ToString());
            for (int i = 0; i < columns.Count; i++)
            {
                if (i > 0) { exec.Append(", "); view.Append(", "); }
                var v = StoredValue.Decode(i < row.Count ? row[i] : null);
                // PostgreSQL's cast needs a known type; without one the driver guesses from the value.
                exec.Append(d.Param(args.Count, v, columns[i].DataType.Length > 0 ? columns[i] : null));
                args.Add(v);
                view.Append(d.Literal(v));
            }
            exec.Append(')');
            view.Append(')');
            yield return (new SqlCommandSpec(exec.ToString(), args), view.ToString());
        }
    }
}
