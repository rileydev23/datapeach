using System.Collections;
using System.Data.Common;
using System.Globalization;
using System.Text;
using DataPeach.Core.Sql;

namespace DataPeach.Core.Db;

/// <summary>
/// Stored procedures and functions: listing them with their parameters, reading their code back as a script that
/// recreates them, calling them and dropping them. Each engine says it its own way; what is common lives here.
/// </summary>
public abstract partial class Dialect
{
    /// <summary>Procedures and functions of the user's schemas. Engines without them (SQLite) return none.</summary>
    public virtual Task<List<DbRoutine>> LoadRoutinesAsync(DbConnection c, CancellationToken ct) => Task.FromResult(new List<DbRoutine>());

    /// <summary>
    /// A script that recreates the routine as it is now, ready to edit and run in a console (with the engine's own
    /// separators: GO, / or DELIMITER).
    /// </summary>
    public virtual Task<string> LoadRoutineSourceAsync(DbConnection c, DbRoutine r, CancellationToken ct) => Task.FromResult("");

    public string NameOf(DbRoutine r) => r.Schema is null || r.Schema == DefaultSchemaHint ? Name(r.Name) : Name(r.Schema) + "." + Name(r.Name);

    /// <summary>
    /// The statement(s) that call <paramref name="r"/>. <paramref name="args"/> holds one SQL expression per input
    /// parameter (<see cref="InputsOf"/>), already written as literals.
    /// </summary>
    public abstract string CallSql(DbRoutine r, IReadOnlyList<string> args);

    public virtual string DropSql(DbRoutine r) => (r.Kind == DbRoutineKind.Procedure ? "DROP PROCEDURE " : "DROP FUNCTION ") + NameOf(r);

    /// <summary>The parameters the caller gives a value to: everything but pure OUT ones.</summary>
    public static IReadOnlyList<DbParameter> InputsOf(DbRoutine r) => r.Parameters.Where(p => p.Direction != ParamDirection.Out).ToList();

    /// <summary>
    /// What the user typed for a parameter, as SQL: empty is NULL, numbers stay as they are for numeric types,
    /// anything else becomes a string literal. A value starting with '=' is taken as a raw expression.
    /// </summary>
    public string ArgLiteral(string? text, DbParameter p)
    {
        if (string.IsNullOrEmpty(text)) return "NULL";
        if (text.StartsWith('=')) return text[1..].Trim();
        var t = p.DataType.ToLowerInvariant();
        // Untyped (DuckDB macros): a number is taken as one.
        bool numeric = t.Length == 0 || t.Contains("int") || t.Contains("num") || t.Contains("dec") || t.Contains("float") || t.Contains("double")
                       || t.Contains("real") || t.Contains("money") || t is "bit" or "number";
        if (numeric && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return text.Trim();
        if (t.StartsWith("bool", StringComparison.Ordinal) && bool.TryParse(text, out var b)) return BoolLiteral(b);
        return Str(text);
    }

    /// <summary>Text of a value the driver may hand back as a list (DuckDB's VARCHAR[]).</summary>
    protected static List<string> Strings(object? v) => v switch
    {
        null => new(),
        string s => s.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        IEnumerable e => e.Cast<object?>().Select(S).ToList(),
        _ => new() { S(v) },
    };

    /// <summary>
    /// EXEC for SQL Server and SAP ASE: OUTPUT parameters get a variable (set first when they are INOUT) and are read
    /// back with a SELECT after the call.
    /// </summary>
    protected static string TsqlCall(Dialect d, DbRoutine r, IReadOnlyList<string> args)
    {
        var name = d.NameOf(r);
        if (r.Kind == DbRoutineKind.Function)
            return (r.ReturnsTable ? "SELECT * FROM " : "SELECT ") + name + "(" + string.Join(", ", args) + ")";
        var sb = new StringBuilder();
        var parts = new List<string>();
        int i = 0;
        foreach (var p in r.Parameters)
        {
            if (p.Direction == ParamDirection.In) { parts.Add($"{p.Name} = {args[i++]}"); continue; }
            sb.Append("DECLARE ").Append(p.Name).Append(' ').Append(p.DataType).Append('\n');
            if (p.Direction == ParamDirection.InOut && args[i++] is var v && v != "NULL") sb.Append("SELECT ").Append(p.Name).Append(" = ").Append(v).Append('\n');
            parts.Add($"{p.Name} = {p.Name} OUTPUT");
        }
        sb.Append("EXEC ").Append(name);
        if (parts.Count > 0) sb.Append(' ').Append(string.Join(", ", parts));
        var outs = r.Parameters.Where(p => p.Direction != ParamDirection.In).ToList();
        if (outs.Count > 0) sb.Append("\nSELECT ").Append(string.Join(", ", outs.Select(p => $"{p.Name} AS {p.Name.TrimStart('@')}")));
        return sb.ToString();
    }
}

public sealed partial class PostgresDialect
{
    private const string RoutineFilter = """
        p.prokind IN ('f', 'p') AND n.nspname NOT IN ('pg_catalog','information_schema','crdb_internal','pg_extension')
        AND n.nspname NOT LIKE 'pg\_toast%' AND n.nspname NOT LIKE 'pg\_temp%'
        AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e')
        """;

