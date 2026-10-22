using System.Data.Common;
using System.Globalization;
using System.Text;
using DataPeach.Core.Data;
using DataPeach.Core.Sql;

namespace DataPeach.Core.Db;

/// <summary>
/// What DataPeach itself knows about an engine: how to name things, read the structure and write literals. The
/// driver (downloaded) does the talking; this is the only engine-specific code of the app.
/// </summary>
public abstract partial class Dialect
{
    public abstract DbKind Kind { get; }

    public static Dialect For(DbKind kind) => kind switch
    {
        DbKind.Postgres => PostgresDialect.Instance,
        DbKind.MySql => MySqlDialect.Instance,
        DbKind.SqlServer => SqlServerDialect.Instance,
        DbKind.Oracle => OracleDialect.Instance,
        DbKind.DuckDb => DuckDbDialect.Instance,
        DbKind.Sybase => SybaseDialect.Instance,
        _ => SqliteDialect.Instance,
    };

    // ------------------------------------------------------------------ names and literals

    public virtual string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    public string Qualified(DbTable t) => t.Schema is null ? Quote(t.Name) : Quote(t.Schema) + "." + Quote(t.Name);

    /// <summary>
    /// Plain identifiers stay unquoted in generated SQL, so it reads naturally. PostgreSQL folds unquoted names to
    /// lower case and Oracle to upper case, so there only names already in that case are plain.
    /// </summary>
    public string Name(string name)
    {
        bool Letter(char c) => Kind switch
        {
            DbKind.Postgres => char.IsAsciiLetterLower(c),
            DbKind.Oracle => char.IsAsciiLetterUpper(c),
            _ => char.IsAsciiLetter(c),
        };
        bool plain = name.Length > 0 && (Letter(name[0]) || name[0] == '_')
                     && name.All(c => Letter(c) || char.IsAsciiDigit(c) || c == '_')
                     && !SqlKeywords.Reserved.Contains(name);
        return plain ? name : Quote(name);
    }

    public string NameOf(DbTable t) => t.Schema is null || t.Schema == DefaultSchemaHint ? Name(t.Name) : Name(t.Schema) + "." + Name(t.Name);

    /// <summary>Schema that is left implicit in generated SQL ("public" in PostgreSQL).</summary>
    protected virtual string? DefaultSchemaHint => null;

    public virtual string BoolLiteral(bool b) => b ? "TRUE" : "FALSE";
    public virtual string BytesLiteral(byte[] b) => "X'" + Convert.ToHexString(b) + "'";

    /// <summary>A value written as SQL, for the staging preview and "copy as INSERT". Execution uses parameters.</summary>
    public string Literal(object? v) => v switch
    {
        null or DBNull => "NULL",
        bool b => BoolLiteral(b),
        byte[] bytes => BytesLiteral(bytes),
        sbyte or byte or short or ushort or int or uint or long or ulong or decimal => Convert.ToString(v, CultureInfo.InvariantCulture)!,
        double d => double.IsFinite(d) ? d.ToString("R", CultureInfo.InvariantCulture) : Str(d.ToString(CultureInfo.InvariantCulture)),
        float f => float.IsFinite(f) ? f.ToString("R", CultureInfo.InvariantCulture) : Str(f.ToString(CultureInfo.InvariantCulture)),
        _ => Str(ValueText.Full(v) ?? ""),
    };

