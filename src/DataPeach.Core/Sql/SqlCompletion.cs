using DataPeach.Core.Db;

namespace DataPeach.Core.Sql;

public enum CompletionKind { Column, Table, View, Schema, Keyword, Function }

public sealed record CompletionItem(string Label, string Insert, CompletionKind Kind, string? Detail);

/// <summary>What Ctrl+Space (or typing) offers: items to replace <see cref="Length"/> characters at <see cref="Start"/>.</summary>
public sealed record CompletionResult(int Start, int Length, IReadOnlyList<CompletionItem> Items)
{
    public static readonly CompletionResult None = new(0, 0, Array.Empty<CompletionItem>());
}

/// <summary>
/// Completion from the cached schema, without a parser: the word under the caret, what precedes it (FROM/JOIN →
/// tables; "alias." → that table's columns) and the tables the statement already names, with their aliases. Pure.
/// </summary>
public static class SqlCompletion
{
    private static readonly HashSet<string> TableContext = new(StringComparer.OrdinalIgnoreCase)
        { "FROM", "JOIN", "UPDATE", "INTO", "TABLE", "DESCRIBE" };

    public static CompletionResult Suggest(string sql, int caret, SchemaSnapshot schema, Dialect dialect, int max = 200)
    {
        caret = Math.Clamp(caret, 0, sql.Length);
        var stmt = SqlStatements.At(sql, caret, dialect.Kind) is { } s && caret >= s.Start && caret <= s.End + 1
            ? s
            : new SqlStatement(caret, 0, "");

        // The word being typed, back to the previous non-identifier character.
        int start = caret;
        while (start > 0 && (char.IsLetterOrDigit(sql[start - 1]) || sql[start - 1] is '_' or '$')) start--;
        var prefix = sql[start..caret];

        // "x." or "schema.table." before it?
        string? qualifier = null;
        if (start > 0 && sql[start - 1] == '.')
        {
            int qEnd = start - 1, qStart = qEnd;
            if (qStart > 0 && sql[qStart - 1] is '"' or '`')
            {
                char q = sql[qStart - 1];
                int open = sql.LastIndexOf(q, qStart - 2 < 0 ? 0 : qStart - 2);
                if (open >= 0) { qualifier = sql[(open + 1)..(qStart - 1)]; }
            }
            else
            {
                while (qStart > 0 && (char.IsLetterOrDigit(sql[qStart - 1]) || sql[qStart - 1] is '_' or '$')) qStart--;
                qualifier = sql[qStart..qEnd];
            }
        }

        var scope = TablesInScope(stmt.Text, schema, dialect.Kind);
        var items = new List<CompletionItem>();

        if (qualifier is { Length: > 0 })
        {
            var table = scope.TryGetValue(qualifier, out var t) ? t : schema.Find(null, qualifier);
            if (table is not null)
                items.AddRange(table.Columns.Select(c => Column(c, dialect)));
            else if (schema.Schemas.Contains(qualifier, StringComparer.OrdinalIgnoreCase))
                items.AddRange(schema.Tables.Where(x => string.Equals(x.Schema, qualifier, StringComparison.OrdinalIgnoreCase)).Select(x => Table(x, dialect, qualified: false)));
            return Finish(start, caret - start, prefix, items, max);
        }

        var before = PreviousWord(sql, stmt.Start, start, dialect.Kind);
        if (before is not null && TableContext.Contains(before))
        {
            items.AddRange(schema.Tables.Select(x => Table(x, dialect, qualified: x.Schema != schema.DefaultSchema)));
            items.AddRange(schema.Schemas.Where(x => x != schema.DefaultSchema).Select(x => new CompletionItem(x, dialect.Name(x), CompletionKind.Schema, null)));
            return Finish(start, caret - start, prefix, items, max);
        }

        // Anywhere else: columns of the tables in the statement first, then everything that fits.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in scope.Values.Distinct())
            foreach (var c in table.Columns)
                if (seen.Add(c.Name)) items.Add(Column(c, dialect, table.Name));
        if (prefix.Length > 0)
        {
            items.AddRange(schema.Tables.Select(x => Table(x, dialect, qualified: x.Schema != schema.DefaultSchema)));
            items.AddRange(SqlKeywords.All.Select(k => new CompletionItem(k, k, CompletionKind.Keyword, null)));
            items.AddRange(SqlKeywords.Functions.Select(f => new CompletionItem(f, f, CompletionKind.Function, null)));
        }
        return Finish(start, caret - start, prefix, items, max);
    }

