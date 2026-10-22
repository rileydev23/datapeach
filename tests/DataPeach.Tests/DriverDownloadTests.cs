using DataPeach.Core;
using DataPeach.Core.Db;
using DataPeach.Core.Drivers;

namespace DataPeach.Tests;

/// <summary>
/// Real downloads from nuget.org: only with DATAPEACH_NET_TESTS=1 (CI and offline runs skip them).
/// </summary>
public class DriverDownloadTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("DATAPEACH_NET_TESTS") == "1";

    [Theory]
    [InlineData(DbKind.Sqlite)]
    [InlineData(DbKind.Postgres)]
    [InlineData(DbKind.MySql)]
    [InlineData(DbKind.SqlServer)]
    [InlineData(DbKind.Oracle)]
    [InlineData(DbKind.DuckDb)]
    public async Task DownloadsAndLoadsEveryDriver(DbKind kind)
    {
        if (!Enabled) return;
        var root = Path.Combine(Path.GetTempPath(), "datapeach-drivers-" + Guid.NewGuid().ToString("N"));
        var m = new DriverManager(root);
        var log = new List<string>();
        await m.InstallAsync(kind, null, new Progress<string>(log.Add), CancellationToken.None);
        Assert.True(m.IsInstalled(kind));
        var f = m.Load(kind);
        Assert.NotNull(f.CreateConnection());
        if (kind == DbKind.Sqlite)
        {
            var file = Path.Combine(root, "t.db");
            File.WriteAllBytes(file, Array.Empty<byte>());
            await using var s = new DbSession(new ConnectionEntry { Kind = DbKind.Sqlite, FilePath = file }, null, f);
            await s.OpenAsync(CancellationToken.None);
            Assert.False(string.IsNullOrEmpty(s.ServerVersion));
        }
    }
}
