using System.Globalization;
using System.Runtime.InteropServices;

namespace DataPeach.Core;

/// <summary>
/// Interface language. Every visible string lives in <see cref="Strings"/> as an (English, Spanish) pair and is
/// looked up by key: <c>Loc.T("home.add")</c> in code, <c>{l:L home.add}</c> in XAML. The language is chosen once
/// at startup (saved choice, else the system language) and a change applies on the next launch.
/// </summary>
public static class Loc
{
    public static readonly (string Code, string Name)[] Languages = { ("en", "English"), ("es", "Español") };

    /// <summary>"en" or "es".</summary>
    public static string Current { get; private set; } = "en";
    public static bool IsSpanish => Current == "es";

    /// <summary><paramref name="saved"/>: "en", "es" or null/"auto" to follow Windows.</summary>
    public static void Init(string? saved) => Current = Resolve(saved, SystemLanguage());

    /// <summary><paramref name="system"/>: two-letter code of the Windows display language ("es", "en"…).</summary>
    public static string Resolve(string? saved, string system) =>
        saved is "en" or "es" ? saved : system == "es" ? "es" : "en";

    /// <summary>
    /// The app pins the invariant culture (Program.Main), so CultureInfo cannot tell the user's language: ask Windows
    /// for the display language instead.
    /// </summary>
    private static string SystemLanguage()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return (GetUserDefaultUILanguage() & 0x3FF) == 0x0A ? "es" : "en";
        }
        catch { /* not available: English */ }
        var lang = Environment.GetEnvironmentVariable("LANG") ?? "";
        return lang.StartsWith("es", StringComparison.OrdinalIgnoreCase) ? "es" : "en";
    }

    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    /// <summary>Test hook: switches the language without touching settings.</summary>
    internal static void Use(string code) => Current = code;

    public static string T(string key) =>
        Strings.All.TryGetValue(key, out var s) ? (IsSpanish ? s.Es : s.En) : key;

    public static string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);

    /// <summary>"1 file" / "3 files": picks the ".one" or ".many" variant of <paramref name="key"/>.</summary>
    public static string Plural(string key, long n) => F(n == 1 ? key + ".one" : key + ".many", n);
}
