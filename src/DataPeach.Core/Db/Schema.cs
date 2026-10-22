namespace DataPeach.Core.Db;

public enum DbObjectKind { Table, View }

/// <summary>A column; <see cref="IsIdentity"/> marks the ones the server numbers itself (SQL Server and SAP ASE).</summary>
public sealed record DbColumn(string Name, string DataType, bool Nullable, bool IsPrimaryKey, string? Default, bool IsIdentity = false);

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
    /// <summary>Stored procedures and functions, loaded with the tables.</summary>
    public List<DbRoutine> Routines { get; init; } = new();

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

public enum DbRoutineKind { Procedure, Function }

public enum ParamDirection { In, Out, InOut }

public sealed record DbParameter(string Name, string DataType, ParamDirection Direction);

/// <summary>
/// A stored procedure or function. <see cref="Id"/> tells overloads apart (PostgreSQL); <see cref="ReturnType"/> is
/// null for procedures, and "TABLE" when a function returns rows.
/// </summary>
public sealed class DbRoutine
{
    public string? Schema { get; init; }
    public required string Name { get; init; }
    public DbRoutineKind Kind { get; init; }
    public string? ReturnType { get; init; }
    public bool ReturnsTable { get; init; }
    public string? Id { get; init; }
    public List<DbParameter> Parameters { get; init; } = new();

    public string Display => Schema is null ? Name : $"{Schema}.{Name}";
    /// <summary>"(a int, b text)": what tells two overloads apart in the tree.</summary>
    public string Signature => "(" + string.Join(", ", Parameters.Where(p => p.Direction != ParamDirection.Out || Kind == DbRoutineKind.Procedure)
        .Select(p => ((p.Direction == ParamDirection.In ? "" : p.Direction.ToString().ToUpperInvariant() + " ") + p.Name + " " + p.DataType).Trim())) + ")";

    public override string ToString() => Display;
}
