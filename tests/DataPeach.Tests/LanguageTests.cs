using System.Text.RegularExpressions;
using DataPeach.Core;
using DataPeach.Core.Data;

namespace DataPeach.Tests;

public class LanguageTests
{
    [Theory]
    [InlineData(null, "es", "es")]
    [InlineData(null, "en", "en")]
    [InlineData(null, "fr", "en")]
    [InlineData("en", "es", "en")]
    [InlineData("es", "en", "es")]
    public void PicksSavedChoiceElseSystemLanguage(string? saved, string system, string expected) =>
        Assert.Equal(expected, Loc.Resolve(saved, system));

    [Fact]
    public void EveryTextExistsInBothLanguagesWithTheSamePlaceholders()
    {
        var placeholder = new Regex(@"\{(\d+)(?::[^}]*)?\}");
        foreach (var (key, (en, es)) in Strings.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(en), key);
            Assert.False(string.IsNullOrWhiteSpace(es), key);
            var a = placeholder.Matches(en).Select(m => m.Groups[1].Value).Distinct().Order();
            var b = placeholder.Matches(es).Select(m => m.Groups[1].Value).Distinct().Order();
            Assert.True(a.SequenceEqual(b), key);
        }
    }

    /// <summary>Keys built at run time ("export." + format…) have one entry per value.</summary>
    [Fact]
    public void KeysBuiltFromEnumsExist()
    {
        foreach (var f in Enum.GetValues<ExportFormat>()) Assert.True(Strings.All.ContainsKey("export." + f.ToString().ToLowerInvariant()), f.ToString());
        foreach (var m in Enum.GetValues<ConnectionMode>())
        {
            var name = m.ToString().ToLowerInvariant();
            Assert.True(Strings.All.ContainsKey("conn.mode." + name), name);
            Assert.True(Strings.All.ContainsKey("conn.dlg.mode." + name + ".note"), name);
        }
    }

    [Fact]
    public void EveryKeyUsedInTheSourceExists()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src"));
        var used = new Regex(@"Loc\.(?:T|F)\(""([\w.]+)""|Loc\.Plural\(""([\w.]+)""|\{l:L ([\w.]+)\}");
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs") || f.EndsWith(".axaml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            foreach (Match m in used.Matches(File.ReadAllText(file)))
            {
                // "prefix." + value: checked by KeysBuiltFromEnumsExist.
                if (m.Groups[1].Success && !m.Groups[1].Value.EndsWith('.') && !Strings.All.ContainsKey(m.Groups[1].Value)) missing.Add(m.Groups[1].Value);
                if (m.Groups[2].Success && !(Strings.All.ContainsKey(m.Groups[2].Value + ".one") && Strings.All.ContainsKey(m.Groups[2].Value + ".many")))
                    missing.Add(m.Groups[2].Value + ".one/.many");
                if (m.Groups[3].Success && !Strings.All.ContainsKey(m.Groups[3].Value)) missing.Add(m.Groups[3].Value);
            }
        }
        Assert.Empty(missing);
    }
}
