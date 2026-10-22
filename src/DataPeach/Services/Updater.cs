using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace DataPeach.Services;

/// <summary>A newer release than the running one, with the download that fits this computer.</summary>
public sealed record UpdateInfo(Version Version, string Notes, string PageUrl, string AssetUrl, string? Sha256);

/// <summary>
/// New versions from the GitHub releases of the repository. Nothing is downloaded until the user confirms; then the
/// running executable is replaced in place (Windows allows renaming an exe in use, Linux replacing it) or, when it
/// was installed with datapeach-windows-x64-setup.exe, the new setup runs silently so the installed-apps entry stays right.
/// Self-contained so it can be copied to the other Peach apps (change <see cref="Repo"/> and <see cref="App"/>).
/// </summary>
public static class Updater
{
    public const string App = "DataPeach";
    public const string Repo = "rileydev23/datapeach";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases/latest";

    /// <summary>Marker the installers leave next to the executable.</summary>
    public const string InstalledMarker = "installed.txt";

    private const string OldSuffix = ".old";
    private const string NewSuffix = ".new";

    public static Version Current
    {
        get
        {
#if DEBUG
            // DATAPEACH_FAKE_VERSION=0.0.1 makes a Debug run see the latest release as new.
            if (Version.TryParse(Environment.GetEnvironmentVariable("DATAPEACH_FAKE_VERSION"), out var fake)) return fake;
#endif
            return Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));
        }
    }

    public static string? ExePath => Environment.ProcessPath;

    public static bool IsInstalled =>
        ExePath is { } exe && File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, InstalledMarker));

    /// <summary>Release asset for this computer: the setup when installed on Windows, otherwise the portable build.</summary>
    public static string AssetName(bool windows, bool installed)
    {
        var low = App.ToLowerInvariant();
        return windows ? $"{low}-windows-x64-{(installed ? "setup" : "portable")}.exe" : $"{low}-linux-x64.tar.gz";
    }

    /// <summary>The latest release if it is newer than <paramref name="current"/>; null when up to date.</summary>
    public static async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        using var http = Client(TimeSpan.FromSeconds(10));
        var json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        return Parse(json, current, AssetName(OperatingSystem.IsWindows(), IsInstalled));
    }

    /// <summary>Reads a GitHub "release" JSON. Null when it is not newer or has no asset named <paramref name="asset"/>.</summary>
    public static UpdateInfo? Parse(string json, Version current, string asset)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!TryParseTag(root.GetProperty("tag_name").GetString(), out var version) || version <= Normalize(current)) return null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            if (a.GetProperty("name").GetString() != asset) continue;
            string? sha = null;
            if (a.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:"))
                sha = digest["sha256:".Length..];
            return new UpdateInfo(version,
                root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                root.GetProperty("html_url").GetString() ?? ReleasesPage,
                a.GetProperty("browser_download_url").GetString()!,
                sha);
        }
        return null;
    }

    /// <summary>"v1.2.3" or "1.2.3" → 1.2.3.</summary>
    public static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        if (!Version.TryParse(tag.Trim().TrimStart('v', 'V'), out var v)) return false;
        version = Normalize(v);
        return true;
    }

    /// <summary>Three parts, so 1.2.3 and 1.2.3.0 (the assembly version) compare equal.</summary>
    private static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    /// <summary>
    /// Downloads and puts the update in place. Returns the process to start afterwards (the new exe, or the setup);
    /// the caller then closes the app.
    /// </summary>
    public static async Task<ProcessStartInfo> ApplyAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken ct = default)
    {
        var exe = ExePath ?? throw new InvalidOperationException("Unknown executable path.");
        var dir = Path.GetDirectoryName(exe)!;
        var pid = Environment.ProcessId.ToString();

        if (OperatingSystem.IsWindows() && IsInstalled)
        {
            var setup = Path.Combine(Path.GetTempPath(), $"{App}-Setup-{update.Version}.exe");
            await DownloadAsync(update, setup, progress, ct);
            return new ProcessStartInfo(setup) { ArgumentList = { "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS" } };
        }

        // Downloaded next to the exe: the final rename stays on the same disk, and a folder we cannot write to fails
        // here, before anything changed.
        var download = exe + NewSuffix;
        try
        {
            await DownloadAsync(update, download, progress, ct);
            if (!OperatingSystem.IsWindows())
            {
                var binary = exe + ".bin" + NewSuffix;
                ExtractBinary(download, binary);
                File.Delete(download);
                download = binary;
            }
            ReplaceRunning(exe, download);
        }
        finally
        {
            TryDelete(download);
        }
        return new ProcessStartInfo(exe) { ArgumentList = { "--updated", pid }, WorkingDirectory = dir };
    }

    /// <summary>
    /// Puts <paramref name="newFile"/> where <paramref name="exe"/> is while it runs. Windows: the running exe is
    /// renamed to .old (allowed, unlike overwriting it) and removed on the next start. Elsewhere a rename over it is
    /// atomic and the running process keeps the old file.
    /// </summary>
    public static void ReplaceRunning(string exe, string newFile)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(newFile, (UnixFileMode)0b111_101_101); // 755
            File.Move(newFile, exe, true);
            return;
        }
        var old = exe + OldSuffix;
        TryDelete(old);
        File.Move(exe, old);
        try { File.Move(newFile, exe); }
        catch
        {
            File.Move(old, exe); // put it back: never leave the folder without an exe
            throw;
        }
    }

    /// <summary>Starts what <see cref="ApplyAsync"/> returned (the new exe or the setup); the caller then closes.</summary>
    public static void Launch(ProcessStartInfo next) => Process.Start(next);

    /// <summary>Takes the executable out of the Linux release archive.</summary>
    public static void ExtractBinary(string tarGz, string target)
    {
        using var file = File.OpenRead(tarGz);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
            if (Path.GetFileName(entry.Name) != App) continue;
            entry.ExtractToFile(target, true);
            return;
        }
        throw new InvalidDataException($"{App} not found in the archive.");
    }

    private static async Task DownloadAsync(UpdateInfo update, string target, IProgress<double>? progress, CancellationToken ct)
    {
        using var http = Client(TimeSpan.FromMinutes(10));
        using var response = await http.GetAsync(update.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var dest = File.Create(target))
        {
            var buffer = new byte[81920];
            long done = 0;
            int n, percent = -1;
            while ((n = await source.ReadAsync(buffer, ct)) > 0)
            {
                await dest.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                // Once per percent: every report is a trip to the UI thread.
                if (total > 0 && (int)(done * 100 / total.Value) is var p && p != percent)
                {
                    percent = p;
                    progress?.Report(p / 100.0);
                }
            }
        }
        if (update.Sha256 is { } expected && !string.Equals(Sha256Of(target), expected, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(target);
            throw new InvalidDataException("The download does not match the release checksum.");
        }
    }

    public static string Sha256Of(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }

    /// <summary>Removes what an update left behind (the previous exe, a half download). Best effort, at start.</summary>
    public static void CleanupOld()
    {
        if (ExePath is not { } exe) return;
        TryDelete(exe + OldSuffix);
        TryDelete(exe + NewSuffix);
        TryDelete(exe + ".bin" + NewSuffix);
    }

    /// <summary>
    /// "--updated &lt;pid&gt;" from <see cref="ApplyAsync"/>: waits for the previous process to end so the new one
    /// becomes the single instance instead of handing its arguments to the old one. Returns the other arguments.
    /// </summary>
    public static string[] WaitForPreviousInstance(string[] args)
    {
        int i = Array.IndexOf(args, "--updated");
        if (i < 0) return args;
        if (i + 1 < args.Length && int.TryParse(args[i + 1], out var pid))
        {
            try { using var p = Process.GetProcessById(pid); p.WaitForExit(10_000); }
            catch { /* already gone */ }
        }
        return args.Where((_, k) => k != i && k != i + 1).ToArray();
    }

    /// <summary>Release notes (Markdown from release-drafter) as plain text: headings without "#", no "@author".</summary>
    public static string ReleaseNotesText(string markdown)
    {
        var lines = markdown.Replace("\r", "").Split('\n')
            .Select(l => l.TrimEnd())
            .Select(l => l.StartsWith('#') ? l.TrimStart('#', ' ') : l)
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"\s+@[\w-]+(\[bot\])?$", ""))
            .Select(l => l.StartsWith("- ") || l.StartsWith("* ") ? "•" + l[1..] : l);
        return System.Text.RegularExpressions.Regex.Replace(string.Join('\n', lines), @"\n{3,}", "\n\n").Trim();
    }

    public static void OpenInBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private static HttpClient Client(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(App, Current.ToString(3)));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
