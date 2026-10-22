using System.Globalization;
using System.Text;
using System.Text.Json;
using DataPeach.Core.Db;

namespace DataPeach.Core.Data;

public enum ExportFormat { Tsv, Csv, Json, Markdown, Insert, TypeScript }

/// <summary>Rows (or a selection of them) as text for the clipboard or a file. Pure.</summary>
public static class Exporters
{
    public static string Export(ExportFormat f, IReadOnlyList<ResultColumn> cols, IEnumerable<object?[]> rows, Dialect d, string tableName = "table") => f switch
    {
        ExportFormat.Tsv => Separated(cols, rows, '\t', header: true),
        ExportFormat.Csv => Separated(cols, rows, ',', header: true),
        ExportFormat.Json => Json(cols, rows),
        ExportFormat.Markdown => Markdown(cols, rows),
        ExportFormat.Insert => Inserts(cols, rows, d, tableName),
        ExportFormat.TypeScript => TypeScript(cols, tableName),
        _ => "",
    };

    public static string FileExtension(ExportFormat f) => f switch
    {
        ExportFormat.Tsv => "tsv",
        ExportFormat.Csv => "csv",
        ExportFormat.Json => "json",
        ExportFormat.Markdown => "md",
        ExportFormat.Insert => "sql",
        _ => "ts",
    };

    public static string Separated(IReadOnlyList<ResultColumn> cols, IEnumerable<object?[]> rows, char sep, bool header)
    {
        var sb = new StringBuilder();
        string Field(string? s)
        {
            if (s is null) return "";
            bool quote = s.Contains(sep) || s.Contains('"') || s.Contains('\n') || s.Contains('\r');
            return quote ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
        if (header) sb.AppendJoin(sep, cols.Select(c => Field(c.Name))).Append("\r\n");
        foreach (var r in rows) sb.AppendJoin(sep, r.Select(v => Field(ValueText.Full(v)))).Append("\r\n");
        return sb.ToString();
    }

    public static string Json(IReadOnlyList<ResultColumn> cols, IEnumerable<object?[]> rows)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartArray();
            foreach (var r in rows)
            {
                w.WriteStartObject();
                for (int i = 0; i < cols.Count; i++)
                {
                    w.WritePropertyName(cols[i].Name);
                    WriteValue(w, r[i]);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter w, object? v)
    {
        switch (v)
        {
            case null or DBNull: w.WriteNullValue(); break;
            case bool b: w.WriteBooleanValue(b); break;
            case int or long or short or byte or sbyte or uint or ushort: w.WriteNumberValue(Convert.ToInt64(v, CultureInfo.InvariantCulture)); break;
            case ulong u: w.WriteNumberValue(u); break;
            case decimal m: w.WriteNumberValue(m); break;
            case double d when double.IsFinite(d): w.WriteNumberValue(d); break;
            case float f when float.IsFinite(f): w.WriteNumberValue(f); break;
            default: w.WriteStringValue(ValueText.Full(v)); break;
        }
    }

    public static string Markdown(IReadOnlyList<ResultColumn> cols, IEnumerable<object?[]> rows)
    {
        static string Cell(string? s) => (s ?? "NULL").Replace("|", "\\|").Replace("\r\n", "<br>").Replace("\n", "<br>");
        var sb = new StringBuilder();
        sb.Append("| ").AppendJoin(" | ", cols.Select(c => Cell(c.Name))).Append(" |\n");
        sb.Append('|').AppendJoin("|", cols.Select(c => ValueText.IsNumeric(c.ClrType) ? " ---: " : " --- ")).Append("|\n");
        foreach (var r in rows) sb.Append("| ").AppendJoin(" | ", r.Select(v => Cell(ValueText.Full(v)))).Append(" |\n");
        return sb.ToString();
    }

    public static string Inserts(IReadOnlyList<ResultColumn> cols, IEnumerable<object?[]> rows, Dialect d, string table)
    {
        var sb = new StringBuilder();
        var head = $"INSERT INTO {table} ({string.Join(", ", cols.Select(c => d.Name(c.Name)))}) VALUES (";
        foreach (var r in rows) sb.Append(head).AppendJoin(", ", r.Select(d.Literal)).Append(");\n");
        return sb.ToString();
    }

    /// <summary>An interface for the result's columns, named after the table (users → User).</summary>
    public static string TypeScript(IReadOnlyList<ResultColumn> cols, string table)
    {
        var sb = new StringBuilder();
        sb.Append("export interface ").Append(TypeName(table)).Append(" {\n");
        foreach (var c in cols)
        {
            var name = IsTsIdentifier(c.Name) ? c.Name : JsonSerializer.Serialize(c.Name, CoreJson.Default.String);
            sb.Append("  ").Append(name).Append(": ").Append(TsType(c.ClrType)).Append(" | null;\n");
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    public static string TsType(Type t) =>
        t == typeof(bool) ? "boolean"
        : ValueText.IsNumeric(t) && t != typeof(long) && t != typeof(ulong) && t != typeof(decimal) ? "number"
        : t == typeof(long) || t == typeof(ulong) ? "number" // JSON drivers send them as numbers; switch to string if > 2^53
        : t == typeof(decimal) ? "string"
        : t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(DateOnly) ? "string"
        : t == typeof(byte[]) ? "Uint8Array"
        : t.IsArray ? TsType(t.GetElementType()!) + "[]"
        : t == typeof(string) || t == typeof(Guid) || t == typeof(TimeSpan) || t == typeof(TimeOnly) ? "string"
        : "unknown";

    private static bool IsTsIdentifier(string s) =>
        s.Length > 0 && (char.IsLetter(s[0]) || s[0] is '_' or '$') && s.All(c => char.IsLetterOrDigit(c) || c is '_' or '$');

    public static string TypeName(string table)
    {
        var name = table.Split('.').Last().Trim('"', '`', '[', ']');
        var parts = name.Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var pascal = string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        if (pascal.Length == 0 || !char.IsLetter(pascal[0])) pascal = "Row" + pascal;
        // Plural table names usually describe one row: "users" → "User", "categories" → "Category".
        if (pascal.EndsWith("ies") && pascal.Length > 4) pascal = pascal[..^3] + "y";
        else if (pascal.EndsWith("ses") || pascal.EndsWith("xes")) pascal = pascal[..^2];
        else if (pascal.EndsWith('s') && !pascal.EndsWith("ss") && pascal.Length > 2) pascal = pascal[..^1];
        return pascal;
    }
}
