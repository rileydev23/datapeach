using System.Data.Common;
using System.Globalization;
using System.Text;

namespace DataPeach.Core.Db;

/// <summary>
/// SAP ASE (Sybase Adaptive Server Enterprise), through AdoNetCore.AseClient. T-SQL like SQL Server, but its catalog is
/// the old one: sysobjects, syscolumns, sysindexes (keys read with index_col) and sysreferences. Owners play the
/// role of schemas.
/// </summary>
public sealed class SybaseDialect : Dialect
{
    public static readonly SybaseDialect Instance = new();
    public override DbKind Kind => DbKind.Sybase;
    protected override string? DefaultSchemaHint => "dbo";

    public override string Quote(string name) => "[" + name.Replace("]", "]]") + "]";
    public override string BoolLiteral(bool b) => b ? "1" : "0";
    public override string BytesLiteral(byte[] b) => "0x" + Convert.ToHexString(b);

    public override string SelectTable(DbTable t, string? where, string? orderBy, int limit)
    {
        var sb = new StringBuilder("SELECT TOP ").Append(limit.ToString(CultureInfo.InvariantCulture)).Append(" * FROM ").Append(NameOf(t));
        if (!string.IsNullOrWhiteSpace(where)) sb.Append(" WHERE ").Append(where.Trim());
        if (!string.IsNullOrWhiteSpace(orderBy)) sb.Append(" ORDER BY ").Append(orderBy.Trim());
        return sb.ToString();
    }

    public override string ConnectionString(DbConnectionStringBuilder b, ConnectionEntry e, string? password)
    {
        SetOptions(b, e.Options);
        b["Data Source"] = e.Host.Length > 0 ? e.Host : "localhost";
        b["Port"] = e.Port > 0 ? e.Port : 5000;
        if (e.Database.Length > 0) b["Database"] = e.Database;
        if (e.User.Length > 0) b["Uid"] = e.User;
        if (password is not null) b["Pwd"] = password;
        // Without a charset the server's default may not hold accents; UTF-8 always does.
        if (!HasOption(e, "Charset")) b["Charset"] = "utf8";
        if (!HasOption(e, "Application Name", "ApplicationName")) b["ApplicationName"] = "DataPeach";
        return b.ConnectionString;
    }

    // ASE has no read-only session: the app alone refuses to write, as with SQL Server.

    public override bool HasDatabases => true;
    public override bool HasUse => true;

    private static readonly string[] SystemDatabases =
        ["master", "model", "sybsystemprocs", "sybsystemdb", "sybsecurity", "sybmgmtdb", "sybpcidb", "dbccdb", "dbccalt"];

