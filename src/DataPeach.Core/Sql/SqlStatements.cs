namespace DataPeach.Core.Sql;

public readonly record struct SqlStatement(int Start, int Length, string Text)
{
    public int End => Start + Length;
}

/// <summary>Statements of a console: splitting, the one under the caret, and whether one writes. Pure.</summary>
public static class SqlStatements
{
    /// <summary>
    /// Statements separated by ';' (outside strings and comments), trimmed, without the ';'. Statements made only of
    /// comments and blanks are left out. Their tools add a separator line: GO in SQL Server and / in Oracle. A block
    /// with ';' inside (a procedure, trigger or function in SQL Server; PL/SQL in Oracle) only ends at that line, and
    /// keeps its ';'. MySQL scripts change the separator with a "DELIMITER $$" line, as its command-line client does.
    /// </summary>
    public static List<SqlStatement> Split(string sql, DbKind kind = DbKind.Postgres)
    {
        var result = new List<SqlStatement>();
        var tokens = SqlTokenizer.Tokenize(sql, kind);
        int first = -1, last = -1;
        bool block = false;
        string? delimiter = null; // MySQL: the one set by DELIMITER, when it is not ';'
        int skipTo = -1;
        void Flush()
        {
            if (first >= 0)
            {
                result.Add(new SqlStatement(first, last - first, sql.Substring(first, last - first)));
                first = last = -1;
            }
            block = false;
        }
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Start < skipTo) continue;
            if (IsSeparatorLine(sql, t, kind)) { Flush(); continue; }
            if (kind == DbKind.MySql && DelimiterLine(sql, t) is { } d)
            {
                Flush();
                delimiter = d.Delimiter == ";" ? null : d.Delimiter;
                skipTo = d.LineEnd;
                continue;
            }
            if (delimiter is not null)
            {
                // It may be glued to a word ("END$$": '$' is a word character), so look inside the token too.
                int at = t.Kind is SqlTokenKind.String or SqlTokenKind.Comment or SqlTokenKind.QuotedIdentifier
                    ? -1
                    : sql.IndexOf(delimiter, t.Start, Math.Max(t.Length, Math.Min(delimiter.Length, sql.Length - t.Start)), StringComparison.Ordinal);
                if (at >= 0)
                {
                    if (at > t.Start)
                    {
                        if (first < 0) first = t.Start;
                        last = at;
                    }
                    Flush();
                    skipTo = at + delimiter.Length;
                    continue;
                }
            }
            else if (t.Kind == SqlTokenKind.Punctuation && sql[t.Start] == ';' && !block) { Flush(); continue; }
            if (t.Kind is SqlTokenKind.Whitespace) continue;
            if (t.Kind is SqlTokenKind.Comment && first < 0) continue; // comments before a statement are not part of it
            if (first < 0)
            {
                first = t.Start;
                block = StartsBlock(sql, tokens, i, kind);
            }
            last = t.End;
        }
        Flush();
        // A trailing comment inside a statement's range is fine; one after it was already skipped above.
        return result;
    }

    private static bool IsSeparatorLine(string sql, SqlToken t, DbKind kind)
    {
        var sep = kind switch { DbKind.SqlServer or DbKind.Sybase => "GO", DbKind.Oracle => "/", _ => null };
        if (sep is null || t.Length != sep.Length || t.Kind is SqlTokenKind.String or SqlTokenKind.QuotedIdentifier or SqlTokenKind.Comment
            || !t.Text(sql).Equals(sep, StringComparison.OrdinalIgnoreCase)) return false;
        for (int i = t.Start - 1; i >= 0 && sql[i] != '\n'; i--) if (!char.IsWhiteSpace(sql[i])) return false;
        for (int i = t.End; i < sql.Length && sql[i] != '\n'; i++) if (!char.IsWhiteSpace(sql[i])) return false;
        return true;
    }

    /// <summary>A "DELIMITER x" line starting at token <paramref name="t"/>: the new delimiter and where the line ends.</summary>
    private static (string Delimiter, int LineEnd)? DelimiterLine(string sql, SqlToken t)
    {
        if (t.Kind is not (SqlTokenKind.Keyword or SqlTokenKind.Identifier) || !t.Text(sql).Equals("DELIMITER", StringComparison.OrdinalIgnoreCase)) return null;
        for (int i = t.Start - 1; i >= 0 && sql[i] != '\n'; i--) if (!char.IsWhiteSpace(sql[i])) return null;
        int end = sql.IndexOf('\n', t.End);
        if (end < 0) end = sql.Length;
        var rest = sql[t.End..end].Trim();
        int space = rest.IndexOfAny(new[] { ' ', '\t' });
        var d = space < 0 ? rest : rest[..space];
        return d.Length == 0 ? null : (d, end);
    }

    private static readonly HashSet<string> BlockObjects = new(StringComparer.OrdinalIgnoreCase)
    {
        "PROC", "PROCEDURE", "FUNCTION", "TRIGGER", "PACKAGE", "TYPE",
    };

    /// <summary>Whether the statement starting at token <paramref name="at"/> is a block that holds its own ';'.</summary>
    private static bool StartsBlock(string sql, List<SqlToken> tokens, int at, DbKind kind)
    {
        if (kind is not (DbKind.SqlServer or DbKind.Sybase or DbKind.Oracle)) return false;
        var words = new List<string>(6);
        for (int i = at; i < tokens.Count && words.Count < 6; i++)
        {
            var t = tokens[i];
            if (t.Kind is SqlTokenKind.Keyword or SqlTokenKind.Identifier) words.Add(t.Text(sql).ToUpperInvariant());
            else if (t.Kind is not (SqlTokenKind.Whitespace or SqlTokenKind.Comment)) break;
        }
        if (words.Count == 0) return false;
        // BEGIN alone is a transaction in T-SQL; an anonymous PL/SQL block in Oracle.
        if (kind == DbKind.Oracle && words[0] is "BEGIN" or "DECLARE") return true;
        if (words[0] is not ("CREATE" or "ALTER")) return false;
        int w = 1;
        if (w + 1 < words.Count && words[w] == "OR" && words[w + 1] is "REPLACE" or "ALTER") w += 2;
        if (w < words.Count && words[w] is "EDITIONABLE" or "NONEDITIONABLE") w++;
        return w < words.Count && BlockObjects.Contains(words[w]) && (kind == DbKind.Oracle || words[w] is not ("PACKAGE" or "TYPE"));
    }

    /// <summary>
    /// The statement Ctrl+Enter runs: the one containing the caret, or else the closest one before it (the caret
    /// usually sits right after the ';' or on the blank line that follows), or else the next one.
    /// </summary>
    public static SqlStatement? At(string sql, int caret, DbKind kind = DbKind.Postgres)
    {
        var all = Split(sql, kind);
        if (all.Count == 0) return null;
        foreach (var s in all) if (caret >= s.Start && caret <= s.End) return s;
        SqlStatement? before = null;
        foreach (var s in all)
        {
            if (s.End > caret) break;
            before = s;
        }
        if (before is { } b)
        {
            // Only if nothing but blanks, ';' and comments lie between it and the caret on the same "paragraph".
            var gap = sql.AsSpan(b.End, caret - b.End);
            if (!gap.Contains("\n\n".AsSpan(), StringComparison.Ordinal) && !gap.Contains("\r\n\r\n".AsSpan(), StringComparison.Ordinal)) return b;
        }
        var next = all.FirstOrDefault(s => s.Start >= caret);
        if (next.Length > 0) return next;
        return before ?? all[^1];
    }

    private static readonly HashSet<string> ReadOnlyStarts = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "SHOW", "EXPLAIN", "DESCRIBE", "DESC", "VALUES", "TABLE", "WITH",
    };

    private static readonly HashSet<string> WritingWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "DROP", "ALTER", "CREATE", "GRANT", "REVOKE", "REPLACE", "INTO",
    };

    /// <summary>
    /// True when the statement may change data or structure. Conservative: anything not recognized as a read counts
    /// as a write, and a WITH or SELECT that contains INSERT/UPDATE/DELETE/… (or SELECT … INTO) too.
    /// </summary>
    public static bool IsWrite(string statement, DbKind kind = DbKind.Postgres)
    {
        var words = SqlTokenizer.Tokenize(statement, kind)
            .Where(t => t.Kind is SqlTokenKind.Keyword or SqlTokenKind.Identifier)
            .Select(t => t.Text(statement))
            .ToList();
        if (words.Count == 0) return false;
        var first = words[0];
        if (first.Equals("PRAGMA", StringComparison.OrdinalIgnoreCase)) return statement.Contains('=');
        if (first.Equals("EXPLAIN", StringComparison.OrdinalIgnoreCase))
            return words.Any(w => w.Equals("ANALYZE", StringComparison.OrdinalIgnoreCase)) && words.Skip(1).Any(WritingWords.Contains);
        if (!ReadOnlyStarts.Contains(first)) return true;
        return words.Skip(1).Any(w => WritingWords.Contains(w) && !w.Equals("REPLACE", StringComparison.OrdinalIgnoreCase));
    }
}
