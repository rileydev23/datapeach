using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace DataPeach.Core.Drivers;

public sealed record PackageRef(string Id, string Version);

/// <summary>
/// The parts of a .nupkg a driver needs: its dependencies for our target framework and the files to copy (managed
/// assemblies for the best framework folder, plus the native libraries and RID-specific assemblies of this machine).
/// Pure apart from reading the zip, and tested.
/// </summary>
public static class NuGetPackage
{
    /// <summary>Major version of the .NET the app runs on: the best "netX.0" folder we can use.</summary>
    public static int RuntimeMajor => Environment.Version.Major;

    /// <summary>
    /// Ranks a target framework for this app: higher is better, -1 is unusable. netX.0 (X ≤ runtime) beats
    /// netstandard2.1, which beats netstandard2.0. Platform-specific ones ("net8.0-windows") and .NET Framework are
    /// left out. <paramref name="tfm"/> may be a folder name ("net8.0") or a nuspec value (".NETStandard2.0").
    /// </summary>
    public static int Rank(string? tfm, int runtimeMajor)
    {
        if (string.IsNullOrEmpty(tfm)) return 0; // no framework: applies to all, lowest priority
        var t = tfm.Trim().ToLowerInvariant();
        if (t.Contains('-')) return -1;
        if (t.StartsWith(".netstandard")) t = "netstandard" + t[".netstandard".Length..];
        else if (t.StartsWith(".netcoreapp")) t = "netcoreapp" + t[".netcoreapp".Length..];
        if (t.StartsWith("netstandard"))
        {
            return t[11..] switch { "2.1" => 3, "2.0" => 2, "1.6" or "1.5" or "1.4" or "1.3" or "1.2" or "1.1" or "1.0" => 1, _ => -1 };
        }
        if (t.StartsWith("netcoreapp"))
        {
            // Older than net5.0 and ranked among themselves: a package may ship netcoreapp1.0 next to netcoreapp2.2
            // (AdoNetCore.AseClient), and the 1.x build lacks APIs the newer one has (its DbProviderFactory).
            return t[10..] switch
            {
                "1.0" or "1.1" => 1,
                "2.0" => 4, "2.1" => 5, "2.2" => 6,
                "3.0" => 7, "3.1" => 8,
                _ => -1,
            };
        }
        if (t.StartsWith("net") && t.Contains('.'))
        {
            var major = t[3..t.IndexOf('.')];
            return int.TryParse(major, out var m) && m >= 5 && m <= runtimeMajor ? 10 + m : -1;
        }
        return -1; // net462, net48, portable-…: .NET Framework
    }

    /// <summary>"[2.1.10, )" → 2.1.10; "2.1.10" → 2.1.10; "[1.0.0]" → 1.0.0. Null when there is no lower bound.</summary>
    public static string? MinVersion(string? range)
    {
        if (string.IsNullOrWhiteSpace(range)) return null;
        var r = range.Trim();
        if (r[0] is '[' or '(')
        {
            var inner = r.Trim('[', ']', '(', ')');
            var low = inner.Split(',')[0].Trim();
            return low.Length > 0 ? low : null;
        }
        return r;
    }

    /// <summary>Lower-case, normalized version as used in nuget.org URLs ("8.0.0.0" → "8.0.0").</summary>
    public static string NormalizeVersion(string v)
    {
        var plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        var dash = v.IndexOf('-');
        var core = dash >= 0 ? v[..dash] : v;
        var rest = dash >= 0 ? v[dash..] : "";
        var parts = core.Split('.').Select(p => int.TryParse(p, out var n) ? n.ToString() : p).ToList();
        while (parts.Count < 3) parts.Add("0");
        if (parts.Count == 4 && parts[3] == "0") parts.RemoveAt(3);
        return (string.Join('.', parts) + rest).ToLowerInvariant();
    }

    public static int CompareVersions(string a, string b)
    {
        static (Version v, string pre) Split(string s)
        {
            var dash = s.IndexOf('-');
            var core = dash >= 0 ? s[..dash] : s;
            return (Version.TryParse(core, out var v) ? v : new Version(0, 0), dash >= 0 ? s[(dash + 1)..] : "");
        }
        var (va, pa) = Split(NormalizeVersion(a));
        var (vb, pb) = Split(NormalizeVersion(b));
        int c = va.CompareTo(vb);
        if (c != 0) return c;
        if (pa.Length == 0 || pb.Length == 0) return pb.Length.CompareTo(pa.Length); // a release beats its previews
        return string.CompareOrdinal(pa, pb);
    }

