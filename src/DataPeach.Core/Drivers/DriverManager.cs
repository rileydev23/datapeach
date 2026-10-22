using System.Data.Common;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace DataPeach.Core.Drivers;

/// <summary>
/// The driver of an engine: an official ADO.NET package from nuget.org. DataPeach ships none of them; each one is
/// downloaded the first time it is needed, with the recommended version below or any other the user picks in the
/// drivers dialog. Only the dialect (Db/Dialect.cs) is DataPeach's own code.
/// </summary>
public sealed record DriverSpec(DbKind Kind, string Package, string Version, string Assembly, string FactoryType)
{
    public string Title => DbKinds.Name(Kind);
}

public static class DriverCatalog
{
    public static readonly DriverSpec[] All =
    {
        new(DbKind.Sqlite, "Microsoft.Data.Sqlite", "10.0.13", "Microsoft.Data.Sqlite", "Microsoft.Data.Sqlite.SqliteFactory"),
        new(DbKind.Postgres, "Npgsql", "10.0.3", "Npgsql", "Npgsql.NpgsqlFactory"),
        new(DbKind.MySql, "MySqlConnector", "2.6.2", "MySqlConnector", "MySqlConnector.MySqlConnectorFactory"),
        new(DbKind.SqlServer, "Microsoft.Data.SqlClient", "7.1.1", "Microsoft.Data.SqlClient", "Microsoft.Data.SqlClient.SqlClientFactory"),
        new(DbKind.Oracle, "Oracle.ManagedDataAccess.Core", "23.26.400", "Oracle.ManagedDataAccess", "Oracle.ManagedDataAccess.Client.OracleClientFactory"),
        // The Full package carries the native engine of every platform (~110 MB download); only this machine's is kept.
        new(DbKind.DuckDb, "DuckDB.NET.Data.Full", "1.5.6.1", "DuckDB.NET.Data", "DuckDB.NET.Data.DuckDBClientFactory"),
    };

    public static DriverSpec For(DbKind kind) => All.First(d => d.Kind == kind);
}

/// <summary>What is installed for one driver (drivers/&lt;kind&gt;/driver.json).</summary>
public sealed class InstalledDriver
{
    public string Package { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTimeOffset Installed { get; set; }
    public List<string> Packages { get; set; } = new();
    public long Bytes { get; set; }
}

/// <summary>
/// Downloads, installs and loads drivers. Each driver lives in its own folder with every assembly and native library
/// it needs, and is loaded in its own <see cref="AssemblyLoadContext"/> so two drivers never fight over a shared
/// dependency. The app only talks to them through <see cref="DbProviderFactory"/>.
/// </summary>
public sealed class DriverManager
{
    private const string Feed = "https://api.nuget.org/v3-flatcontainer/";
    private readonly string _root;
    private readonly Dictionary<DbKind, DbProviderFactory> _loaded = new();
    private readonly SemaphoreSlim _installing = new(1, 1);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public DriverManager(string root)
    {
        _root = root;
    }

    public string Root => _root;

    private string FolderOf(DbKind kind) => Path.Combine(_root, kind.ToString().ToLowerInvariant());

    public InstalledDriver? Installed(DbKind kind)
    {
        try
        {
            var file = Path.Combine(FolderOf(kind), "driver.json");
            return File.Exists(file) ? JsonSerializer.Deserialize(File.ReadAllText(file), CoreJson.Default.InstalledDriver) : null;
        }
        catch { return null; }
    }

    public bool IsInstalled(DbKind kind) => Installed(kind) is not null;

    /// <summary>True when the installed version is older than the one this DataPeach recommends.</summary>
    public bool HasUpdate(DbKind kind) =>
        Installed(kind) is { } i && NuGetPackage.CompareVersions(i.Version, DriverCatalog.For(kind).Version) < 0;

    public bool IsLoaded(DbKind kind)
    {
        lock (_loaded) return _loaded.ContainsKey(kind);
    }

    /// <summary>
    /// Downloads the driver and every dependency the runtime does not already have, into a temporary folder that
    /// replaces the old one only when everything arrived. <paramref name="progress"/> gets the package being fetched.
    /// </summary>
    public async Task InstallAsync(DbKind kind, string? version, IProgress<string>? progress, CancellationToken ct)
    {
        var spec = DriverCatalog.For(kind);
        if (!string.IsNullOrWhiteSpace(version)) spec = spec with { Version = version.Trim() };
        // A loaded driver keeps its files open: a new version can only take over after a restart.
        if (IsLoaded(kind)) throw new IOException(Loc.T("drivers.inUse"));
        await _installing.WaitAsync(ct);
        var target = FolderOf(kind);
        var tmp = target + ".tmp";
        try
        {
            Directory.CreateDirectory(_root);
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);

            var framework = FrameworkAssemblies();
            // Breadth-first over the dependency graph; when two packages ask for the same one, the higher version wins.
            var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [spec.Package] = spec.Version };
            var fetched = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            queue.Enqueue(spec.Package);
            long bytes = 0;
            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var id = queue.Dequeue();
                var wantedVersion = wanted[id];
                if (fetched.TryGetValue(id, out var have) && NuGetPackage.CompareVersions(have, wantedVersion) >= 0) continue;
                progress?.Report($"{id} {wantedVersion}");
                var data = await DownloadAsync(id, wantedVersion, ct);
                bytes += data.Length;
                using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
                foreach (var (entry, name) in NuGetPackage.FilesToCopy(zip.Entries.Select(e => e.FullName), NuGetPackage.RuntimeMajor, NuGetPackage.Rids()))
                {
                    zip.GetEntry(entry)!.ExtractToFile(Path.Combine(tmp, name), overwrite: true);
                }
                fetched[id] = wantedVersion;
                foreach (var dep in NuGetPackage.Dependencies(NuGetPackage.ReadNuspec(zip), NuGetPackage.RuntimeMajor))
                {
                    if (IsProvidedByRuntime(dep.Id, framework)) continue;
                    if (wanted.TryGetValue(dep.Id, out var w) && NuGetPackage.CompareVersions(w, dep.Version) >= 0) continue;
                    wanted[dep.Id] = dep.Version;
                    queue.Enqueue(dep.Id);
                }
            }

