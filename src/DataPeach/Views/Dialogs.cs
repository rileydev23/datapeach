using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DataPeach.Controls;
using DataPeach.Core;

namespace DataPeach.Views;

/// <summary>Small code-built modal dialogs, so we avoid a XAML file per prompt.</summary>
public static class Dialogs
{
    public static Window? Owner =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    internal static Window Make(string title, double width)
    {
        return new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Palette.Panel,
            ShowInTaskbar = false,
        };
    }

    internal static Button Btn(string text, bool primary = false) => new()
    {
        Content = text,
        MinWidth = 84,
        Padding = new Thickness(12, 5),
        Background = primary ? Palette.Accent : Palette.Panel2,
        Foreground = Brushes.White,
        HorizontalContentAlignment = HorizontalAlignment.Center,
    };

    public static async Task<bool> Confirm(string title, string message, string? ok = null, string? cancelText = null)
    {
        var owner = Owner;
        if (owner is null) return false;
        var w = Make(title, 440);
        bool result = false;
        var okBtn = Btn(ok ?? Loc.T("common.ok"), true);
        var cancel = Btn(cancelText ?? Loc.T("common.cancel"));
        okBtn.Click += (_, _) => { result = true; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        w.Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Text },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { okBtn, cancel } },
            },
        };
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        await w.ShowDialog(owner);
        return result;
    }

    /// <summary>Two choices plus Cancel. Returns 0 or 1 for the choices, -1 for cancel.</summary>
    public static async Task<int> Choose(string title, string message, string first, string second)
    {
        var owner = Owner;
        if (owner is null) return -1;
        var w = Make(title, 460);
        int result = -1;
        var a = Btn(first, true);
        var b = Btn(second);
        var cancel = Btn(Loc.T("common.cancel"));
        a.Click += (_, _) => { result = 0; w.Close(); };
        b.Click += (_, _) => { result = 1; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        w.Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Text },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { a, b, cancel } },
            },
        };
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        await w.ShowDialog(owner);
        return result;
    }

    public static async Task Message(string title, string message)
    {
        var owner = Owner;
        if (owner is null) return;
        var w = Make(title, 520);
        var ok = Btn(Loc.T("common.close"), true);
        ok.Click += (_, _) => w.Close();
        // Display-only text is a TextBlock: no caret, no focus border, nothing that looks editable.
        w.Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 16,
            Children =
            {
                new ScrollViewer
                {
                    MaxHeight = 320,
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Text },
                },
                new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Children = { ok } },
            },
        };
        w.KeyDown += (_, e) => { if (e.Key is Key.Escape or Key.Enter) w.Close(); };
        await w.ShowDialog(owner);
    }

    /// <summary>F1 / "?": app identity first, then the keyboard shortcuts as a plain two-column table.</summary>
    public static async Task About(string description, string dataPath, IEnumerable<(string Title, (string Gesture, string Description)[] Items)> shortcutGroups,
        Store store, Action openDataFolder)
    {
        var owner = Owner;
        if (owner is null) return;
        var w = Make(Loc.T("dlg.about.title"), 520);
        var version = typeof(Dialogs).Assembly.GetName().Version;
        var close = Btn(Loc.T("common.close"), true);
        close.Click += (_, _) => w.Close();
        var openData = Btn(Loc.T("dlg.about.openData"));
        openData.Click += (_, _) => openDataFolder();

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var logo = new Image
        {
            Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://DataPeach/Assets/icon64.png"))),
            Width = 48, Height = 48, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top,
        };
        DockPanel.SetDock(logo, Dock.Left);
        header.Children.Add(logo);
        header.Children.Add(new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = "DataPeach", FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Palette.Text },
                new TextBlock { Text = Loc.F("dlg.about.version", version?.ToString(3)), Foreground = Palette.Dim, FontSize = 12 },
                new TextBlock { Text = description, Foreground = Palette.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) },
            },
        });

        var shortcuts = new StackPanel { Spacing = 2 };
        foreach (var (title, items) in shortcutGroups)
        {
            shortcuts.Children.Add(new TextBlock
            {
                Text = title, FontWeight = FontWeight.SemiBold, Foreground = Palette.Text,
                Margin = new Thickness(0, shortcuts.Children.Count == 0 ? 0 : 10, 0, 4),
            });
            foreach (var (gesture, desc) in items)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("215,*") };
                row.Children.Add(new TextBlock { Text = gesture, Foreground = Palette.Dim, FontSize = 12, FontFamily = new FontFamily("Cascadia Mono, Consolas, DejaVu Sans Mono") });
                var d = new TextBlock { Text = desc, Foreground = Palette.Text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(d, 1);
                row.Children.Add(d);
                shortcuts.Children.Add(row);
            }
        }

        w.Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 14,
            Children =
            {
                header,
                new Border { Height = 1, Background = Palette.Border },
                new TextBlock { Text = Loc.T("dlg.about.shortcuts"), FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Palette.Text },
                new ScrollViewer
                {
                    MaxHeight = 380,
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content = shortcuts,
                },
                new Border { Height = 1, Background = Palette.Border },
                LanguagePicker(store),
                new TextBlock { Text = Loc.F("dlg.about.dataPath", dataPath), Foreground = Palette.Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { openData, close } },
            },
        };
        w.KeyDown += (_, e) => { if (e.Key is Key.Escape or Key.Enter) w.Close(); };
        await w.ShowDialog(owner);
    }

    /// <summary>
    /// "Idioma: [English] [Español]": the joined view switch of the design system. The choice is saved at once and
    /// applies on the next launch, since every screen reads its texts when it is built.
    /// </summary>
    private static Control LanguagePicker(Store store)
    {
        var note = new TextBlock { Text = Loc.T("dlg.about.languageNext"), Foreground = Palette.Amber, FontSize = 11, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(new TextBlock { Text = Loc.T("dlg.about.language"), Foreground = Palette.Text, VerticalAlignment = VerticalAlignment.Center });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var chosen = store.State.Language ?? Loc.Current;
        for (int i = 0; i < Loc.Languages.Length; i++)
        {
            var (code, name) = Loc.Languages[i];
            var b = new Button { Content = name, Tag = code };
            b.Classes.Add("mode");
            b.Classes.Add(i == 0 ? "left" : "right");
            if (code == chosen) b.Classes.Add("active");
            b.Click += (_, _) =>
            {
                foreach (var other in buttons.Children.OfType<Button>()) other.Classes.Set("active", ReferenceEquals(other, b));
                store.State.Language = code;
                store.Save();
                note.IsVisible = code != Loc.Current;
            };
            buttons.Children.Add(b);
        }
        row.Children.Add(buttons);
        row.Children.Add(note);
        return row;
    }

    public static async Task<string?> Prompt(string title, string label, string initial = "")
    {
        var r = await Form(title, new[] { (label, initial) });
        return r?[0];
    }

    public static async Task<string[]?> Form(string title, (string Label, string Initial)[] fields, string? ok = null)
    {
        var owner = Owner;
        if (owner is null) return null;
        var w = Make(title, 480);
        string[]? result = null;
        var boxes = new List<TextBox>();
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 8 };
        foreach (var (label, initial) in fields)
        {
            panel.Children.Add(new TextBlock { Text = label, Foreground = Palette.Dim });
            var tb = new TextBox { Text = initial };
            boxes.Add(tb);
            panel.Children.Add(tb);
        }
        var okBtn = Btn(ok ?? Loc.T("common.ok"), true);
        var cancel = Btn(Loc.T("common.cancel"));
        void Accept() { result = boxes.Select(b => b.Text ?? "").ToArray(); w.Close(); }
        okBtn.Click += (_, _) => Accept();
        cancel.Click += (_, _) => w.Close();
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right, Children = { okBtn, cancel },
        });
        w.Content = panel;
        w.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) w.Close();
            else if (e.Key == Key.Enter) Accept();
        };
        w.Opened += (_, _) => { boxes[0].Focus(); boxes[0].SelectAll(); };
        await w.ShowDialog(owner);
        return result;
    }

    public static async Task<string?> PickFolder(string title)
    {
        var owner = Owner;
        if (owner is null) return null;
        var res = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return res.Count > 0 ? res[0].TryGetLocalPath() : null;
    }

    /// <summary>Ctrl+Shift+O: fuzzy-pick a saved connection.</summary>
    public static Task<ConnectionEntry?> QuickOpen(IReadOnlyList<ConnectionEntry> connections) =>
        Pick(Loc.T("dlg.openConnection"), Loc.T("dlg.openConnection.watermark"),
            q => connections.Where(r => Fuzzy(r.Name + " " + r.Target, q)).ToList(),
            r => r.Name, r => DbKinds.Name(r.Kind) + " · " + r.Target, r => Palette.FromHex(DbKinds.Color(r.Kind)));

    public static async Task<string?> PickFile(string title, string filterName, string[] patterns)
    {
        var owner = Owner;
        if (owner is null) return null;
        var res = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(filterName) { Patterns = patterns }, FilePickerFileTypes.All },
        });
        return res.Count > 0 ? res[0].TryGetLocalPath() : null;
    }

    public static async Task<string?> SaveFile(string title, string suggestedName)
    {
        var owner = Owner;
        if (owner is null) return null;
        var res = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = suggestedName });
        return res?.TryGetLocalPath();
    }

    public static async Task CopyAsync(string text)
    {
        if (Owner?.Clipboard is { } cb) await cb.SetTextAsync(text);
    }

    public static async Task<T?> Pick<T>(string title, string watermark, Func<string, List<T>> filter,
        Func<T, string> main, Func<T, string> detail, Func<T, IBrush>? glyph) where T : class
    {
        var owner = Owner;
        if (owner is null) return null;
        var w = Make(title, 620);
        w.SizeToContent = SizeToContent.Manual;
        w.Height = 440;
        T? result = null;
        var search = new TextBox { Watermark = watermark };
        var first = filter("");
        var list = new ListBox { Background = Palette.Bg, ItemsSource = first, SelectedIndex = first.Count > 0 ? 0 : -1 };
        list.ItemTemplate = new FuncDataTemplate<T>((r, _) =>
        {
            var text = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = r is null ? "" : main(r), FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = r is null ? "" : detail(r), Foreground = Palette.Dim, FontSize = 11 },
                },
            };
            var row = new DockPanel { Margin = new Thickness(8, 4) };
            if (glyph is not null && r is not null)
            {
                var g = new TextBlock { Text = "●", Foreground = glyph(r), FontSize = 10, Margin = new Thickness(0, 3, 8, 0), VerticalAlignment = VerticalAlignment.Top };
                DockPanel.SetDock(g, Dock.Left);
                row.Children.Add(g);
            }
            row.Children.Add(text);
            return row;
        });
        void Filter()
        {
            var filtered = filter(search.Text?.Trim() ?? "");
            list.ItemsSource = filtered;
            list.SelectedIndex = filtered.Count > 0 ? 0 : -1;
        }
        search.TextChanged += (_, _) => Filter();
        void Accept() { result = list.SelectedItem as T; if (result is not null) w.Close(); }
        list.DoubleTapped += (_, _) => Accept();
        w.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) w.Close();
            else if (e.Key == Key.Enter) Accept();
            else if (e.Key == Key.Down && list.ItemCount > 0) { list.SelectedIndex = Math.Min(list.SelectedIndex + 1, list.ItemCount - 1); list.ScrollIntoView(list.SelectedIndex); e.Handled = true; }
            else if (e.Key == Key.Up && list.ItemCount > 0) { list.SelectedIndex = Math.Max(list.SelectedIndex - 1, 0); list.ScrollIntoView(list.SelectedIndex); e.Handled = true; }
        };
        var grid = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(search, Dock.Top);
        search.Margin = new Thickness(0, 0, 0, 8);
        grid.Children.Add(search);
        grid.Children.Add(list);
        w.Content = grid;
        w.Opened += (_, _) => search.Focus();
        await w.ShowDialog(owner);
        return result;
    }

    public static bool Fuzzy(string text, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        int i = 0;
        foreach (var ch in text)
        {
            if (char.ToLowerInvariant(ch) == char.ToLowerInvariant(query[i]) && ++i == query.Length) return true;
        }
        return false;
    }
}
