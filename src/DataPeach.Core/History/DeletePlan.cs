using DataPeach.Core.Db;
using DataPeach.Core.Sql;

namespace DataPeach.Core.History;

/// <summary>
/// How a console statement that deletes rows gets its backup: the SELECT that reads the same rows first and the
/// COUNT shown in the confirmation. Only plain forms are backed up — DELETE [FROM] t [alias] [WHERE …] (with
/// MySQL's ORDER BY/LIMIT) and TRUNCATE [TABLE] t; anything else (USING, joins, RETURNING, CTEs, MERGE…) still runs
/// after confirming, with <see cref="Unsupported"/> saying why there is no backup. Pure.
/// </summary>
public sealed record DeletePlan(string? Schema, string Table, string? SelectSql, string? CountSql, bool IsTruncate, string? Unsupported)
{
    public bool CanBackup => SelectSql is not null;
    public string TableDisplay => Schema is null ? Table : Schema + "." + Table;

    private static readonly HashSet<string> Blocking = new(StringComparer.OrdinalIgnoreCase)
    {
        "USING", "JOIN", "RETURNING", "OUTPUT", "FROM", "TOP", "OPTION", "WITH",
    };

    /// <summary>True for statements that remove rows: DELETE, TRUNCATE, and a WITH or MERGE that deletes.</summary>
    public static bool IsDelete(string sql, DbKind kind)
    {
        var words = Words(sql, kind);
        if (words.Count == 0) return false;
        var first = words[0].Text.ToUpperInvariant();
        if (first is "DELETE" or "TRUNCATE") return true;
        return first is "WITH" or "MERGE" && words.Any(w => w.Text.Equals("DELETE", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The plan for <paramref name="sql"/>, or null when it does not delete.</summary>
    public static DeletePlan? For(string sql, DbKind kind, Dialect d, SchemaSnapshot schema)
    {
        if (!IsDelete(sql, kind)) return null;
        var tokens = SqlTokenizer.Tokenize(sql, kind).Where(t => t.Kind is not (SqlTokenKind.Whitespace or SqlTokenKind.Comment)).ToList();
        string Up(int i) => i < tokens.Count ? tokens[i].Text(sql).ToUpperInvariant() : "";
        var first = Up(0);
        if (first is "WITH" or "MERGE") return new DeletePlan(null, "?", null, null, false, Loc.T("history.unsupported.complex"));

        int at = 1;
        bool truncate = first == "TRUNCATE";
        if (truncate && Up(at) == "TABLE") at++;
        if (!truncate && Up(at) == "TOP") return new DeletePlan(null, "?", null, null, false, Loc.F("history.unsupported.word", "TOP"));
        if (!truncate && Up(at) == "FROM") at++;

        // The table: name(.name)*, quoted or not.
        var parts = new List<string>();
        int nameStart = at < tokens.Count ? tokens[at].Start : sql.Length, nameEnd = nameStart;
        while (at < tokens.Count && tokens[at].Kind is SqlTokenKind.Identifier or SqlTokenKind.QuotedIdentifier or SqlTokenKind.Keyword)
        {
            parts.Add(Unquote(tokens[at].Text(sql)));
            nameEnd = tokens[at].End;
            at++;
            if (at < tokens.Count && tokens[at].Kind == SqlTokenKind.Punctuation && sql[tokens[at].Start] == '.') at++;
            else break;
        }
        if (parts.Count == 0) return new DeletePlan(null, "?", null, null, truncate, Loc.T("history.unsupported.complex"));
        var tableSql = sql[nameStart..nameEnd];
        var table = parts[^1];
        var owner = parts.Count > 1 ? parts[^2] : null;
        var known = schema.Find(owner, table);
        string? schemaName = known?.Schema ?? owner;
        if (known is not null) table = known.Name;

        if (truncate)
        {
            var all = "SELECT * FROM " + tableSql;
            return new DeletePlan(schemaName, table, all, "SELECT COUNT(*) FROM " + tableSql, true, null);
        }

        // "DELETE t FROM t JOIN …" (MySQL, SQL Server): the first name is a target, not the table.
        int restAt = at;
        if (restAt < tokens.Count && !IsClause(Up(restAt)))
        {
            if (Up(restAt) == "AS") restAt++;
            if (restAt < tokens.Count && tokens[restAt].Kind is SqlTokenKind.Identifier or SqlTokenKind.QuotedIdentifier && !IsClause(Up(restAt))) restAt++;
        }
        // Only the statement's own clauses count: a subquery in the WHERE may have its FROM and JOINs.
        int depth = 0;
        for (int i = restAt; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == SqlTokenKind.Punctuation) depth += sql[tokens[i].Start] switch { '(' => 1, ')' => -1, _ => 0 };
            if (depth == 0 && tokens[i].Kind == SqlTokenKind.Keyword && Blocking.Contains(tokens[i].Text(sql)))
                return new DeletePlan(schemaName, table, null, null, false, Loc.F("history.unsupported.word", tokens[i].Text(sql).ToUpperInvariant()));
        }
        if (restAt < tokens.Count && !IsClause(Up(restAt)))
            return new DeletePlan(schemaName, table, null, null, false, Loc.T("history.unsupported.complex"));

        // FROM t alias WHERE …: the SELECT keeps the alias so the WHERE still refers to it.
        var fromPart = sql[nameStart..(restAt > at ? tokens[restAt - 1].End : nameEnd)];
        var rest = restAt < tokens.Count ? " " + sql[tokens[restAt].Start..].TrimEnd().TrimEnd(';') : "";
        var select = "SELECT * FROM " + fromPart + rest;
        bool limited = tokens.Skip(restAt).Any(t => t.Text(sql).ToUpperInvariant() is "LIMIT" or "ORDER");
        var count = limited ? "SELECT COUNT(*) FROM (" + select + ") dp_rows" : "SELECT COUNT(*) FROM " + fromPart + rest;
        return new DeletePlan(schemaName, table, select, count, false, null);
    }

    private static bool IsClause(string word) => word is "WHERE" or "ORDER" or "LIMIT" or "";

    private static string Unquote(string name) =>
        name.Length >= 2 && (name[0], name[^1]) is ('"', '"') or ('`', '`') or ('[', ']')
            ? name[1..^1].Replace(name[0] == '[' ? "]]" : new string(name[0], 2), name[0] == '[' ? "]" : name[0].ToString())
            : name;

    private static List<(string Text, SqlTokenKind Kind)> Words(string sql, DbKind kind) =>
        SqlTokenizer.Tokenize(sql, kind).Where(t => t.Kind is SqlTokenKind.Keyword or SqlTokenKind.Identifier).Select(t => (t.Text(sql), t.Kind)).ToList();
}
