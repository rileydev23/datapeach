namespace DataPeach.Core.Sql;

/// <summary>Keywords colored and completed in every dialect (a union; being lenient costs nothing here).</summary>
public static class SqlKeywords
{
    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        "ADD", "ALL", "ALTER", "ANALYZE", "AND", "ANY", "ARRAY", "AS", "ASC", "AUTO_INCREMENT", "AUTOINCREMENT",
        "BEGIN", "BETWEEN", "BIGINT", "BIGSERIAL", "BLOB", "BOOLEAN", "BOTH", "BY", "CASCADE", "CASE", "CAST", "CHAR",
        "CHARACTER", "CHECK", "COLLATE", "COLUMN", "COMMENT", "COMMIT", "CONFLICT", "CONSTRAINT", "COPY", "CREATE", "CROSS",
        "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "CURRENT_USER", "DATABASE", "DATE", "DATETIME", "DECIMAL",
        "DECLARE", "DEFAULT", "DEFERRABLE", "DELETE", "DESC", "DESCRIBE", "DISTINCT", "DO", "DOUBLE", "DROP", "ELSE",
        "END", "ENUM", "ESCAPE", "EXCEPT", "EXISTS", "EXPLAIN", "EXTENSION", "FALSE", "FETCH", "FILTER", "FIRST", "FLOAT",
        "FOR", "FOREIGN", "FROM", "FULL", "FUNCTION", "GENERATED", "GRANT", "GROUP", "HAVING", "IF", "ILIKE", "IN", "INDEX",
        "INNER", "INSERT", "INT", "INTEGER", "INTERSECT", "INTERVAL", "INTO", "IS", "ISNULL", "JOIN", "JSON", "JSONB", "KEY",
        "LANGUAGE", "LAST", "LATERAL", "LEADING", "LEFT", "LIKE", "LIMIT", "LOCK", "MATERIALIZED", "MERGE", "NATURAL",
        "NOT", "NOTHING", "NOTNULL", "NULL", "NULLS", "NUMERIC", "OFFSET", "ON", "ONLY", "OR", "ORDER", "OUTER", "OVER",
        "PARTITION", "PRAGMA", "PRIMARY", "PROCEDURE", "REAL", "RECURSIVE", "REFERENCES", "REINDEX", "RELEASE", "RENAME",
        "REPLACE", "RESTRICT", "RETURNING", "RETURNS", "REVOKE", "RIGHT", "ROLLBACK", "ROW", "ROWS", "SAVEPOINT", "SCHEMA",
        "SELECT", "SEQUENCE", "SERIAL", "SESSION", "SET", "SHOW", "SIMILAR", "SMALLINT", "SOME", "START", "TABLE",
        "TEMP", "TEMPORARY", "TEXT", "THEN", "TIME", "TIMESTAMP", "TIMESTAMPTZ", "TO", "TRAILING", "TRANSACTION",
        "TRIGGER", "TRUE", "TRUNCATE", "TYPE", "UNION", "UNIQUE", "UNSIGNED", "UPDATE", "USE", "USING", "UUID", "VACUUM",
        "VALUES", "VARCHAR", "VIEW", "WHEN", "WHERE", "WINDOW", "WITH", "WITHOUT",
    };

    /// <summary>Words that can never be a bare column or table name in generated SQL.</summary>
    public static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALL", "AND", "ANY", "AS", "ASC", "BETWEEN", "BY", "CASE", "CAST", "CHECK", "COLUMN", "CONSTRAINT", "CREATE",
        "CROSS", "DEFAULT", "DELETE", "DESC", "DISTINCT", "DROP", "ELSE", "END", "EXISTS", "FALSE", "FETCH", "FOR",
        "FOREIGN", "FROM", "FULL", "GRANT", "GROUP", "HAVING", "IN", "INDEX", "INNER", "INSERT", "INTO", "IS", "JOIN",
        "KEY", "LEFT", "LIKE", "LIMIT", "NOT", "NULL", "OFFSET", "ON", "OR", "ORDER", "OUTER", "PRIMARY", "REFERENCES",
        "RIGHT", "SELECT", "SET", "TABLE", "THEN", "TO", "TRUE", "UNION", "UNIQUE", "UPDATE", "USER", "USING", "VALUES",
        "WHEN", "WHERE", "WITH", "ORDER", "RANGE", "ROWS", "WINDOW", "OVER", "PARTITION",
    };

    /// <summary>Common functions offered by completion.</summary>
    public static readonly string[] Functions =
    {
        "avg", "coalesce", "concat", "count", "current_date", "current_timestamp", "date_trunc", "extract", "greatest",
        "group_concat", "ifnull", "least", "length", "lower", "max", "min", "now", "nullif", "replace", "round",
        "string_agg", "substr", "substring", "sum", "trim", "upper", "json_extract", "jsonb_build_object", "to_char",
    };
}