    public override async Task<List<DbRoutine>> LoadRoutinesAsync(DbConnection c, CancellationToken ct)
    {
        var heads = await QueryAsync(c, $"""
            SELECT n.nspname, p.proname, p.prokind::text, p.oid::text, pg_get_function_result(p.oid), p.proretset
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE {RoutineFilter}
            ORDER BY 1, 2, 4
            """, ct);
        // proargnames and proargmodes follow proallargtypes when there are OUT parameters, else proargtypes.
        var args = await QueryAsync(c, $"""
            SELECT p.oid::text, COALESCE(p.proargnames[a.ord], ''), format_type(a.t, NULL), COALESCE(p.proargmodes[a.ord]::text, 'i')
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace,
                 unnest(COALESCE(p.proallargtypes, CAST(p.proargtypes AS oid[]))) WITH ORDINALITY a(t, ord)
            WHERE {RoutineFilter}
            ORDER BY 1, a.ord
            """, ct);
        var byId = args.GroupBy(r => S(r[0])).ToDictionary(g => g.Key, g => g.ToList());
        var list = new List<DbRoutine>();
        foreach (var h in heads)
        {
            var id = S(h[3]);
            bool proc = S(h[2]) == "p";
            var ps = new List<DbParameter>();
            foreach (var a in byId.GetValueOrDefault(id) ?? new())
            {
                var mode = S(a[3]);
                if (mode == "t") continue; // a column of RETURNS TABLE
                ps.Add(new DbParameter(S(a[1]), S(a[2]), mode switch { "o" => ParamDirection.Out, "b" => ParamDirection.InOut, _ => ParamDirection.In }));
            }
            list.Add(new DbRoutine
            {
                Schema = S(h[0]), Name = S(h[1]), Id = id, Parameters = ps,
                Kind = proc ? DbRoutineKind.Procedure : DbRoutineKind.Function,
                ReturnType = proc ? null : S(h[4]),
                ReturnsTable = !proc && (B(h[5]) || S(h[4]).StartsWith("TABLE", StringComparison.Ordinal) || ps.Any(p => p.Direction != ParamDirection.In)),
            });
        }
        return list;
    }

