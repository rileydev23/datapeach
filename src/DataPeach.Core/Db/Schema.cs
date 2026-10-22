namespace DataPeach.Core.Db;

public enum DbObjectKind { Table, View }

public sealed record DbColumn(string Name, string DataType, bool Nullable, bool IsPrimaryKey, string? Default);

/// <summary>A table or view with its columns. <see cref="Schema"/> is null where the engine has none (SQLite).</summary>
public sealed class DbTable
{
    public string? Schema { get; init; }
    public required string Name { get; init; }
    public DbObjectKind Kind { get; init; }
    public List<DbColumn> Columns { get; } = new();

    public IReadOnlyList<DbColumn> PrimaryKey => Columns.Where(c => c.IsPrimaryKey).ToList();
    /// <summary>Editable in the grid: a table with a primary key (without one there is no safe WHERE).</summary>
    public bool CanEdit => Kind == DbObjectKind.Table && Columns.Any(c => c.IsPrimaryKey);
    public string Display => Schema is null ? Name : $"{Schema}.{Name}";

    public DbColumn? Column(string name) =>
        Columns.FirstOrDefault(c => c.Name == name) ?? Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => Display;
}

/// <summary>Everything the app knows about a database's structure, loaded in one go and cached per connection.</summary>
public sealed class SchemaSnapshot
{
    public List<string> Schemas { get; init; } = new();
    public List<DbTable> Tables { get; init; } = new();
    /// <summary>Schema that unqualified names resolve to ("public", the current database…).</summary>
    public string? DefaultSchema { get; init; }

    public static readonly SchemaSnapshot Empty = new();

    /// <summary>Finds a table by name, optionally qualified; unqualified names prefer the default schema.</summary>
    public DbTable? Find(string? schema, string name)
    {
        bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        if (schema is not null) return Tables.FirstOrDefault(t => Same(t.Schema, schema) && t.Name == name)
                                       ?? Tables.FirstOrDefault(t => Same(t.Schema, schema) && Same(t.Name, name));
        return Tables.FirstOrDefault(t => Same(t.Schema, DefaultSchema) && t.Name == name)
               ?? Tables.FirstOrDefault(t => Same(t.Schema, DefaultSchema) && Same(t.Name, name))
               ?? Tables.FirstOrDefault(t => Same(t.Name, name));
    }
}

public sealed record DbIndex(string Name, bool Unique, string Definition);
public sealed record DbForeignKey(string Name, string Definition);

/// <summary>What the detail panel shows besides the columns; loaded when a table is selected.</summary>
public sealed class TableDetails
{
    public List<DbIndex> Indexes { get; } = new();
    public List<DbForeignKey> ForeignKeys { get; } = new();
}
