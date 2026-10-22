using System.Formats.Tar;
using System.IO.Compression;
using DataPeach.Services;

namespace DataPeach.Tests;

public class UpdaterTests
{
    // Trimmed GitHub "latest release" response.
    private const string Release = """
        {
          "tag_name": "v0.2.0",
          "html_url": "https://github.com/rileydev23/datapeach/releases/tag/v0.2.0",
          "body": "## ✨ New / Novedades\n\n- Aviso de versión nueva (#3) @devrileywin\n",
          "assets": [
            { "name": "datapeach-windows-x64-portable.exe", "browser_download_url": "https://example.com/datapeach-windows-x64-portable.exe", "digest": "sha256:ABC123" },
            { "name": "datapeach-windows-x64-setup.exe", "browser_download_url": "https://example.com/datapeach-windows-x64-setup.exe", "digest": null },
            { "name": "datapeach-linux-x64.tar.gz", "browser_download_url": "https://example.com/datapeach-linux-x64.tar.gz" }
          ]
        }
        """;

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData(" V0.1.1 ", "0.1.1")]
    public void TagsBecomeThreePartVersions(string tag, string expected)
    {
        Assert.True(Updater.TryParseTag(tag, out var v));
        Assert.Equal(Version.Parse(expected), v);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    public void BadTagsAreRejected(string? tag) => Assert.False(Updater.TryParseTag(tag, out _));

    [Fact]
    public void NewerReleaseGivesTheAssetForThisComputer()
    {
        var info = Updater.Parse(Release, new Version(0, 1, 1, 0), "datapeach-windows-x64-portable.exe");
        Assert.NotNull(info);
        Assert.Equal(new Version(0, 2, 0), info.Version);
        Assert.Equal("https://example.com/datapeach-windows-x64-portable.exe", info.AssetUrl);
        Assert.Equal("ABC123", info.Sha256);
        Assert.Contains("Aviso de versión nueva", info.Notes);

        Assert.Null(Updater.Parse(Release, new Version(0, 1, 1), "datapeach-windows-x64-setup.exe")!.Sha256);
    }

    [Fact]
    public void SameOrOlderReleaseOrMissingAssetIsNoUpdate()
    {
        Assert.Null(Updater.Parse(Release, new Version(0, 2, 0, 0), "datapeach-windows-x64-portable.exe"));
        Assert.Null(Updater.Parse(Release, new Version(1, 0, 0), "datapeach-windows-x64-portable.exe"));
        Assert.Null(Updater.Parse(Release, new Version(0, 1, 0), "DataPeach-arm64.exe"));
    }

    [Fact]
    public void AssetNamesMatchTheReleaseWorkflow()
    {
        Assert.Equal("datapeach-windows-x64-portable.exe", Updater.AssetName(windows: true, installed: false));
        Assert.Equal("datapeach-windows-x64-setup.exe", Updater.AssetName(windows: true, installed: true));
        Assert.Equal("datapeach-linux-x64.tar.gz", Updater.AssetName(windows: false, installed: true));
    }

    [Fact]
    public void ReplacesTheExecutableAndKeepsTheOldOneAside()
    {
        var dir = Directory.CreateTempSubdirectory("tp-update-").FullName;
        try
        {
            var exe = Path.Combine(dir, "datapeach-windows-x64-portable.exe");
            var fresh = exe + ".new";
            File.WriteAllText(exe, "old");
            File.WriteAllText(fresh, "new");
            using (File.Open(exe, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)) // "running"
                Updater.ReplaceRunning(exe, fresh);
            Assert.Equal("new", File.ReadAllText(exe));
            Assert.False(File.Exists(fresh));
            if (OperatingSystem.IsWindows()) Assert.Equal("old", File.ReadAllText(exe + ".old"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ExtractsTheBinaryFromTheLinuxArchive()
    {
        var dir = Directory.CreateTempSubdirectory("tp-tar-").FullName;
        try
        {
            var archive = Path.Combine(dir, "datapeach-linux-x64.tar.gz");
            using (var gz = new GZipStream(File.Create(archive), CompressionLevel.Fastest))
            using (var tar = new TarWriter(gz))
            {
                foreach (var (name, text) in new[] { ("install.sh", "#!/bin/sh"), ("DataPeach", "ELF") })
                {
                    var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)) };
                    tar.WriteEntry(entry);
                }
            }
            var target = Path.Combine(dir, "out");
            Updater.ExtractBinary(archive, target);
            Assert.Equal("ELF", File.ReadAllText(target));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ChecksumIsLowercaseHex()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "abc");
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Updater.Sha256Of(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void UpdatedArgumentIsRemovedFromTheRest()
    {
        var rest = Updater.WaitForPreviousInstance(new[] { "C:\\proj", "--updated", "999999999" });
        Assert.Equal(new[] { "C:\\proj" }, rest);
    }

    [Fact]
    public void ReleaseNotesBecomePlainText()
    {
        var text = Updater.ReleaseNotesText("## ✨ New / Novedades\r\n\r\n\r\n- Aviso de versión nueva (#3) @devrileywin\r\n* Otra @github-actions[bot]");
        Assert.Equal("✨ New / Novedades\n\n• Aviso de versión nueva (#3)\n• Otra", text);
    }
}