    public virtual string Str(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>
    /// Placeholder for a parameter going into <paramref name="column"/>. PostgreSQL gets text casts so the driver never
    /// has to guess the column type of an edited value (enums, json, uuid…).
    /// </summary>
    public virtual string Param(int index, object? value, DbColumn? column) => "@p" + index;

    /// <summary>Name of parameter <paramref name="index"/> as the driver wants it in DbParameter.ParameterName.</summary>
    public virtual string ParamName(int index) => "@p" + index;

    // ------------------------------------------------------------------ queries

    public virtual string SelectTable(DbTable t, string? where, string? orderBy, int limit)
    {
        var sb = new StringBuilder("SELECT * FROM ").Append(NameOf(t));
        if (!string.IsNullOrWhiteSpace(where)) sb.Append(" WHERE ").Append(where.Trim());
        if (!string.IsNullOrWhiteSpace(orderBy)) sb.Append(" ORDER BY ").Append(orderBy.Trim());
        sb.Append(" LIMIT ").Append(limit.ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    public string CountTable(DbTable t, string? where) =>
        "SELECT COUNT(*) FROM " + NameOf(t) + (string.IsNullOrWhiteSpace(where) ? "" : " WHERE " + where.Trim());

    /// <summary>Connection string for <paramref name="e"/>; the user's extra options go first so our fields win.</summary>
    public abstract string ConnectionString(DbConnectionStringBuilder b, ConnectionEntry e, string? password);

    /// <summary>Whether a write answers with a result holding its row count instead of RecordsAffected (DuckDB).</summary>
    public virtual bool CountsAsResult => false;

    /// <summary>Statement run right after opening a read-only connection, if the engine has one.</summary>
    public virtual string? ReadOnlySessionSql => null;

    public abstract Task<SchemaSnapshot> LoadSchemaAsync(DbConnection c, CancellationToken ct);
    public abstract Task<TableDetails> LoadDetailsAsync(DbConnection c, DbTable t, CancellationToken ct);

    /// <summary>The foreign keys of <paramref name="t"/>, or of every table <see cref="LoadSchemaAsync"/> reads when null.</summary>
    public abstract Task<List<DbForeignKey>> LoadForeignKeysAsync(DbConnection c, DbTable? t, CancellationToken ct);

    /// <summary>Runs a query of one row per column pair (see <see cref="DbForeignKey.FromRows"/>) and builds the keys.</summary>
    protected static async Task<List<DbForeignKey>> ForeignKeysAsync(DbConnection c, string sql, CancellationToken ct, params (string Name, object? Value)[] args) =>
        DbForeignKey.FromRows((await QueryAsync(c, sql, ct, args)).Select(r => r.Select(v => v is null ? null : S(v)).ToArray()));

    // ------------------------------------------------------------------ databases

    /// <summary>
    /// Whether one server holds several databases that the tree shows as folders (SQL Server, PostgreSQL, MySQL,
    /// SAP ASE). Each one is read and queried through a connection of its own, made by changing the database field.
    /// </summary>
    public virtual bool HasDatabases => false;

    /// <summary>The server's databases and the one <paramref name="c"/> is in (null when none, MySQL without one).</summary>
    public virtual Task<(List<DbDatabase> All, string? Current)> LoadDatabasesAsync(DbConnection c, CancellationToken ct) =>
        Task.FromResult((new List<DbDatabase>(), (string?)null));

    /// <summary>Whether the engine switches databases with <c>USE name</c> on the same connection.</summary>
    public virtual bool HasUse => false;

    /// <summary>The database a <c>USE name</c> statement switches to; null for any other statement.</summary>
    public string? UsedDatabase(string sql)
    {
        if (!HasUse) return null;
        var tokens = SqlTokenizer.Tokenize(sql, Kind).Where(t => t.Kind is not (SqlTokenKind.Whitespace or SqlTokenKind.Comment)).ToList();
        if (tokens.Count is < 2 or > 3 || !tokens[0].Text(sql).Equals("USE", StringComparison.OrdinalIgnoreCase)) return null;
        if (tokens.Count == 3 && tokens[2].Text(sql) != ";") return null;
        var name = tokens[1];
        if (name.Kind == SqlTokenKind.QuotedIdentifier)
        {
            var q = name.Text(sql);
            return q[1..^1].Replace(q[^1].ToString() + q[^1], q[^1].ToString());
        }
        return name.Kind is SqlTokenKind.Identifier or SqlTokenKind.Keyword ? name.Text(sql) : null;
    }

    public string UseSql(string database) => "USE " + Quote(database);

    // ------------------------------------------------------------------ helpers

    protected static async Task<List<object?[]>> QueryAsync(DbConnection c, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        var rows = new List<object?[]>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var row = new object?[r.FieldCount];
            for (int i = 0; i < row.Length; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    protected static string S(object? v) => v switch { null => "", string s => s, byte[] b => Encoding.UTF8.GetString(b), _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" };
    protected static bool B(object? v) => v switch { bool b => b, null => false, string s => s is "1" or "t" or "true" or "YES" or "yes", _ => Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0 };

    protected static void SetOptions(DbConnectionStringBuilder b, string options)
    {
        if (!string.IsNullOrWhiteSpace(options)) b.ConnectionString = options;
    }

    /// <summary>
    /// Whether the user's extra options set one of <paramref name="keys"/>. A driver's own builder cannot tell: its
    /// ContainsKey is true for every keyword it knows.
    /// </summary>
    protected static bool HasOption(ConnectionEntry e, params string[] keys)
    {
        if (string.IsNullOrWhiteSpace(e.Options)) return false;
        try
        {
            var set = new DbConnectionStringBuilder { ConnectionString = e.Options };
            return keys.Any(set.ContainsKey);
        }
        catch { return false; } // the driver reports a malformed string better
    }
}

public sealed partial class PostgresDialect : Dialect
{
    public static readonly PostgresDialect Instance = new();
    public override DbKind Kind => DbKind.Postgres;
    protected override string? DefaultSchemaHint => "public";

    public override string BytesLiteral(byte[] b) => "'\\x" + Convert.ToHexString(b) + "'::bytea";

    public override string Param(int index, object? value, DbColumn? column) =>
        value is string && column is not null ? $"CAST(@p{index} AS {column.DataType})" : "@p" + index;

    public override string ConnectionString(DbConnectionStringBuilder b, ConnectionEntry e, string? password)
    {
        SetOptions(b, e.Options);
        b["Host"] = e.Host.Length > 0 ? e.Host : "localhost";
        b["Port"] = e.Port > 0 ? e.Port : 5432;
        if (e.Database.Length > 0) b["Database"] = e.Database;
        if (e.User.Length > 0) b["Username"] = e.User;
        if (password is not null) b["Password"] = password;
        if (!HasOption(e, "Application Name", "ApplicationName", "App")) b["Application Name"] = "DataPeach";
        return b.ConnectionString;
    }

    public override string ReadOnlySessionSql => "SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY";

    public override bool HasDatabases => true;

    /// <summary>Templates (template1) and CockroachDB's "system" are the server's; template0 refuses connections.</summary>
    public override async Task<(List<DbDatabase> All, string? Current)> LoadDatabasesAsync(DbConnection c, CancellationToken ct)
    {
        var cockroach = S((await QueryAsync(c, "SELECT version()", ct)).FirstOrDefault()?[0]).Contains("CockroachDB", StringComparison.Ordinal);
        var all = (await QueryAsync(c, "SELECT datname, datistemplate FROM pg_database WHERE datallowconn ORDER BY 1", ct))
            .Select(r => new DbDatabase(S(r[0]), B(r[1]) || (cockroach && S(r[0]) == "system"))).ToList();
        var current = (await QueryAsync(c, "SELECT current_database()", ct)).FirstOrDefault()?[0] as string;
        return (all, current);
    }

    // crdb_internal and pg_extension: CockroachDB's own schemas.
    private const string Filter = "n.nspname NOT IN ('pg_catalog','information_schema','crdb_internal','pg_extension') AND n.nspname NOT LIKE 'pg\\_toast%' AND n.nspname NOT LIKE 'pg\\_temp%'";

    public override async Task<SchemaSnapshot> LoadSchemaAsync(DbConnection c, CancellationToken ct)
    {
        var schemas = (await QueryAsync(c, $"SELECT n.nspname FROM pg_namespace n WHERE {Filter} ORDER BY 1", ct)).Select(r => S(r[0])).ToList();
        var current = (await QueryAsync(c, "SELECT current_schema()", ct)).FirstOrDefault()?[0] as string;
        var rows = await QueryAsync(c, $"""
            SELECT n.nspname, c.relname, c.relkind::text, a.attname, format_type(a.atttypid, a.atttypmod), NOT a.attnotnull,
                   pg_get_expr(d.adbin, d.adrelid),
                   EXISTS (SELECT 1 FROM pg_index i WHERE i.indrelid = c.oid AND i.indisprimary AND a.attnum = ANY (i.indkey))
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
            LEFT JOIN pg_attrdef d ON d.adrelid = c.oid AND d.adnum = a.attnum
            WHERE c.relkind IN ('r','v','m','p','f') AND c.relispartition IS NOT TRUE AND {Filter}
            ORDER BY n.nspname, c.relname, a.attnum
            """, ct);
        // CockroachDB gives every table without a primary key a hidden rowid key that SELECT * leaves out: without
        // dropping it, such a table would look editable by a key the grid never sees.
        var version = S((await QueryAsync(c, "SELECT version()", ct)).FirstOrDefault()?[0]);
        if (version.Contains("CockroachDB", StringComparison.Ordinal))
        {
            var hidden = (await QueryAsync(c, "SELECT table_schema, table_name, column_name FROM information_schema.columns WHERE is_hidden = 'YES'", ct))
                .Select(r => (S(r[0]), S(r[1]), S(r[2]))).ToHashSet();
            rows.RemoveAll(r => hidden.Contains((S(r[0]), S(r[1]), S(r[3]))));
        }
        return Build(schemas, current, rows, kind: k => k is "v" or "m" ? DbObjectKind.View : DbObjectKind.Table);
    }

    public override async Task<TableDetails> LoadDetailsAsync(DbConnection c, DbTable t, CancellationToken ct)
    {
        var d = new TableDetails();
        var name = Qualified(t);
        foreach (var r in await QueryAsync(c, """
                     SELECT i.relname, ix.indisunique, pg_get_indexdef(ix.indexrelid)
                     FROM pg_index ix JOIN pg_class i ON i.oid = ix.indexrelid
                     WHERE ix.indrelid = CAST(@t AS regclass) ORDER BY 1
                     """, ct, ("@t", name)))
        {
            var def = S(r[2]);
            var using_ = def.IndexOf(" USING ", StringComparison.Ordinal);
            d.Indexes.Add(new DbIndex(S(r[0]), B(r[1]), using_ >= 0 ? def[(using_ + 7)..] : def));
        }
        d.ForeignKeys.AddRange(await LoadForeignKeysAsync(c, t, ct));
        return d;
    }

    /// <summary>conkey and confkey list the column numbers of both sides in the same order.</summary>
    public override Task<List<DbForeignKey>> LoadForeignKeysAsync(DbConnection c, DbTable? t, CancellationToken ct) =>
        ForeignKeysAsync(c, $"""
            SELECT n.nspname, cl.relname, k.conname, a.attname, rn.nspname, rc.relname, ra.attname
            FROM pg_constraint k
            JOIN pg_class cl ON cl.oid = k.conrelid
            JOIN pg_namespace n ON n.oid = cl.relnamespace
            JOIN pg_class rc ON rc.oid = k.confrelid
            JOIN pg_namespace rn ON rn.oid = rc.relnamespace
            CROSS JOIN LATERAL generate_subscripts(k.conkey, 1) AS s(i)
            JOIN pg_attribute a ON a.attrelid = k.conrelid AND a.attnum = k.conkey[s.i]
            JOIN pg_attribute ra ON ra.attrelid = k.confrelid AND ra.attnum = k.confkey[s.i]
            WHERE k.contype = 'f' AND cl.relispartition IS NOT TRUE
              AND {(t is null ? Filter : "k.conrelid = CAST(@t AS regclass)")}
            ORDER BY 1, 2, 3, s.i
            """, ct, t is null ? [] : [("@t", Qualified(t))]);

    internal static SchemaSnapshot Build(List<string> schemas, string? current, List<object?[]> rows, Func<string, DbObjectKind> kind)
    {
        var tables = new List<DbTable>();
        DbTable? t = null;
        foreach (var r in rows)
        {
            var schema = S(r[0]);
            var name = S(r[1]);
            if (t is null || t.Schema != schema || t.Name != name)
            {
                t = new DbTable { Schema = schema, Name = name, Kind = kind(S(r[2])) };
                tables.Add(t);
            }
            t.Columns.Add(new DbColumn(S(r[3]), S(r[4]), B(r[5]), B(r[7]), r[6] is null ? null : S(r[6]), r.Length > 8 && B(r[8])));
        }
        return new SchemaSnapshot { Schemas = schemas, Tables = tables, DefaultSchema = current };
    }
}

public sealed partial class MySqlDialect : Dialect
{
    public static readonly MySqlDialect Instance = new();
    public override DbKind Kind => DbKind.MySql;

    public override string Quote(string name) => "`" + name.Replace("`", "``") + "`";
    public override string Str(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "''") + "'";

    public override string ConnectionString(DbConnectionStringBuilder b, ConnectionEntry e, string? password)
    {
        SetOptions(b, e.Options);
        b["Server"] = e.Host.Length > 0 ? e.Host : "localhost";
        b["Port"] = e.Port > 0 ? e.Port : 3306;
        if (e.Database.Length > 0) b["Database"] = e.Database;
        if (e.User.Length > 0) b["User ID"] = e.User;
        if (password is not null) b["Password"] = password;
        if (!HasOption(e, "Application Name", "ApplicationName", "App")) b["Application Name"] = "DataPeach";
        // Without it the driver takes every @name for a parameter, so "SET @x = 1" and procedure OUT values fail.
        if (!HasOption(e, "Allow User Variables", "AllowUserVariables")) b["Allow User Variables"] = true;
        return b.ConnectionString;
    }

    public override string ReadOnlySessionSql => "SET SESSION TRANSACTION READ ONLY";

    private const string System = "('mysql','information_schema','performance_schema','sys')";

    public override bool HasDatabases => true;
    public override bool HasUse => true;

    public override async Task<(List<DbDatabase> All, string? Current)> LoadDatabasesAsync(DbConnection c, CancellationToken ct)
    {
        string[] system = ["mysql", "information_schema", "performance_schema", "sys"];
        var all = (await QueryAsync(c, "SELECT SCHEMA_NAME FROM information_schema.SCHEMATA ORDER BY 1", ct))
            .Select(r => S(r[0])).Select(n => new DbDatabase(n, system.Contains(n, StringComparer.OrdinalIgnoreCase))).ToList();
        var current = (await QueryAsync(c, "SELECT DATABASE()", ct)).FirstOrDefault()?[0] as string;
        return (all, current);
    }

    public override async Task<SchemaSnapshot> LoadSchemaAsync(DbConnection c, CancellationToken ct)
    {
        var current = (await QueryAsync(c, "SELECT DATABASE()", ct)).FirstOrDefault()?[0] as string;
        var scope = current is null ? $"c.TABLE_SCHEMA NOT IN {System}" : "c.TABLE_SCHEMA = DATABASE()";
        var schemas = current is null
            ? (await QueryAsync(c, $"SELECT SCHEMA_NAME FROM information_schema.SCHEMATA c WHERE SCHEMA_NAME NOT IN {System} ORDER BY 1", ct)).Select(r => S(r[0])).ToList()
            : new List<string> { current };
        var rows = await QueryAsync(c, $"""
            SELECT c.TABLE_SCHEMA, c.TABLE_NAME, t.TABLE_TYPE, c.COLUMN_NAME, c.COLUMN_TYPE, c.IS_NULLABLE = 'YES',
                   c.COLUMN_DEFAULT, 0, c.COLUMN_KEY = 'PRI'
            FROM information_schema.COLUMNS c
            JOIN information_schema.TABLES t ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
            WHERE {scope}
            ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION
            """, ct);
        foreach (var r in rows) (r[7], r[8]) = (r[8], null); // same layout as PostgreSQL: [7] = primary key, [8] = identity
        return PostgresDialect.Build(schemas, current, rows, k => k.Contains("VIEW") ? DbObjectKind.View : DbObjectKind.Table);
    }

    public override async Task<TableDetails> LoadDetailsAsync(DbConnection c, DbTable t, CancellationToken ct)
    {
        var d = new TableDetails();
        var args = new[] { ("@s", (object?)t.Schema), ("@t", t.Name) };
        var idx = await QueryAsync(c, """
            SELECT INDEX_NAME, NON_UNIQUE = 0, GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX SEPARATOR ', ')
            FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = @s AND TABLE_NAME = @t
            GROUP BY INDEX_NAME, NON_UNIQUE ORDER BY 1
            """, ct, args);
        foreach (var r in idx) d.Indexes.Add(new DbIndex(S(r[0]), B(r[1]), "(" + S(r[2]) + ")"));
        d.ForeignKeys.AddRange(await LoadForeignKeysAsync(c, t, ct));
        return d;
    }

    /// <summary>Without a table: those of the current database, or of every user database when there is none.</summary>
    public override Task<List<DbForeignKey>> LoadForeignKeysAsync(DbConnection c, DbTable? t, CancellationToken ct) =>
        ForeignKeysAsync(c, $"""
            SELECT TABLE_SCHEMA, TABLE_NAME, CONSTRAINT_NAME, COLUMN_NAME, REFERENCED_TABLE_SCHEMA, REFERENCED_TABLE_NAME, REFERENCED_COLUMN_NAME
            FROM information_schema.KEY_COLUMN_USAGE
            WHERE REFERENCED_TABLE_NAME IS NOT NULL
              AND {(t is null ? $"(TABLE_SCHEMA = DATABASE() OR (DATABASE() IS NULL AND TABLE_SCHEMA NOT IN {System}))" : "TABLE_SCHEMA = @s AND TABLE_NAME = @t")}
            ORDER BY 1, 2, 3, ORDINAL_POSITION
            """, ct, t is null ? [] : [("@s", t.Schema), ("@t", t.Name)]);
}

public sealed partial class SqliteDialect : Dialect
{
    public static readonly SqliteDialect Instance = new();
    public override DbKind Kind => DbKind.Sqlite;

    public override string BoolLiteral(bool b) => b ? "1" : "0";

    public override string ConnectionString(DbConnectionStringBuilder b, ConnectionEntry e, string? password)
    {
        SetOptions(b, e.Options);
        b["Data Source"] = e.FilePath;
        // Never create a file by mistake: a missing database is an error, not a new empty one.
        b["Mode"] = e.Mode == ConnectionMode.ReadOnly ? "ReadOnly" : "ReadWrite";
        if (password is { Length: > 0 }) b["Password"] = password;
        return b.ConnectionString;
    }

    public override async Task<SchemaSnapshot> LoadSchemaAsync(DbConnection c, CancellationToken ct)
    {
        var rows = await QueryAsync(c, """
            SELECT m.name, m.type, p.name, p.type, p."notnull", p.dflt_value, p.pk
            FROM sqlite_master m JOIN pragma_table_info(m.name) p
            WHERE m.type IN ('table', 'view') AND m.name NOT LIKE 'sqlite\_%' ESCAPE '\'
            ORDER BY m.name, p.cid
            """, ct);
        var tables = new List<DbTable>();
        DbTable? t = null;
        foreach (var r in rows)
        {
            var name = S(r[0]);
            if (t is null || t.Name != name)
            {
                t = new DbTable { Name = name, Kind = S(r[1]) == "view" ? DbObjectKind.View : DbObjectKind.Table };
                tables.Add(t);
            }
            t.Columns.Add(new DbColumn(S(r[2]), S(r[3]), !B(r[4]), B(r[6]), r[5] is null ? null : S(r[5])));
        }
        return new SchemaSnapshot { Schemas = new(), Tables = tables, DefaultSchema = null };
    }

    public override async Task<TableDetails> LoadDetailsAsync(DbConnection c, DbTable t, CancellationToken ct)
    {
        var d = new TableDetails();
        foreach (var r in await QueryAsync(c, """
                     SELECT il.name, il."unique", group_concat(ii.name, ', ')
                     FROM pragma_index_list(@t) il JOIN pragma_index_info(il.name) ii
                     GROUP BY il.name, il."unique" ORDER BY 1
                     """, ct, ("@t", t.Name)))
            d.Indexes.Add(new DbIndex(S(r[0]), B(r[1]), "(" + S(r[2]) + ")"));
        d.ForeignKeys.AddRange(await LoadForeignKeysAsync(c, t, ct));
        return d;
    }

    /// <summary>
    /// Keys have no name in SQLite's catalog: "fk" and their number. "REFERENCES users" without columns points at the
    /// primary key, whose column seq + 1 is the one paired with this one.
    /// </summary>
    public override Task<List<DbForeignKey>> LoadForeignKeysAsync(DbConnection c, DbTable? t, CancellationToken ct) =>
        ForeignKeysAsync(c, $"""
            SELECT NULL, m.name, 'fk' || f.id, f."from", NULL, f."table",
                   COALESCE(f."to", (SELECT p.name FROM pragma_table_info(f."table") p WHERE p.pk = f.seq + 1))
            FROM sqlite_master m JOIN pragma_foreign_key_list(m.name) f
            WHERE m.type = 'table' AND {(t is null ? @"m.name NOT LIKE 'sqlite\_%' ESCAPE '\'" : "m.name = @t")}
            ORDER BY m.name, f.id, f.seq
            """, ct, t is null ? [] : [("@t", t.Name)]);
}

public sealed partial class SqlServerDialect : Dialect
{
    public static readonly SqlServerDialect Instance = new();
    public override DbKind Kind => DbKind.SqlServer;
    protected override string? DefaultSchemaHint => "dbo";

    public override string Quote(string name) => "[" + name.Replace("]", "]]") + "]";
    public override string BoolLiteral(bool b) => b ? "1" : "0";
    public override string BytesLiteral(byte[] b) => "0x" + Convert.ToHexString(b);
    public override string Str(string s) => "N'" + s.Replace("'", "''") + "'";

    /// <summary>No LIMIT in T-SQL: TOP goes before the columns.</summary>
    public override string SelectTable(DbTable t, string? where, string? orderBy, int limit)
    {
        var sb = new StringBuilder("SELECT TOP (").Append(limit.ToString(CultureInfo.InvariantCulture)).Append(") * FROM ").Append(NameOf(t));
        if (!string.IsNullOrWhiteSpace(where)) sb.Append(" WHERE ").Append(where.Trim());
        if (!string.IsNullOrWhiteSpace(orderBy)) sb.Append(" ORDER BY ").Append(orderBy.Trim());
        return sb.ToString();
    }

    public override string ConnectionString(DbConnectionStringBuilder b, ConnectionEntry e, string? password)
    {
        SetOptions(b, e.Options);
        var host = e.Host.Length > 0 ? e.Host : "localhost";
        b["Data Source"] = e.Port > 0 && e.Port != 1433 ? $"{host},{e.Port}" : host;
        if (e.Database.Length > 0) b["Initial Catalog"] = e.Database;
        // Without a user, the Windows account signs in (the usual way into a local SQL Server).
        if (e.User.Length > 0) b["User ID"] = e.User;
        else if (!HasOption(e, "Integrated Security", "Trusted_Connection", "Authentication")) b["Integrated Security"] = true;
        if (password is not null) b["Password"] = password;
        if (!HasOption(e, "Application Name", "ApplicationName", "App")) b["Application Name"] = "DataPeach";
        return b.ConnectionString;
    }

    // T-SQL has no read-only session: the app alone refuses to write (ApplicationIntent=ReadOnly only routes to a
    // replica of an availability group).

    public override bool HasDatabases => true;
    public override bool HasUse => true;

    /// <summary>master, tempdb, model and msdb (ids 1-4) and a replication distributor are the server's; databases
    /// the login cannot enter or that are not online are left out.</summary>
    public override async Task<(List<DbDatabase> All, string? Current)> LoadDatabasesAsync(DbConnection c, CancellationToken ct)
    {
        var all = (await QueryAsync(c, """
                SELECT name, CAST(CASE WHEN database_id <= 4 OR is_distributor = 1 THEN 1 ELSE 0 END AS bit)
                FROM sys.databases WHERE state = 0 AND HAS_DBACCESS(name) = 1 ORDER BY name
                """, ct))
            .Select(r => new DbDatabase(S(r[0]), B(r[1]))).ToList();
        var current = (await QueryAsync(c, "SELECT DB_NAME()", ct)).FirstOrDefault()?[0] as string;
        return (all, current);
    }

    /// <summary>The column type as it is written in DDL: nvarchar(50), decimal(10,2), varchar(max)…</summary>
    private const string TypeSql = """
        CASE
            WHEN ty.name IN ('varchar','char','varbinary','binary') THEN ty.name + '(' + IIF(c.max_length = -1, 'max', CAST(c.max_length AS varchar(10))) + ')'
            WHEN ty.name IN ('nvarchar','nchar') THEN ty.name + '(' + IIF(c.max_length = -1, 'max', CAST(c.max_length / 2 AS varchar(10))) + ')'
            WHEN ty.name IN ('decimal','numeric') THEN ty.name + '(' + CAST(c.precision AS varchar(10)) + ',' + CAST(c.scale AS varchar(10)) + ')'
            WHEN ty.name IN ('datetime2','time','datetimeoffset') THEN ty.name + '(' + CAST(c.scale AS varchar(10)) + ')'
            ELSE ty.name
        END
        """;

    public override async Task<SchemaSnapshot> LoadSchemaAsync(DbConnection c, CancellationToken ct)
    {
        // Fixed schemas guest, INFORMATION_SCHEMA and sys (2-4) and the db_* role schemas (16384+) are not the user's.
        var schemas = (await QueryAsync(c, "SELECT name FROM sys.schemas WHERE schema_id NOT IN (2, 3, 4) AND schema_id < 16384 ORDER BY 1", ct))
            .Select(r => S(r[0])).ToList();
        var current = (await QueryAsync(c, "SELECT SCHEMA_NAME()", ct)).FirstOrDefault()?[0] as string;
        var rows = await QueryAsync(c, $"""
            SELECT s.name, o.name, o.type, c.name, {TypeSql}, c.is_nullable, OBJECT_DEFINITION(c.default_object_id),
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                                          WHERE i.object_id = o.object_id AND i.is_primary_key = 1 AND ic.column_id = c.column_id) THEN 1 ELSE 0 END AS bit),
                   c.is_identity
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.columns c ON c.object_id = o.object_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0
            ORDER BY s.name, o.name, c.column_id
            """, ct);
        return PostgresDialect.Build(schemas, current, rows, k => k.Trim() == "V" ? DbObjectKind.View : DbObjectKind.Table);
    }

    public override async Task<TableDetails> LoadDetailsAsync(DbConnection c, DbTable t, CancellationToken ct)
    {
        var d = new TableDetails();
        var name = Qualified(t);
        foreach (var r in await QueryAsync(c, """
                     SELECT i.name, i.is_unique,
                            STRING_AGG(col.name, ', ') WITHIN GROUP (ORDER BY ic.key_ordinal)
                     FROM sys.indexes i
                     JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                     JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
                     WHERE i.object_id = OBJECT_ID(@t) AND i.type > 0
                     GROUP BY i.name, i.is_unique ORDER BY 1
                     """, ct, ("@t", name)))
            d.Indexes.Add(new DbIndex(S(r[0]), B(r[1]), "(" + S(r[2]) + ")"));
        d.ForeignKeys.AddRange(await LoadForeignKeysAsync(c, t, ct));
        return d;
    }

    public override Task<List<DbForeignKey>> LoadForeignKeysAsync(DbConnection c, DbTable? t, CancellationToken ct) =>
        ForeignKeysAsync(c, $"""
            SELECT OBJECT_SCHEMA_NAME(fk.parent_object_id), OBJECT_NAME(fk.parent_object_id), fk.name, pc.name,
                   OBJECT_SCHEMA_NAME(fk.referenced_object_id), OBJECT_NAME(fk.referenced_object_id), rc.name
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE {(t is null ? "fk.is_ms_shipped = 0" : "fk.parent_object_id = OBJECT_ID(@t)")}
            ORDER BY 1, 2, 3, fkc.constraint_column_id
            """, ct, t is null ? [] : [("@t", Qualified(t))]);
}

public sealed partial class OracleDialect : Dialect
{
    public static readonly OracleDialect Instance = new();
    public override DbKind Kind => DbKind.Oracle;

    // BOOLEAN only exists since 23ai; 1/0 works everywhere.
    public override string BoolLiteral(bool b) => b ? "1" : "0";
    public override string BytesLiteral(byte[] b) => "HEXTORAW('" + Convert.ToHexString(b) + "')";
    public override string Param(int index, object? value, DbColumn? column) => ":p" + index;
    public override string ParamName(int index) => "p" + index;

    public override string SelectTable(DbTable t, string? where, string? orderBy, int limit)
    {
        var sb = new StringBuilder("SELECT * FROM ").Append(NameOf(t));
        if (!string.IsNullOrWhiteSpace(where)) sb.Append(" WHERE ").Append(where.Trim());
        if (!string.IsNullOrWhiteSpace(orderBy)) sb.Append(" ORDER BY ").Append(orderBy.Trim());
        sb.Append(" FETCH FIRST ").Append(limit.ToString(CultureInfo.InvariantCulture)).Append(" ROWS ONLY");
        return sb.ToString();
    }

    /// <summary>The database field holds the service name ("FREEPDB1", "ORCLPDB1"…), the EZConnect way.</summary>
    public override string ConnectionString(DbConnectionStringBuilder b, ConnectionEntry e, string? password)
    {
        SetOptions(b, e.Options);
        var host = e.Host.Length > 0 ? e.Host : "localhost";
        b["Data Source"] = $"{host}:{(e.Port > 0 ? e.Port : 1521)}{(e.Database.Length > 0 ? "/" + e.Database : "")}";
        if (e.User.Length > 0) b["User Id"] = e.User;
        if (password is not null) b["Password"] = password;
        return b.ConnectionString;
    }

    // Oracle has read-only transactions but no read-only session: the app alone refuses to write.

    private const string TypeSql = """
        CASE
            WHEN c.data_type IN ('VARCHAR2', 'NVARCHAR2', 'CHAR', 'NCHAR') THEN c.data_type || '(' || c.char_length || ')'
            WHEN c.data_type = 'RAW' THEN 'RAW(' || c.data_length || ')'
            WHEN c.data_type = 'NUMBER' AND c.data_precision IS NOT NULL THEN 'NUMBER(' || c.data_precision || ',' || c.data_scale || ')'
            ELSE c.data_type
        END
        """;

    // Schemas of real users, not the dozens Oracle maintains itself (SYS, XDB, MDSYS…).
    private const string Owners = "SELECT username FROM all_users WHERE oracle_maintained = 'N'";

    public override async Task<SchemaSnapshot> LoadSchemaAsync(DbConnection c, CancellationToken ct)
    {
        var schemas = (await QueryAsync(c, Owners + " ORDER BY 1", ct)).Select(r => S(r[0])).ToList();
        var current = (await QueryAsync(c, "SELECT SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') FROM dual", ct)).FirstOrDefault()?[0] as string;
        // DATA_DEFAULT is a LONG, which the driver does not read inline: defaults are left out.
        var rows = await QueryAsync(c, $"""
            SELECT o.owner, o.object_name, o.object_type, c.column_name, {TypeSql}, CASE c.nullable WHEN 'Y' THEN 1 ELSE 0 END, NULL,
                   CASE WHEN EXISTS (SELECT 1 FROM all_constraints k JOIN all_cons_columns kc ON kc.owner = k.owner AND kc.constraint_name = k.constraint_name
                                     WHERE k.owner = o.owner AND k.table_name = o.object_name AND k.constraint_type = 'P' AND kc.column_name = c.column_name)
                        THEN 1 ELSE 0 END
            FROM all_objects o
            JOIN all_tab_columns c ON c.owner = o.owner AND c.table_name = o.object_name
            WHERE o.object_type IN ('TABLE', 'VIEW') AND o.owner IN ({Owners}) AND o.object_name NOT LIKE 'BIN$%'
            ORDER BY o.owner, o.object_name, c.column_id
            """, ct);
        return PostgresDialect.Build(schemas, current, rows, k => k == "VIEW" ? DbObjectKind.View : DbObjectKind.Table);
    }

    public override async Task<TableDetails> LoadDetailsAsync(DbConnection c, DbTable t, CancellationToken ct)
    {
        var d = new TableDetails();
        var args = new[] { ("s", (object?)t.Schema), ("t", t.Name) };
        foreach (var r in await QueryAsync(c, """
                     SELECT i.index_name, CASE i.uniqueness WHEN 'UNIQUE' THEN 1 ELSE 0 END,
                            LISTAGG(ic.column_name, ', ') WITHIN GROUP (ORDER BY ic.column_position)
                     FROM all_indexes i JOIN all_ind_columns ic ON ic.index_owner = i.owner AND ic.index_name = i.index_name
                     WHERE i.table_owner = :s AND i.table_name = :t
                     GROUP BY i.index_name, i.uniqueness ORDER BY 1
                     """, ct, args))
            d.Indexes.Add(new DbIndex(S(r[0]), B(r[1]), "(" + S(r[2]) + ")"));
        d.ForeignKeys.AddRange(await LoadForeignKeysAsync(c, t, ct));
        return d;
    }

    public override Task<List<DbForeignKey>> LoadForeignKeysAsync(DbConnection c, DbTable? t, CancellationToken ct) =>
        ForeignKeysAsync(c, $"""
            SELECT k.owner, k.table_name, k.constraint_name, kc.column_name, r.owner, r.table_name, rc.column_name
            FROM all_constraints k
            JOIN all_cons_columns kc ON kc.owner = k.owner AND kc.constraint_name = k.constraint_name
            JOIN all_constraints r ON r.owner = k.r_owner AND r.constraint_name = k.r_constraint_name
            JOIN all_cons_columns rc ON rc.owner = r.owner AND rc.constraint_name = r.constraint_name AND rc.position = kc.position
            WHERE k.constraint_type = 'R'
              AND {(t is null ? $"k.owner IN ({Owners}) AND k.table_name NOT LIKE 'BIN$%'" : "k.owner = :s AND k.table_name = :t")}
            ORDER BY 1, 2, 3, kc.position
            """, ct, t is null ? [] : [("s", t.Schema), ("t", t.Name)]);
}

public sealed partial class DuckDbDialect : Dialect
{
    public static readonly DuckDbDialect Instance = new();
    public override DbKind Kind => DbKind.DuckDb;
    protected override string? DefaultSchemaHint => "main";
    public override bool CountsAsResult => true;

    public override string BytesLiteral(byte[] b) => "from_hex('" + Convert.ToHexString(b) + "')";
    public override string Param(int index, object? value, DbColumn? column) => "$p" + index;
    public override string ParamName(int index) => "p" + index;

    public override string ConnectionString(DbConnectionStringBuilder b, ConnectionEntry e, string? password)
    {
        SetOptions(b, e.Options);
        b["Data Source"] = e.FilePath;
        if (e.Mode == ConnectionMode.ReadOnly) b["ACCESS_MODE"] = "READ_ONLY";
        return b.ConnectionString;
    }

    public override async Task<SchemaSnapshot> LoadSchemaAsync(DbConnection c, CancellationToken ct)
    {
        // Only the file's own database: attached ones and the built-in "system"/"temp" stay out.
        var schemas = (await QueryAsync(c, "SELECT schema_name FROM duckdb_schemas() WHERE database_name = current_database() AND NOT internal ORDER BY 1", ct))
            .Select(r => S(r[0])).ToList();
        var current = (await QueryAsync(c, "SELECT current_schema()", ct)).FirstOrDefault()?[0] as string;
        var rows = await QueryAsync(c, """
            SELECT c.schema_name, c.table_name, CASE WHEN v.view_name IS NULL THEN 'T' ELSE 'V' END, c.column_name, c.data_type,
                   c.is_nullable, c.column_default,
                   EXISTS (SELECT 1 FROM duckdb_constraints() k
                           WHERE k.database_name = c.database_name AND k.schema_name = c.schema_name AND k.table_name = c.table_name
                             AND k.constraint_type = 'PRIMARY KEY' AND list_contains(k.constraint_column_names, c.column_name))
            FROM duckdb_columns() c
            LEFT JOIN duckdb_views() v ON v.database_name = c.database_name AND v.schema_name = c.schema_name AND v.view_name = c.table_name
            WHERE c.database_name = current_database() AND NOT c.internal
            ORDER BY c.schema_name, c.table_name, c.column_index
            """, ct);
        return PostgresDialect.Build(schemas, current, rows, k => k == "V" ? DbObjectKind.View : DbObjectKind.Table);
    }

    public override async Task<TableDetails> LoadDetailsAsync(DbConnection c, DbTable t, CancellationToken ct)
    {
        var d = new TableDetails();
        var args = new[] { ("s", (object?)t.Schema), ("t", t.Name) };
        foreach (var r in await QueryAsync(c, """
                     SELECT index_name, is_unique, expressions FROM duckdb_indexes()
                     WHERE database_name = current_database() AND schema_name = $s AND table_name = $t ORDER BY 1
                     """, ct, args))
            d.Indexes.Add(new DbIndex(S(r[0]), B(r[1]), "(" + S(r[2]).Trim('[', ']') + ")"));
        d.ForeignKeys.AddRange(await LoadForeignKeysAsync(c, t, ct));
        return d;
    }

    /// <summary>A key references a table of its own schema; the column lists pair up by position.</summary>
    public override Task<List<DbForeignKey>> LoadForeignKeysAsync(DbConnection c, DbTable? t, CancellationToken ct) =>
        ForeignKeysAsync(c, $"""
            SELECT schema_name, table_name, constraint_name, constraint_column_names[i], schema_name, referenced_table, referenced_column_names[i]
            FROM (SELECT *, generate_subscripts(constraint_column_names, 1) AS i FROM duckdb_constraints()
                  WHERE database_name = current_database() AND constraint_type = 'FOREIGN KEY'
                    {(t is null ? "" : "AND schema_name = $s AND table_name = $t")})
            ORDER BY 1, 2, 3, i
            """, ct, t is null ? [] : [("s", t.Schema), ("t", t.Name)]);
}
