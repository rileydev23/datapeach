using System.Text.Json.Serialization;

namespace DataPeach.Core;

/// <summary>Database engines DataPeach talks to. The driver of each one is downloaded on demand (see Drivers/).</summary>
public enum DbKind { Sqlite, Postgres, MySql, SqlServer, Oracle, DuckDb, Sybase }

/// <summary>
/// How careful the app is with a connection: <see cref="Production"/> asks before anything that writes and shows a
/// banner; <see cref="ReadOnly"/> refuses to write at all (the session is opened read-only where the engine allows).
/// </summary>
public enum ConnectionMode { Normal, Production, ReadOnly }

/// <summary>A saved connection. The password is never stored here: it lives encrypted in <see cref="Secrets"/>.</summary>
public sealed class ConnectionEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public DbKind Kind { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Database { get; set; } = "";
    public string User { get; set; } = "";
    /// <summary>SQLite and DuckDB: the database file.</summary>
    public string FilePath { get; set; } = "";
    /// <summary>Extra "key=value;…" options appended to the connection string (SSL mode, timeouts…).</summary>
    public string Options { get; set; } = "";
    public ConnectionMode Mode { get; set; }
    /// <summary>
    /// Optional project folder: its consoles are saved in <c>&lt;folder&gt;/.peach/queries/</c> so they can be
    /// versioned with git. Without it they stay in the settings folder.
    /// </summary>
    public string? QueriesFolder { get; set; }
    public bool Favorite { get; set; }
    public DateTimeOffset LastOpened { get; set; }
    /// <summary>Name of the <see cref="ConnectionGroup"/> it belongs to; null = ungrouped.</summary>
    public string? Group { get; set; }

    [JsonIgnore] public bool IsFile => DbKinds.IsFileKind(Kind);
    /// <summary>File connections whose file no longer exists.</summary>
    [JsonIgnore] public bool Missing { get; set; }

    /// <summary>"localhost:5432/shop" or the file path: what the Home shows under the name.</summary>
    [JsonIgnore]
    public string Target => IsFile
        ? FilePath
        : $"{(User.Length > 0 ? User + "@" : "")}{Host}{(Port > 0 ? ":" + Port : "")}{(Database.Length > 0 ? "/" + Database : "")}";

    public ConnectionEntry Clone() => (ConnectionEntry)MemberwiseClone();
}

public sealed class ConnectionGroup
{
    public string Name { get; set; } = "";
    public bool Collapsed { get; set; }
}

public static class DbKinds
{
    public static readonly DbKind[] All = { DbKind.Sqlite, DbKind.Postgres, DbKind.MySql, DbKind.SqlServer, DbKind.Oracle, DbKind.Sybase, DbKind.DuckDb };

    public static string Name(DbKind k) => k switch
    {
        DbKind.Sqlite => "SQLite",
        DbKind.Postgres => "PostgreSQL",
        DbKind.MySql => "MySQL",
        DbKind.SqlServer => "SQL Server",
        DbKind.Oracle => "Oracle",
        DbKind.DuckDb => "DuckDB",
        DbKind.Sybase => "SAP ASE",
        _ => k.ToString(),
    };

    public static int DefaultPort(DbKind k) => k switch
    {
        DbKind.Postgres => 5432,
        DbKind.MySql => 3306,
        DbKind.SqlServer => 1433,
        DbKind.Oracle => 1521,
        DbKind.Sybase => 5000,
        _ => 0,
    };

    /// <summary>Color of the engine (a category of the series in docs/DISENO.md 3.7).</summary>
    public static string Color(DbKind k) => k switch
    {
        DbKind.Postgres => "#4a90d9",
        DbKind.MySql => "#e0a24a",
        DbKind.Sqlite => "#4ac7c0",
        DbKind.SqlServer => "#9a7ae0",
        DbKind.Oracle => "#e0645a",
        DbKind.DuckDb => "#c8c84a",
        DbKind.Sybase => "#6cc070",
        _ => "#8a8d93",
    };

    /// <summary>Engines whose database is a local file instead of a server.</summary>
    public static bool IsFileKind(DbKind k) => k is DbKind.Sqlite or DbKind.DuckDb;

    /// <summary>Patterns of the "open a database file" pickers.</summary>
    public static readonly string[] FilePatterns = { "*.db", "*.sqlite", "*.sqlite3", "*.db3", "*.s3db", "*.duckdb", "*.ddb" };

    /// <summary>
    /// The engine of a database file, by its header (".db" is used by both) and else by its extension; null when it
    /// looks like neither.
    /// </summary>
    public static DbKind? DetectFile(string path)
    {
        try
        {
            // Shared: the file may be open in another program (DuckDB keeps it open while in use).
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[16];
            int n = f.Read(head, 0, head.Length);
            if (n >= 16 && "SQLite format 3\0"u8.SequenceEqual(head)) return DbKind.Sqlite;
            if (n >= 12 && "DUCK"u8.SequenceEqual(head.AsSpan(8, 4))) return DbKind.DuckDb;
            if (n > 0) return null; // something else with content
        }
        catch { /* unreadable or missing: go by the name */ }
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".sqlite" or ".sqlite3" or ".db" or ".db3" or ".s3db" => DbKind.Sqlite,
            ".duckdb" or ".ddb" => DbKind.DuckDb,
            _ => null,
        };
    }
}
