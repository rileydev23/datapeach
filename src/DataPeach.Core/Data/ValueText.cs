using System.Globalization;
using System.Text;

namespace DataPeach.Core.Data;

/// <summary>How cell values become text (grid, copy, exports, edit box) and how edited text becomes a value. Pure.</summary>
public static class ValueText
{
    public const string Null = "NULL";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Full text of a value, as the edit box and "copy" use it. Null for SQL NULL.</summary>
    public static string? Full(object? v) => v switch
    {
        null or DBNull => null,
        string s => s,
        bool b => b ? "true" : "false",
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        DateTime d => d.TimeOfDay == TimeSpan.Zero && d.Kind != DateTimeKind.Utc ? d.ToString("yyyy-MM-dd", Inv) : d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", Inv).TrimEnd('.'),
        DateTimeOffset o => o.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", Inv),
        DateOnly d => d.ToString("yyyy-MM-dd", Inv),
        TimeOnly t => t.ToString("HH:mm:ss.FFFFFFF", Inv).TrimEnd('.'),
        TimeSpan t => t.ToString("c", Inv),
        float f => f.ToString("R", Inv),
        double d => d.ToString("R", Inv),
        IFormattable f => f.ToString(null, Inv),
        // A DuckDB STRUCT or MAP arrives as a dictionary.
        System.Collections.IDictionary map => "{" + string.Join(", ", map.Keys.Cast<object>().Select(k => Full(k) + ": " + (Full(map[k]) ?? Null))) + "}",
        System.Collections.IEnumerable list => "{" + string.Join(", ", list.Cast<object?>().Select(x => Full(x) ?? Null)) + "}",
        _ => v.ToString(),
    };

    /// <summary>
    /// One-line text for a grid cell: line breaks become ⏎ and long values are cut, so drawing never depends on how
    /// big the value is. Null for SQL NULL.
    /// </summary>
    public static string? Cell(object? v, int max = 300)
    {
        if (v is byte[] bytes)
        {
            var hex = Convert.ToHexString(bytes, 0, Math.Min(bytes.Length, 64));
            return "0x" + hex + (bytes.Length > 64 ? $"… ({bytes.Length:N0} B)" : "");
        }
        var s = Full(v);
        if (s is null) return null;
        if (s.Length > max) s = s[..max] + "…";
        if (s.AsSpan().IndexOfAny('\r', '\n', '\t') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\r') { if (i + 1 < s.Length && s[i + 1] == '\n') i++; sb.Append('⏎'); }
            else if (c == '\n') sb.Append('⏎');
            else if (c == '\t') sb.Append(' ');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Numbers are right-aligned in the grid.</summary>
    public static bool IsNumeric(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t == typeof(sbyte)
        || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort)
        || t == typeof(decimal) || t == typeof(double) || t == typeof(float);

    /// <summary>
    /// Edited text → value to send for a column of CLR type <paramref name="clr"/>. Booleans and numbers are parsed so
    /// engines with loose typing store the right thing; everything else goes as text (PostgreSQL casts it, see
    /// <see cref="Db.Dialect.Param"/>). Returns false when the text does not fit the type.
    /// </summary>
    public static bool TryParse(string text, Type clr, out object? value)
    {
        value = text;
        var t = text.Trim();
        if (clr == typeof(bool))
        {
            switch (t.ToLowerInvariant())
            {
                case "true" or "1" or "t" or "yes" or "y": value = true; return true;
                case "false" or "0" or "f" or "no" or "n": value = false; return true;
                default: return false;
            }
        }
        if (clr == typeof(int) || clr == typeof(short) || clr == typeof(byte) || clr == typeof(sbyte) || clr == typeof(long)
            || clr == typeof(uint) || clr == typeof(ushort) || clr == typeof(ulong))
        {
            if (!long.TryParse(t, NumberStyles.Integer, Inv, out var l)) return false;
            value = l;
            return true;
        }
        if (clr == typeof(decimal))
        {
            if (!decimal.TryParse(t, NumberStyles.Number | NumberStyles.AllowExponent, Inv, out var d)) return false;
            value = d;
            return true;
        }
        if (clr == typeof(double) || clr == typeof(float))
        {
            if (!double.TryParse(t, NumberStyles.Float, Inv, out var d)) return false;
            value = d;
            return true;
        }
        if (clr == typeof(byte[]))
        {
            var hex = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? t[2..] : t;
            try { value = Convert.FromHexString(hex); return true; }
            catch (FormatException) { return false; }
        }
        return true;
    }

    /// <summary>Stable text of a row key, used to recognize the same row across reloads.</summary>
    public static string Key(IEnumerable<object?> values) =>
        string.Join("\u001f", values.Select(v => v is null or DBNull ? "\u0000" : Full(v)));
}