    /// <summary>Dependencies of the nuspec for the best dependency group of this runtime.</summary>
    public static List<PackageRef> Dependencies(XDocument nuspec, int runtimeMajor)
    {
        var deps = nuspec.Descendants().FirstOrDefault(e => e.Name.LocalName == "dependencies");
        if (deps is null) return new();
        var groups = deps.Elements().Where(e => e.Name.LocalName == "group").ToList();
        IEnumerable<XElement> chosen;
        if (groups.Count == 0) chosen = deps.Elements().Where(e => e.Name.LocalName == "dependency");
        else
        {
            var best = groups
                .Select(g => (g, rank: Rank((string?)g.Attribute("targetFramework"), runtimeMajor)))
                .Where(x => x.rank >= 0)
                .OrderByDescending(x => x.rank)
                .FirstOrDefault().g;
            if (best is null) return new();
            chosen = best.Elements().Where(e => e.Name.LocalName == "dependency");
        }
        var list = new List<PackageRef>();
        foreach (var d in chosen)
        {
            var id = (string?)d.Attribute("id");
            var v = MinVersion((string?)d.Attribute("version"));
            // "exclude=Compile" alone still needs the runtime files; only skip what is excluded at runtime.
            var exclude = ((string?)d.Attribute("exclude") ?? "").ToLowerInvariant();
            if (id is null || v is null || exclude.Contains("runtime") || exclude == "all") continue;
            list.Add(new PackageRef(id, v));
        }
        return list;
    }

    /// <summary>
    /// RIDs of this machine, most specific first ("win-x64", "win"). Native libraries and RID-specific
    /// assemblies are taken from the first one that has them.
    /// </summary>
    public static string[] Rids()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => "x64",
        };
        if (OperatingSystem.IsWindows()) return new[] { $"win-{arch}", "win" };
        if (OperatingSystem.IsMacOS()) return new[] { $"osx-{arch}", "osx", "unix" };
        return new[] { $"linux-{arch}", "linux", "unix" };
    }

    /// <summary>
    /// Entries to copy from a package, as (zip entry, file name). Managed assemblies from the best lib/ folder, replaced
    /// by runtimes/&lt;rid&gt;/lib/ when the package has a RID-specific build, plus runtimes/&lt;rid&gt;/native/.
    /// </summary>
    public static List<(string Entry, string FileName)> FilesToCopy(IEnumerable<string> entries, int runtimeMajor, string[] rids)
    {
        var all = entries.Select(e => e.Replace('\\', '/')).ToList();
        var result = new List<(string, string)>();

        string? BestFolder(string prefix)
        {
            return all.Where(e => e.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(e => e[prefix.Length..])
                .Where(rest => rest.Contains('/'))
                .Select(rest => rest[..rest.IndexOf('/')])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(tfm => (tfm, rank: Rank(tfm, runtimeMajor)))
                .Where(x => x.rank > 0)
                .OrderByDescending(x => x.rank)
                .Select(x => x.tfm)
                .FirstOrDefault();
        }

        static bool IsAssembly(string e) => e.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

        // RID-specific managed build wins over lib/.
        string? managedPrefix = null;
        foreach (var rid in rids)
        {
            var p = $"runtimes/{rid}/lib/";
            if (BestFolder(p) is { } f) { managedPrefix = p + f + "/"; break; }
        }
        if (managedPrefix is null && BestFolder("lib/") is { } libTfm) managedPrefix = "lib/" + libTfm + "/";
        if (managedPrefix is not null)
        {
            foreach (var e in all)
            {
                if (e.StartsWith(managedPrefix, StringComparison.OrdinalIgnoreCase) && IsAssembly(e) && !e[managedPrefix.Length..].Contains('/'))
                    result.Add((e, e[managedPrefix.Length..]));
            }
        }

        foreach (var rid in rids)
        {
            var p = $"runtimes/{rid}/native/";
            var native = all.Where(e => e.StartsWith(p, StringComparison.OrdinalIgnoreCase) && !e.EndsWith('/') && !e[p.Length..].Contains('/')).ToList();
            if (native.Count == 0) continue;
            foreach (var e in native) result.Add((e, e[p.Length..]));
            break;
        }
        return result;
    }

    /// <summary>Reads the .nuspec of an open package.</summary>
    public static XDocument ReadNuspec(ZipArchive zip)
    {
        var entry = zip.Entries.FirstOrDefault(e => !e.FullName.Contains('/') && e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("nuspec not found");
        using var s = entry.Open();
        return XDocument.Load(s);
    }
}
