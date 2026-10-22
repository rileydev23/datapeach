using Avalonia.Media;

namespace DataPeach.Controls;

/// <summary>Brushes shared by code-built UI. Values from docs/DISENO.md.</summary>
public static class Palette
{
    private static SolidColorBrush B(string hex) => new(Color.Parse(hex));

    public static readonly IBrush Bg = B("#1e1f22");
    public static readonly IBrush Chrome = B("#18191c");
    public static readonly IBrush Sidebar = B("#222327");
    public static readonly IBrush Panel = B("#26272b");
    public static readonly IBrush Panel2 = B("#2d2e33");
    public static readonly IBrush Border = B("#3a3c42");
    public static readonly IBrush Code = B("#1b1c1f");
    public static readonly IBrush Text = B("#d4d4d4");
    public static readonly IBrush Dim = B("#8a8d93");
    public static readonly IBrush Accent = B("#4a90d9");
    public static readonly IBrush Selection = B("#33507a");
    public static readonly IBrush Transparent = Brushes.Transparent;

    public static readonly IBrush Blue = B("#4a90d9");
    public static readonly IBrush Amber = B("#e0a24a");
    public static readonly IBrush Green = B("#6cc070");
    public static readonly IBrush Pink = B("#d96a9b");
    public static readonly IBrush Purple = B("#9a7ae0");
    public static readonly IBrush Teal = B("#4ac7c0");
    public static readonly IBrush Red = B("#e0645a");
    public static readonly IBrush Olive = B("#c8c84a");

    public static readonly IBrush MatchBg = B("#5a4318");
    public static readonly IBrush MatchFg = B("#ffd27a");
    /// <summary>Text on the amber warning background (#5a4318): banners and edited cells.</summary>
    public static readonly IBrush AmberLight = B("#f0c890");
    public static readonly IBrush FileBg = B("#34363c");
    public static readonly IBrush AddedBg = B("#1d3a2a"), AddedFg = B("#a6e3b4");
    public static readonly IBrush RemovedBg = B("#431f22"), RemovedFg = B("#f4a6a6");
    public static readonly IBrush HunkBg = B("#25364d");
    public static readonly IBrush ContextFg = B("#c8c8c8");
    public static readonly IBrush Gutter = B("#5d6066");

    public static readonly Color[] Series =
    {
        Color.Parse("#4a90d9"), Color.Parse("#e0a24a"), Color.Parse("#6cc070"), Color.Parse("#d96a9b"),
        Color.Parse("#9a7ae0"), Color.Parse("#4ac7c0"), Color.Parse("#e0645a"), Color.Parse("#c8c84a"),
    };

    private static readonly Dictionary<string, IBrush> Cache = new();

    public static IBrush FromHex(string hex)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(hex, out var b)) Cache[hex] = b = B(hex);
            return b;
        }
    }
}
