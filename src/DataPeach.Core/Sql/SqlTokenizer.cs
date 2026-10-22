namespace DataPeach.Core.Sql;

public enum SqlTokenKind
{
    Whitespace, Comment, Keyword, Identifier, QuotedIdentifier, String, Number, Operator, Punctuation, Parameter, Unknown,
}

public readonly record struct SqlToken(SqlTokenKind Kind, int Start, int Length)
{
    public int End => Start + Length;
    public string Text(string sql) => sql.Substring(Start, Length);
}

/// <summary>
/// A small SQL lexer good enough for coloring, splitting statements and completion, for the dialects DataPeach
/// supports: '' strings (with E'' and backslash escapes), PostgreSQL dollar quoting, "", `` and [] identifiers,
/// -- / nested /* */ comments and :name / @name / $1 / ? parameters. MySQL adds # comments and backslash escapes in
/// every string. Never throws; unknown input becomes <see cref="SqlTokenKind.Unknown"/>.
/// </summary>
public static class SqlTokenizer
{
    public static List<SqlToken> Tokenize(string sql, DbKind dialect = DbKind.Postgres)
    {
        bool mysql = dialect == DbKind.MySql;
        // $$…$$ strings are PostgreSQL's (and DuckDB's); in MySQL $$ is the usual DELIMITER of procedure scripts.
        bool dollarQuotes = dialect is DbKind.Postgres or DbKind.DuckDb;
        var list = new List<SqlToken>(sql.Length / 4);
        int i = 0, n = sql.Length;
        while (i < n)
        {
            int start = i;
            char c = sql[i];
            SqlTokenKind kind;
            if (char.IsWhiteSpace(c))
            {
                while (i < n && char.IsWhiteSpace(sql[i])) i++;
                kind = SqlTokenKind.Whitespace;
            }
            else if (c == '-' && At(sql, i + 1) == '-' || (mysql && c == '#'))
            {
                while (i < n && sql[i] != '\n') i++;
                kind = SqlTokenKind.Comment;
            }
            else if (c == '/' && At(sql, i + 1) == '*')
            {
                i += 2;
                int depth = 1;
                while (i < n && depth > 0)
                {
                    if (sql[i] == '/' && At(sql, i + 1) == '*') { depth++; i += 2; }
                    else if (sql[i] == '*' && At(sql, i + 1) == '/') { depth--; i += 2; }
                    else i++;
                }
                kind = SqlTokenKind.Comment;
            }
            else if (c == '\'' || ((c is 'E' or 'e' or 'N' or 'n' or 'X' or 'x' or 'B' or 'b') && At(sql, i + 1) == '\'' && !IsWordChar(At(sql, i - 1))))
            {
                bool backslash = mysql || c is 'E' or 'e';
                if (c != '\'') i++;
                i = SkipQuoted(sql, i, '\'', backslash);
                kind = SqlTokenKind.String;
            }
            else if (c == '"' || c == '`')
            {
                i = SkipQuoted(sql, i, c, false);
                kind = SqlTokenKind.QuotedIdentifier;
            }
            else if (c == '[' && LooksLikeBracketName(sql, i))
            {
                int close = sql.IndexOf(']', i + 1);
                i = close < 0 ? n : close + 1;
                kind = SqlTokenKind.QuotedIdentifier;
            }
            else if (c == '$' && dollarQuotes && DollarTag(sql, i) is { } tag)
            {
                int close = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                i = close < 0 ? n : close + tag.Length;
                kind = SqlTokenKind.String;
            }
            else if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(At(sql, i + 1))))
            {
                if (c == '0' && At(sql, i + 1) is 'x' or 'X')
                {
                    i += 2;
                    while (i < n && char.IsAsciiHexDigit(sql[i])) i++;
                }
                else
                {
                    while (i < n && (char.IsAsciiDigit(sql[i]) || sql[i] == '_')) i++;
                    if (i < n && sql[i] == '.' && At(sql, i + 1) != '.') { i++; while (i < n && char.IsAsciiDigit(sql[i])) i++; }
                    if (i < n && sql[i] is 'e' or 'E' && (char.IsAsciiDigit(At(sql, i + 1)) || (At(sql, i + 1) is '+' or '-' && char.IsAsciiDigit(At(sql, i + 2)))))
                    {
                        i += 2;
                        while (i < n && char.IsAsciiDigit(sql[i])) i++;
                    }
                }
                kind = SqlTokenKind.Number;
            }
            else if (IsWordStart(c))
            {
                while (i < n && IsWordChar(sql[i])) i++;
                kind = SqlKeywords.All.Contains(sql.Substring(start, i - start)) ? SqlTokenKind.Keyword : SqlTokenKind.Identifier;
            }
            else if ((c is ':' or '@' && IsWordStart(At(sql, i + 1)) && At(sql, i - 1) != ':') || (c == '$' && char.IsAsciiDigit(At(sql, i + 1))))
            {
                i++;
                while (i < n && IsWordChar(sql[i])) i++;
                kind = SqlTokenKind.Parameter;
            }
            else if (c == '?')
            {
                i++;
                kind = SqlTokenKind.Parameter;
            }
            else if (c is '(' or ')' or ',' or ';' or '.')
            {
                i++;
                kind = SqlTokenKind.Punctuation;
            }
            else if ("+-*/%=<>!|&^~:".Contains(c))
            {
                while (i < n && "+-*/%=<>!|&^~:".Contains(sql[i]) && !(sql[i] == '-' && At(sql, i + 1) == '-') && !(sql[i] == '/' && At(sql, i + 1) == '*')) i++;
                if (i == start) i++;
                kind = SqlTokenKind.Operator;
            }
            else
            {
                i++;
                kind = SqlTokenKind.Unknown;
            }
            list.Add(new SqlToken(kind, start, i - start));
        }
        return list;
    }

    private static char At(string s, int i) => i >= 0 && i < s.Length ? s[i] : '\0';
    private static bool IsWordStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    /// <summary>Index after the closing quote; a doubled quote is an escaped one. Unclosed runs to the end.</summary>
    private static int SkipQuoted(string s, int i, char q, bool backslash)
    {
        i++;
        while (i < s.Length)
        {
            char c = s[i];
            if (backslash && c == '\\') { i += 2; continue; }
            if (c == q)
            {
                if (At(s, i + 1) == q) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return s.Length;
    }

    /// <summary>"$$" or "$tag$" starting at <paramref name="i"/>, or null.</summary>
    private static string? DollarTag(string s, int i)
    {
        int j = i + 1;
        while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
        if (j >= s.Length || s[j] != '$') return null;
        if (j > i + 1 && char.IsAsciiDigit(s[i + 1])) return null; // $1 is a parameter
        return s.Substring(i, j - i + 1);
    }

    /// <summary>[name] is a SQL Server/SQLite identifier; elsewhere '[' is an array subscript.</summary>
    private static bool LooksLikeBracketName(string s, int i)
    {
        int close = s.IndexOf(']', i + 1);
        if (close < 0 || close == i + 1) return false;
        for (int k = i + 1; k < close; k++) if (s[k] == '\n') return false;
        return !char.IsAsciiDigit(s[i + 1]) && !IsWordChar(At(s, i - 1));
    }
}