    public override async Task<string> LoadRoutineSourceAsync(DbConnection c, DbRoutine r, CancellationToken ct)
    {
        var def = S((await QueryAsync(c, "SELECT pg_get_functiondef(CAST(@id AS oid))", ct, ("@id", r.Id))).FirstOrDefault()?[0]).Trim();
        // CockroachDB writes a plain CREATE; running the script again must update the routine, not fail.
        if (System.Text.RegularExpressions.Regex.Match(def, @"^CREATE\s+(FUNCTION|PROCEDURE)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } m)
            def = "CREATE OR REPLACE " + m.Groups[1].Value + def[m.Length..];
        return def + ";";
    }

    public override string CallSql(DbRoutine r, IReadOnlyList<string> args)
    {
        if (r.Kind == DbRoutineKind.Function) return "SELECT * FROM " + NameOf(r) + "(" + string.Join(", ", args) + ")";
        // A procedure's OUT parameters still take a placeholder in CALL (PostgreSQL 14+).
        int i = 0;
        var all = r.Parameters.Select(p => p.Direction == ParamDirection.Out ? "NULL" : args[i++]);
        return "CALL " + NameOf(r) + "(" + string.Join(", ", all) + ")";
    }

    /// <summary>Overloads share a name, so DROP names the argument types.</summary>
    public override string DropSql(DbRoutine r) =>
        base.DropSql(r) + "(" + string.Join(", ", r.Parameters.Where(p => r.Kind == DbRoutineKind.Procedure || p.Direction != ParamDirection.Out).Select(p => p.DataType)) + ")";
}

public sealed partial class MySqlDialect
{
    public override async Task<List<DbRoutine>> LoadRoutinesAsync(DbConnection c, CancellationToken ct)
    {
        var current = (await QueryAsync(c, "SELECT DATABASE()", ct)).FirstOrDefault()?[0] as string;
        var scope = current is null ? $"ROUTINE_SCHEMA NOT IN {System}" : "ROUTINE_SCHEMA = DATABASE()";
        var heads = await QueryAsync(c, $"SELECT ROUTINE_SCHEMA, ROUTINE_NAME, ROUTINE_TYPE, DTD_IDENTIFIER FROM information_schema.ROUTINES WHERE {scope} ORDER BY 1, 2", ct);
        var args = await QueryAsync(c, $"""
            SELECT SPECIFIC_SCHEMA, SPECIFIC_NAME, ROUTINE_TYPE, PARAMETER_NAME, DTD_IDENTIFIER, PARAMETER_MODE
            FROM information_schema.PARAMETERS WHERE {scope.Replace("ROUTINE_SCHEMA", "SPECIFIC_SCHEMA")} AND ORDINAL_POSITION > 0
            ORDER BY 1, 2, ORDINAL_POSITION
            """, ct);
        return heads.Select(h => new DbRoutine
        {
            Schema = S(h[0]), Name = S(h[1]),
            Kind = S(h[2]) == "PROCEDURE" ? DbRoutineKind.Procedure : DbRoutineKind.Function,
            ReturnType = h[3] is null ? null : S(h[3]),
            Parameters = args.Where(a => S(a[0]) == S(h[0]) && S(a[1]) == S(h[1]) && S(a[2]) == S(h[2]))
                .Select(a => new DbParameter(S(a[3]), S(a[4]), S(a[5]) switch { "OUT" => ParamDirection.Out, "INOUT" => ParamDirection.InOut, _ => ParamDirection.In }))
                .ToList(),
        }).ToList();
    }

    /// <summary>The body has its own ';', so the script switches the console's delimiter while it is created.</summary>
    public override async Task<string> LoadRoutineSourceAsync(DbConnection c, DbRoutine r, CancellationToken ct)
    {
        var what = r.Kind == DbRoutineKind.Procedure ? "PROCEDURE" : "FUNCTION";
        var rows = await QueryAsync(c, $"SHOW CREATE {what} {Quote(r.Schema ?? "")}.{Quote(r.Name)}", ct);
        var create = S(rows.FirstOrDefault()?[2]);
        return $"DROP {what} IF EXISTS {NameOf(r)};\n\nDELIMITER $$\n{create}$$\nDELIMITER ;";
    }

    public override string CallSql(DbRoutine r, IReadOnlyList<string> args)
    {
        if (r.Kind == DbRoutineKind.Function) return "SELECT " + NameOf(r) + "(" + string.Join(", ", args) + ")";
        // OUT and INOUT go through session variables, read back after the call.
        var sb = new StringBuilder();
        var parts = new List<string>();
        int i = 0;
        foreach (var p in r.Parameters)
        {
            if (p.Direction == ParamDirection.In) { parts.Add(args[i++]); continue; }
            var v = "@" + p.Name;
            if (p.Direction == ParamDirection.InOut) sb.Append("SET ").Append(v).Append(" = ").Append(args[i++]).Append(";\n");
            parts.Add(v);
        }
        sb.Append("CALL ").Append(NameOf(r)).Append('(').Append(string.Join(", ", parts)).Append(')');
        var outs = r.Parameters.Where(p => p.Direction != ParamDirection.In).ToList();
        if (outs.Count > 0) sb.Append(";\nSELECT ").Append(string.Join(", ", outs.Select(p => $"@{p.Name} AS {Name(p.Name)}")));
        return sb.ToString();
    }
}

public sealed partial class SqlServerDialect
{
    private const string RoutineTypes = "('P', 'PC', 'FN', 'IF', 'TF', 'FS', 'FT')";

    public override async Task<List<DbRoutine>> LoadRoutinesAsync(DbConnection c, CancellationToken ct)
    {
        var heads = await QueryAsync(c, $"""
            SELECT s.name, o.name, o.type, CAST(o.object_id AS varchar(20))
            FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.type IN {RoutineTypes} AND o.is_ms_shipped = 0
            ORDER BY 1, 2
            """, ct);
        var args = await QueryAsync(c, $"""
            SELECT CAST(c.object_id AS varchar(20)), c.name, {TypeSql}, c.is_output, c.parameter_id
            FROM sys.parameters c JOIN sys.objects o ON o.object_id = c.object_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE o.type IN {RoutineTypes} AND o.is_ms_shipped = 0
            ORDER BY 1, c.parameter_id
            """, ct);
        var byId = args.GroupBy(r => S(r[0])).ToDictionary(g => g.Key, g => g.ToList());
        return heads.Select(h =>
        {
            var type = S(h[2]).Trim();
            var ps = byId.GetValueOrDefault(S(h[3])) ?? new();
            bool proc = type is "P" or "PC";
            return new DbRoutine
            {
                Schema = S(h[0]), Name = S(h[1]), Id = S(h[3]),
                Kind = proc ? DbRoutineKind.Procedure : DbRoutineKind.Function,
                // parameter_id 0 is a scalar function's return value.
                ReturnType = proc ? null : type is "IF" or "TF" or "FT" ? "TABLE" : ps.Where(p => Convert.ToInt32(p[4], CultureInfo.InvariantCulture) == 0).Select(p => S(p[2])).FirstOrDefault(),
                ReturnsTable = type is "IF" or "TF" or "FT",
                Parameters = ps.Where(p => Convert.ToInt32(p[4], CultureInfo.InvariantCulture) > 0)
                    .Select(p => new DbParameter(S(p[1]), S(p[2]), B(p[3]) ? ParamDirection.InOut : ParamDirection.In)).ToList(),
            };
        }).ToList();
    }

    /// <summary>The definition as written, turned into CREATE OR ALTER so running it again updates the routine.</summary>
    public override async Task<string> LoadRoutineSourceAsync(DbConnection c, DbRoutine r, CancellationToken ct)
    {
        var def = S((await QueryAsync(c, "SELECT OBJECT_DEFINITION(CAST(@id AS int))", ct, ("@id", r.Id))).FirstOrDefault()?[0]);
        return CreateOrAlter(def, DbKind.SqlServer).Trim() + "\nGO";
    }

    /// <summary>Inserts "OR ALTER" after the first CREATE that is code (not in a comment), if it is not there.</summary>
    internal static string CreateOrAlter(string def, DbKind kind)
    {
        var tokens = SqlTokenizer.Tokenize(def, kind);
        var words = tokens.Where(t => t.Kind is SqlTokenKind.Keyword or SqlTokenKind.Identifier).Take(3).ToList();
        if (words.Count < 2 || !words[0].Text(def).Equals("CREATE", StringComparison.OrdinalIgnoreCase)
            || words[1].Text(def).Equals("OR", StringComparison.OrdinalIgnoreCase)) return def;
        return def[..words[0].End] + " OR ALTER" + def[words[0].End..];
    }

    /// <summary>Scalar functions must be called with their schema, even dbo's; table ones and procedures need not.</summary>
    public override string CallSql(DbRoutine r, IReadOnlyList<string> args) =>
        r.Kind == DbRoutineKind.Function && !r.ReturnsTable && r.Schema is not null
            ? "SELECT " + Name(r.Schema) + "." + Name(r.Name) + "(" + string.Join(", ", args) + ")"
            : TsqlCall(this, r, args);
}

public sealed partial class OracleDialect
{
    public override async Task<List<DbRoutine>> LoadRoutinesAsync(DbConnection c, CancellationToken ct)
    {
        const string Owners = "SELECT username FROM all_users WHERE oracle_maintained = 'N'";
        var heads = await QueryAsync(c, $"""
            SELECT owner, object_name, object_type FROM all_objects
            WHERE object_type IN ('PROCEDURE', 'FUNCTION') AND owner IN ({Owners}) ORDER BY 1, 2
            """, ct);
        // Position 0 is a function's return value; a routine without arguments has one row with no name.
        var args = await QueryAsync(c, $"""
            SELECT owner, object_name, argument_name, data_type, in_out, position FROM all_arguments
            WHERE package_name IS NULL AND data_level = 0 AND owner IN ({Owners})
            ORDER BY owner, object_name, position
            """, ct);
        var byName = args.GroupBy(a => (S(a[0]), S(a[1]))).ToDictionary(g => g.Key, g => g.ToList());
        return heads.Select(h =>
        {
            var ps = byName.GetValueOrDefault((S(h[0]), S(h[1]))) ?? new();
            bool proc = S(h[2]) == "PROCEDURE";
            return new DbRoutine
            {
                Schema = S(h[0]), Name = S(h[1]),
                Kind = proc ? DbRoutineKind.Procedure : DbRoutineKind.Function,
                ReturnType = proc ? null : ps.Where(p => Convert.ToInt32(p[5], CultureInfo.InvariantCulture) == 0).Select(p => S(p[3])).FirstOrDefault(),
                Parameters = ps.Where(p => Convert.ToInt32(p[5], CultureInfo.InvariantCulture) > 0 && p[2] is not null)
                    .Select(p => new DbParameter(S(p[2]), S(p[3]), S(p[4]) switch { "OUT" => ParamDirection.Out, "IN/OUT" => ParamDirection.InOut, _ => ParamDirection.In }))
                    .ToList(),
            };
        }).ToList();
    }

    public override async Task<string> LoadRoutineSourceAsync(DbConnection c, DbRoutine r, CancellationToken ct)
    {
        var lines = await QueryAsync(c, "SELECT text FROM all_source WHERE owner = :s AND name = :n AND type = :t ORDER BY line", ct,
            ("s", r.Schema), ("n", r.Name), ("t", r.Kind == DbRoutineKind.Procedure ? "PROCEDURE" : "FUNCTION"));
        return "CREATE OR REPLACE " + string.Concat(lines.Select(l => S(l[0]))).TrimEnd() + "\n/";
    }

    public override string CallSql(DbRoutine r, IReadOnlyList<string> args)
    {
        if (r.Kind == DbRoutineKind.Function && r.Parameters.All(p => p.Direction == ParamDirection.In))
            return "SELECT " + NameOf(r) + "(" + string.Join(", ", args) + ") FROM dual";
        // OUT values land in variables: the user reads them (DBMS_OUTPUT is not shown) or extends the block.
        var declare = new StringBuilder();
        var body = new StringBuilder();
        var parts = new List<string>();
        int i = 0;
        foreach (var p in r.Parameters)
        {
            if (p.Direction == ParamDirection.In) { parts.Add(args[i++]); continue; }
            var v = "v_" + p.Name.ToLowerInvariant();
            declare.Append("  ").Append(v).Append(' ').Append(PlsqlType(p.DataType)).Append(";\n");
            if (p.Direction == ParamDirection.InOut) body.Append("  ").Append(v).Append(" := ").Append(args[i++]).Append(";\n");
            parts.Add(v);
        }
        if (r.Kind == DbRoutineKind.Function) declare.Append("  v_result ").Append(PlsqlType(r.ReturnType ?? "VARCHAR2")).Append(";\n");
        body.Append("  ").Append(r.Kind == DbRoutineKind.Function ? "v_result := " : "").Append(NameOf(r)).Append('(').Append(string.Join(", ", parts)).Append(");\n");
        return (declare.Length > 0 ? "DECLARE\n" + declare : "") + "BEGIN\n" + body + "END;\n/";
    }

    private static string PlsqlType(string t) => t is "VARCHAR2" or "NVARCHAR2" or "CHAR" or "RAW" ? t + "(4000)" : t;
}

public sealed partial class DuckDbDialect
{
    /// <summary>DuckDB's user code is macros: scalar ones and table ones.</summary>
    public override async Task<List<DbRoutine>> LoadRoutinesAsync(DbConnection c, CancellationToken ct)
    {
        var rows = await QueryAsync(c, """
            SELECT schema_name, function_name, function_type, parameters, macro_definition
            FROM duckdb_functions()
            WHERE database_name = current_database() AND NOT internal AND function_type IN ('macro', 'table_macro')
            ORDER BY 1, 2
            """, ct);
        return rows.Select(r => new DbRoutine
        {
            Schema = S(r[0]), Name = S(r[1]), Kind = DbRoutineKind.Function, Id = S(r[4]),
            ReturnsTable = S(r[2]) == "table_macro",
            ReturnType = S(r[2]) == "table_macro" ? "TABLE" : null,
            Parameters = Strings(r[3]).Select(p => new DbParameter(p, "", ParamDirection.In)).ToList(),
        }).ToList();
    }

    public override Task<string> LoadRoutineSourceAsync(DbConnection c, DbRoutine r, CancellationToken ct) =>
        Task.FromResult($"CREATE OR REPLACE MACRO {NameOf(r)}({string.Join(", ", r.Parameters.Select(p => p.Name))}) AS {(r.ReturnsTable ? "TABLE " : "")}{r.Id};");

    public override string CallSql(DbRoutine r, IReadOnlyList<string> args) =>
        (r.ReturnsTable ? "SELECT * FROM " : "SELECT ") + NameOf(r) + "(" + string.Join(", ", args) + ")";

    public override string DropSql(DbRoutine r) => (r.ReturnsTable ? "DROP MACRO TABLE " : "DROP MACRO ") + NameOf(r);
}

public sealed partial class SqliteDialect
{
    public override string CallSql(DbRoutine r, IReadOnlyList<string> args) => "SELECT " + NameOf(r) + "(" + string.Join(", ", args) + ")";
}