    private static CompletionItem Column(DbColumn c, Dialect d, string? table = null) =>
        new(c.Name, d.Name(c.Name), CompletionKind.Column, table is null ? c.DataType : $"{c.DataType} · {table}");

    private static CompletionItem Table(DbTable t, Dialect d, bool qualified) =>
        new(qualified ? t.Display : t.Name, qualified && t.Schema is not null ? d.Name(t.Schema) + "." + d.Name(t.Name) : d.Name(t.Name),
            t.Kind == DbObjectKind.View ? CompletionKind.View : CompletionKind.Table, t.Schema);

    /// <summary>Starts-with matches first, then contains; columns before tables before keywords; shorter names first.</summary>
    private static CompletionResult Finish(int start, int length, string prefix, List<CompletionItem> items, int max)
    {
        var ranked = items
            .Select(i => (i, score: prefix.Length == 0 ? 0
                : i.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? 0
                : i.Label.Contains(prefix, StringComparison.OrdinalIgnoreCase) ? 1 : -1))
            .Where(x => x.score >= 0)
            // Without a prefix columns keep the table's order; with one, the closest (shortest) match goes first.
            .OrderBy(x => x.score).ThenBy(x => (int)x.i.Kind)
            .ThenBy(x => prefix.Length == 0 ? 0 : x.i.Label.Length)
            .ThenBy(x => prefix.Length == 0 ? "" : x.i.Label, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.i)
            .DistinctBy(i => (i.Label, i.Kind))
            .Take(max)
            .ToList();
        return new CompletionResult(start, length, ranked);
    }

    /// <summary>The keyword or identifier right before position <paramref name="pos"/>, skipping blanks and comments.</summary>
    private static string? PreviousWord(string sql, int from, int pos, DbKind kind)
    {
        if (pos <= from) return null;
        var tokens = SqlTokenizer.Tokenize(sql[from..pos], kind);
        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            var t = tokens[i];
            if (t.Kind is SqlTokenKind.Whitespace or SqlTokenKind.Comment) continue;
            if (t.Kind is SqlTokenKind.Keyword or SqlTokenKind.Identifier) return sql.Substring(from + t.Start, t.Length);
            return null;
        }
        return null;
    }

    /// <summary>
    /// Tables named after FROM / JOIN / UPDATE / INTO (and comma lists after FROM), keyed by alias and by name.
    /// </summary>
    public static Dictionary<string, DbTable> TablesInScope(string stmt, SchemaSnapshot schema, DbKind kind)
    {
        var map = new Dictionary<string, DbTable>(StringComparer.OrdinalIgnoreCase);
        var toks = SqlTokenizer.Tokenize(stmt, kind).Where(t => t.Kind is not (SqlTokenKind.Whitespace or SqlTokenKind.Comment)).ToList();
        string Text(int i) => toks[i].Text(stmt);
        string Unquote(int i)
        {
            var s = Text(i);
            return toks[i].Kind == SqlTokenKind.QuotedIdentifier && s.Length >= 2 ? s[1..^1] : s;
        }
        bool IsName(int i) => i < toks.Count && toks[i].Kind is SqlTokenKind.Identifier or SqlTokenKind.QuotedIdentifier;
        bool Is(int i, string s) => i < toks.Count && Text(i).Equals(s, StringComparison.OrdinalIgnoreCase);

        for (int i = 0; i < toks.Count; i++)
        {
            if (toks[i].Kind != SqlTokenKind.Keyword || !TableContext.Contains(Text(i))) continue;
            bool fromList = Is(i, "FROM");
            int j = i + 1;
            while (true)
            {
                if (!IsName(j)) break;
                string? sch = null;
                var name = Unquote(j);
                if (Is(j + 1, ".") && IsName(j + 2)) { sch = name; name = Unquote(j + 2); j += 2; }
                j++;
                var table = schema.Find(sch, name);
                string? alias = null;
                if (Is(j, "AS") && IsName(j + 1)) { alias = Unquote(j + 1); j += 2; }
                else if (IsName(j)) { alias = Unquote(j); j++; }
                if (table is not null)
                {
                    map[table.Name] = table;
                    if (alias is not null) map[alias] = table;
                }
                if (fromList && Is(j, ",")) { j++; continue; }
                break;
            }
        }
        return map;
    }
}