    /// <summary>The databases ASE installs for itself, and every tempdb, go in the system folder.</summary>
    public override async Task<(List<DbDatabase> All, string? Current)> LoadDatabasesAsync(DbConnection c, CancellationToken ct)
    {
        var all = (await QueryAsync(c, "SELECT name FROM master..sysdatabases ORDER BY name", ct))
            .Select(r => S(r[0]).Trim())
            .Select(n => new DbDatabase(n, SystemDatabases.Contains(n, StringComparer.OrdinalIgnoreCase) || n.StartsWith("tempdb", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var current = S((await QueryAsync(c, "SELECT db_name()", ct)).FirstOrDefault()?[0]).Trim();
        return (all, current.Length > 0 ? current : null);
    }

    /// <summary>The column type as written in DDL: varchar(20), numeric(10,2)…</summary>
    private const string TypeSql = """
        CASE
            WHEN t.name IN ('varchar','char','varbinary','binary','nvarchar','nchar') THEN t.name + '(' + convert(varchar(10), c.length) + ')'
            WHEN t.name IN ('univarchar','unichar') THEN t.name + '(' + convert(varchar(10), c.length / 2) + ')'
            WHEN t.name IN ('decimal','numeric') THEN t.name + '(' + convert(varchar(10), c.prec) + ',' + convert(varchar(10), c.scale) + ')'
            ELSE t.name
        END
        """;

    // sysquerymetrics is a view ASE puts in every database for its own query metrics.
    public override async Task<SchemaSnapshot> LoadSchemaAsync(DbConnection c, CancellationToken ct)
    {
        var current = S((await QueryAsync(c, "SELECT user_name()", ct)).FirstOrDefault()?[0]);
        var schemas = (await QueryAsync(c, "SELECT DISTINCT user_name(uid) FROM sysobjects WHERE type IN ('U', 'V', 'P', 'SF') ORDER BY 1", ct))
            .Select(r => S(r[0])).ToList();
        if (current.Length > 0 && !schemas.Contains(current)) schemas.Add(current);
        // status: 8 = nullable, 128 = identity. A default is a text in syscomments: "DEFAULT 'x'".
        var rows = await QueryAsync(c, $"""
            SELECT user_name(o.uid), o.name, o.type, c.name, {TypeSql}, c.status & 8,
                   (SELECT max(m.text) FROM syscomments m WHERE m.id = c.cdefault), 0, c.status & 128
            FROM sysobjects o, syscolumns c, systypes t
            WHERE c.id = o.id AND t.usertype = c.usertype AND o.type IN ('U', 'V') AND o.name != 'sysquerymetrics'
            ORDER BY 1, 2, c.colid
            """, ct);
        var keys = new HashSet<(string, string, string)>();
        foreach (var k in await QueryAsync(c, $"SELECT user_name(o.uid), o.name, {KeyColumns("o", "i")} FROM sysindexes i, sysobjects o WHERE i.id = o.id AND o.type = 'U' AND i.status & 2048 = 2048", ct))
            for (int i = 2; i < k.Length && k[i] is not null; i++) keys.Add((S(k[0]), S(k[1]), S(k[i])));

        var tables = new List<DbTable>();
        DbTable? t = null;
        foreach (var r in rows)
        {
            var (owner, name) = (S(r[0]), S(r[1]));
            if (t is null || t.Schema != owner || t.Name != name)
            {
                t = new DbTable { Schema = owner, Name = name, Kind = S(r[2]).Trim() == "V" ? DbObjectKind.View : DbObjectKind.Table };
                tables.Add(t);
            }
            var column = S(r[3]);
            var def = r[6] is null ? null : S(r[6]).Trim();
            if (def is not null && def.StartsWith("DEFAULT", StringComparison.OrdinalIgnoreCase)) def = def[7..].Trim();
            t.Columns.Add(new DbColumn(column, S(r[4]), B(r[5]), keys.Contains((owner, name, column)), def, B(r[8])));
        }
        return new SchemaSnapshot { Schemas = schemas, Tables = tables, DefaultSchema = current.Length > 0 ? current : "dbo" };
    }

    /// <summary>The key columns of an index, one per column (null past the last): index_col for keys 1 to 16.</summary>
    private static string KeyColumns(string o, string i) =>
        string.Join(", ", Enumerable.Range(1, 16).Select(n => $"index_col({o}.name, {i}.indid, {n}, {o}.uid)"));

    public override async Task<TableDetails> LoadDetailsAsync(DbConnection c, DbTable t, CancellationToken ct)
    {
        var d = new TableDetails();
        var args = new[] { ("@s", (object?)t.Schema), ("@t", t.Name) };
        // indid 0 is the table itself, 255 its text/image chain. status 2 = unique.
        foreach (var r in await QueryAsync(c, $"""
                     SELECT i.name, i.status & 2, {KeyColumns("o", "i")}
                     FROM sysindexes i, sysobjects o
                     WHERE i.id = o.id AND o.name = @t AND user_name(o.uid) = @s AND i.indid BETWEEN 1 AND 254
                     ORDER BY 1
                     """, ct, args))
            d.Indexes.Add(new DbIndex(S(r[0]), B(r[1]), "(" + string.Join(", ", r.Skip(2).TakeWhile(x => x is not null).Select(S)) + ")"));
        d.ForeignKeys.AddRange(await LoadForeignKeysAsync(c, t, ct));
        return d;
    }

    /// <summary>sysreferences holds a key in one row, with its columns in fokey1..16 and refkey1..16.</summary>
    public override async Task<List<DbForeignKey>> LoadForeignKeysAsync(DbConnection c, DbTable? t, CancellationToken ct)
    {
        var fo = string.Join(", ", Enumerable.Range(1, 16).Select(n => $"col_name(r.tableid, r.fokey{n})"));
        var re = string.Join(", ", Enumerable.Range(1, 16).Select(n => $"col_name(r.reftabid, r.refkey{n})"));
        var rows = await QueryAsync(c, $"""
            SELECT user_name(o.uid), o.name, object_name(r.constrid), r.keycnt, user_name(rt.uid), rt.name, {fo}, {re}
            FROM sysreferences r, sysobjects o, sysobjects rt
            WHERE o.id = r.tableid AND rt.id = r.reftabid {(t is null ? "" : "AND o.name = @t AND user_name(o.uid) = @s")}
            ORDER BY 1, 2, 3
            """, ct, t is null ? [] : [("@s", t.Schema), ("@t", t.Name)]);
        return DbForeignKey.FromRows(rows.SelectMany(r => Enumerable.Range(0, Convert.ToInt32(r[3], CultureInfo.InvariantCulture))
            .Select(i => new[] { S(r[0]), S(r[1]), S(r[2]), S(r[6 + i]), S(r[4]), S(r[5]), S(r[22 + i]) })));
    }

    // ------------------------------------------------------------------ routines

    public override async Task<List<DbRoutine>> LoadRoutinesAsync(DbConnection c, CancellationToken ct)
    {
        var heads = await QueryAsync(c, "SELECT user_name(uid), name, type, convert(varchar(20), id) FROM sysobjects WHERE type IN ('P', 'SF') ORDER BY 1, 2", ct);
        // status2: 1 = input, 2 = output. A SQL function's return value is its last "parameter", named "Return Type".
        var args = await QueryAsync(c, $"""
            SELECT convert(varchar(20), c.id), c.name, {TypeSql}, c.status2
            FROM syscolumns c, sysobjects o, systypes t
            WHERE c.id = o.id AND t.usertype = c.usertype AND o.type IN ('P', 'SF')
            ORDER BY 1, c.colid
            """, ct);
        var byId = args.GroupBy(a => S(a[0])).ToDictionary(g => g.Key, g => g.ToList());
        return heads.Select(h =>
        {
            var ps = byId.GetValueOrDefault(S(h[3])) ?? new();
            bool proc = S(h[2]).Trim() == "P";
            var ret = ps.FirstOrDefault(p => !S(p[1]).StartsWith('@'));
            return new DbRoutine
            {
                Schema = S(h[0]), Name = S(h[1]), Id = S(h[3]),
                Kind = proc ? DbRoutineKind.Procedure : DbRoutineKind.Function,
                ReturnType = proc ? null : ret is null ? null : S(ret[2]),
                Parameters = ps.Where(p => S(p[1]).StartsWith('@'))
                    .Select(p => new DbParameter(S(p[1]), S(p[2]), (Convert.ToInt32(p[3], CultureInfo.InvariantCulture) & 2) != 0 ? ParamDirection.InOut : ParamDirection.In))
                    .ToList(),
            };
        }).ToList();
    }

    /// <summary>
    /// The text kept in syscomments (split in 255-character rows), after a DROP when it exists: ASE 16 SP02 has no
    /// CREATE OR REPLACE for every object, so this works on any version.
    /// </summary>
    public override async Task<string> LoadRoutineSourceAsync(DbConnection c, DbRoutine r, CancellationToken ct)
    {
        var parts = await QueryAsync(c, "SELECT text FROM syscomments WHERE id = convert(int, @id) ORDER BY colid2, colid", ct, ("@id", r.Id));
        var text = string.Concat(parts.Select(p => S(p[0]))).Trim();
        var what = r.Kind == DbRoutineKind.Procedure ? "PROCEDURE" : "FUNCTION";
        return $"IF OBJECT_ID('{(r.Schema is null ? "" : r.Schema + ".")}{r.Name.Replace("'", "''")}') IS NOT NULL\n    DROP {what} {NameOf(r)}\nGO\n{text}\nGO";
    }

    public override string CallSql(DbRoutine r, IReadOnlyList<string> args)
    {
        // SQL functions must be called with their owner, even dbo's.
        if (r.Kind == DbRoutineKind.Function)
            return "SELECT " + (r.Schema is null ? "" : Name(r.Schema) + ".") + Name(r.Name) + "(" + string.Join(", ", args) + ")";
        return TsqlCall(this, r, args);
    }
}
