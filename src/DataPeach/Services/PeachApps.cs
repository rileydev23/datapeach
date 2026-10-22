using System.Text.Json;

namespace DataPeach.Services;

/// <summary>
/// Shared home of the Peach apps: <c>%APPDATA%\peachapps\&lt;App&gt;\</c>. Each app keeps its settings there
/// and, every time it starts, writes <c>location.json</c> with the path it is running from. That lets the
/// apps find each other without installers, registry keys or a fixed folder layout.
/// The same file (with another namespace) lives in every Peach app; keep them in sync.
/// </summary>
public static class PeachApps
{
    public const string LocationFile = "location.json";

    public static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "peachapps");

    public static string FolderOf(string app) => Path.Combine(Root, app);

    /// <summary>
    /// Settings folder for <paramref name="app"/>: <c>.\data</c> next to the exe in portable mode, otherwise the
    /// shared folder. Settings from the old location (<c>%APPDATA%\&lt;App&gt;</c>) are moved over once.
    /// </summary>
    public static string SettingsFolder(string app)
    {
        var exeDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(exeDir, "portable.txt"))) return Path.Combine(exeDir, "data");
        var dir = FolderOf(app);
        Directory.CreateDirectory(dir);
        MigrateLegacy(app, dir);
        return dir;
    }

    private static void MigrateLegacy(string app, string dir)
    {
        try
        {
            var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), app);
            if (!Directory.Exists(legacy)) return;
            foreach (var file in Directory.GetFiles(legacy))
            {
                var target = Path.Combine(dir, Path.GetFileName(file));
                if (!File.Exists(target)) File.Move(file, target);
            }
            if (!Directory.EnumerateFileSystemEntries(legacy).Any()) Directory.Delete(legacy);
        }
        catch { /* best effort: the app simply starts with fresh settings */ }
    }

    /// <summary>Records where this app is running from (best effort, also in portable mode).</summary>
    public static void Announce(string app)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var dir = FolderOf(app);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, LocationFile);
            var tmp = file + ".tmp";
            using (var stream = File.Create(tmp))
            using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteString("exe", exe);
                w.WriteString("version", typeof(PeachApps).Assembly.GetName().Version?.ToString(3));
                w.WriteString("lastRun", DateTimeOffset.Now);
                w.WriteEndObject();
            }
            File.Move(tmp, file, true);
        }
        catch { /* not being discoverable is not worth an error */ }
    }

    /// <summary>Executable of another Peach app as it last announced itself, if that file still exists.</summary>
    public static string? Locate(string app)
    {
        try
        {
            var file = Path.Combine(FolderOf(app), LocationFile);
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            var exe = doc.RootElement.TryGetProperty("exe", out var e) ? e.GetString() : null;
            return !string.IsNullOrEmpty(exe) && File.Exists(exe) ? exe : null;
        }
        catch { return null; }
    }
}
