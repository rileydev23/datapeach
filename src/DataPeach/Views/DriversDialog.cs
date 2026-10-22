using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DataPeach.Controls;
using DataPeach.Core;
using DataPeach.Core.Drivers;
using DataPeach.Services;

namespace DataPeach.Views;

/// <summary>
/// Drivers: one row per engine with what is installed and what DataPeach recommends. Download, update, pick
/// another version from nuget.org or remove; nothing needs a new DataPeach build.
/// </summary>
public static class DriversDialog
{
    public static async Task Show(AppServices app)
    {
        var owner = Dialogs.Owner;
        if (owner is null) return;
        var w = Dialogs.Make(Loc.T("drivers.title"), 620);
        var rows = new StackPanel { Spacing = 0 };
        var status = new TextBlock { Classes = { "dim" }, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        bool busy = false;

        async Task Run(Func<Task> action)
        {
            if (busy) return;
            busy = true;
            try { await action(); }
            catch (Exception ex) { status.Text = ex.Message; status.Foreground = Palette.Red; }
            finally { busy = false; Build(); }
        }

        async Task Install(DriverSpec spec, string? version)
        {
            status.Foreground = Palette.Dim;
            var progress = new Progress<string>(p => status.Text = Loc.F("drivers.downloading", p));
            await Task.Run(() => app.Drivers.InstallAsync(spec.Kind, version, progress, CancellationToken.None));
            status.Text = Loc.F("drivers.installed", spec.Title, version ?? spec.Version);
            status.Foreground = Palette.Green;
        }

        void Build()
        {
            rows.Children.Clear();
            foreach (var spec in DriverCatalog.All)
            {
                var installed = app.Drivers.Installed(spec.Kind);
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8) };
                var info = new StackPanel { Spacing = 2 };
                info.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = "●", FontSize = 10, Foreground = Palette.FromHex(DbKinds.Color(spec.Kind)), VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = spec.Title, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = spec.Package, Foreground = Palette.Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                    },
                });
                string state;
                IBrush brush;
                if (installed is null) { state = Loc.F("drivers.notInstalled", spec.Version); brush = Palette.Dim; }
                else if (app.Drivers.HasUpdate(spec.Kind)) { state = Loc.F("drivers.update", installed.Version, spec.Version); brush = Palette.Amber; }
                else { state = Loc.F("drivers.ok", installed.Version, installed.Bytes / 1024.0 / 1024.0, installed.Installed.ToString("yyyy-MM-dd HH:mm")); brush = Palette.Green; }
                info.Children.Add(new TextBlock { Text = state, Foreground = brush, FontSize = 12, Margin = new Thickness(18, 0, 0, 0) });
                if (installed is not null)
                    info.Children.Add(new TextBlock { Text = string.Join(" · ", installed.Packages), Foreground = Palette.Dim, FontSize = 11, Margin = new Thickness(18, 0, 0, 0), TextWrapping = TextWrapping.Wrap });
                row.Children.Add(info);

                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                if (installed is null || app.Drivers.HasUpdate(spec.Kind))
                {
                    var get = new Button { Content = installed is null ? Loc.T("drivers.download") : Loc.T("drivers.updateBtn") };
                    get.Click += async (_, _) => await Run(() => Install(spec, null));
                    actions.Children.Add(get);
                }
                var other = new Button { Content = Loc.T("drivers.version") };
                other.Click += async (_, _) => await Run(async () =>
                {
                    status.Text = Loc.T("drivers.listing");
                    status.Foreground = Palette.Dim;
                    var versions = await DriverManager.VersionsAsync(spec.Kind, CancellationToken.None);
                    status.Text = "";
                    var picked = await Dialogs.Pick(Loc.F("drivers.pickVersion", spec.Package), Loc.T("drivers.pickVersion.watermark"),
                        q => versions.Where(v => v.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList(),
                        v => v, v => v == spec.Version ? Loc.T("drivers.recommended") : v == installed?.Version ? Loc.T("drivers.current") : "", null);
                    if (picked is not null) await Install(spec, picked);
                });
                actions.Children.Add(other);
                if (installed is not null)
                {
                    var remove = new Button { Content = Loc.T("drivers.remove") };
                    remove.Click += async (_, _) => await Run(() =>
                    {
                        app.Drivers.Uninstall(spec.Kind);
                        status.Text = Loc.F("drivers.removed", spec.Title);
                        status.Foreground = Palette.Dim;
                        return Task.CompletedTask;
                    });
                    actions.Children.Add(remove);
                }
                Grid.SetColumn(actions, 1);
                row.Children.Add(actions);
                if (rows.Children.Count > 0) rows.Children.Add(new Border { Height = 1, Background = Palette.Border });
                rows.Children.Add(row);
            }
        }

        var close = Dialogs.Btn(Loc.T("common.close"), primary: true);
        close.Click += (_, _) => w.Close();
        var openFolder = Dialogs.Btn(Loc.T("drivers.openFolder"));
        openFolder.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(app.Drivers.Root);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(app.Drivers.Root) { UseShellExecute = true });
            }
            catch { /* nothing to show */ }
        };
        Build();
        w.Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 12,
            Children =
            {
                new TextBlock { Text = Loc.T("drivers.intro"), TextWrapping = TextWrapping.Wrap, Foreground = Palette.Text },
                new Border { Height = 1, Background = Palette.Border },
                rows,
                new Border { Height = 1, Background = Palette.Border },
                status,
                new TextBlock { Text = Loc.F("drivers.folder", app.Drivers.Root), Foreground = Palette.Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { openFolder, close } },
            },
        };
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        await w.ShowDialog(owner);
    }
}