            var info = new InstalledDriver
            {
                Package = spec.Package,
                Version = spec.Version,
                Installed = DateTimeOffset.Now,
                Packages = fetched.Select(kv => $"{kv.Key} {kv.Value}").OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
                Bytes = bytes,
            };
            File.WriteAllText(Path.Combine(tmp, "driver.json"), JsonSerializer.Serialize(info, CoreJson.Default.InstalledDriver));

            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(tmp, target);
        }
        finally
        {
            // Whatever happened, no half-downloaded folder stays behind.
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { /* next install cleans it */ }
            _installing.Release();
        }
    }

    public void Uninstall(DbKind kind)
    {
        if (IsLoaded(kind)) throw new IOException(Loc.T("drivers.inUse"));
        var dir = FolderOf(kind);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    /// <summary>Published versions of the driver's package on nuget.org, newest first (previews left out).</summary>
    public static async Task<List<string>> VersionsAsync(DbKind kind, CancellationToken ct)
    {
        var lid = DriverCatalog.For(kind).Package.ToLowerInvariant();
        var json = await Http.GetStringAsync($"{Feed}{lid}/index.json", ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("versions").EnumerateArray()
            .Select(v => v.GetString() ?? "")
            .Where(v => v.Length > 0 && !v.Contains('-'))
            .OrderByDescending(v => v, Comparer<string>.Create(NuGetPackage.CompareVersions))
            .ToList();
    }

    private static async Task<byte[]> DownloadAsync(string id, string version, CancellationToken ct)
    {
        var lid = id.ToLowerInvariant();
        var v = NuGetPackage.NormalizeVersion(version);
        var url = $"{Feed}{lid}/{v}/{lid}.{v}.nupkg";
        using var resp = await Http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) throw new IOException(Loc.F("drivers.downloadFailed", id, v, (int)resp.StatusCode));
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>File names (without .dll) of the assemblies that come with the runtime.</summary>
    private static HashSet<string> FrameworkAssemblies()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
            foreach (var p in tpa.Split(Path.PathSeparator)) set.Add(Path.GetFileNameWithoutExtension(p));
        // Single-file builds have no TPA list: the shared framework assemblies are still in the default context.
        foreach (var a in AssemblyLoadContext.Default.Assemblies) set.Add(a.GetName().Name ?? "");
        return set;
    }

    private static bool IsProvidedByRuntime(string id, HashSet<string> framework) =>
        framework.Contains(id) || IsFrameworkPackage(id);

    /// <summary>Packages that only exist for older frameworks; the runtime has their contents.</summary>
    private static bool IsFrameworkPackage(string id) =>
        id.StartsWith("System.", StringComparison.OrdinalIgnoreCase) && IsInRuntime(id)
        || id.Equals("NETStandard.Library", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("Microsoft.NETCore.", StringComparison.OrdinalIgnoreCase)
        || id.Equals("Microsoft.CSharp", StringComparison.OrdinalIgnoreCase);

    private static bool IsInRuntime(string assembly)
    {
        try { return AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(assembly)) is not null; }
        catch { return false; }
    }

    /// <summary>The provider factory of an installed driver, loading it the first time.</summary>
    public DbProviderFactory Load(DbKind kind)
    {
        lock (_loaded)
        {
            if (_loaded.TryGetValue(kind, out var f)) return f;
            var spec = DriverCatalog.For(kind);
            var dir = FolderOf(kind);
            if (!File.Exists(Path.Combine(dir, "driver.json"))) throw new DriverMissingException(kind);
            var ctx = new DriverLoadContext(dir, kind.ToString());
            var asm = ctx.LoadFromAssemblyName(new AssemblyName(spec.Assembly));
            var type = asm.GetType(spec.FactoryType, throwOnError: true)!;
            var field = type.GetField("Instance", BindingFlags.Public | BindingFlags.Static)
                        ?? throw new InvalidOperationException($"{spec.FactoryType}.Instance");
            f = (DbProviderFactory)field.GetValue(null)!;
            _loaded[kind] = f;
            return f;
        }
    }
}

public sealed class DriverMissingException(DbKind kind) : Exception(Loc.F("drivers.missing", DbKinds.Name(kind)))
{
    public DbKind Kind { get; } = kind;
}

/// <summary>Resolves a driver's assemblies and native libraries from its folder; everything else from the app.</summary>
internal sealed class DriverLoadContext(string folder, string name) : AssemblyLoadContext("driver-" + name)
{
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // The runtime's own assemblies (System.Data.Common above all) must be shared with the app.
        try
        {
            if (Default.LoadFromAssemblyName(assemblyName) is { } shared) return shared;
        }
        catch { /* not in the app: look in the driver folder */ }
        var path = Path.Combine(folder, assemblyName.Name + ".dll");
        return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        foreach (var candidate in new[] { unmanagedDllName, unmanagedDllName + ".dll", "lib" + unmanagedDllName + ".so", unmanagedDllName + ".so", "lib" + unmanagedDllName + ".dylib" })
        {
            var path = Path.Combine(folder, candidate);
            if (File.Exists(path)) return LoadUnmanagedDllFromPath(path);
        }
        return IntPtr.Zero;
    }
}
