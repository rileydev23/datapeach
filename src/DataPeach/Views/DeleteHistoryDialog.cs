using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DataPeach.Controls;
using DataPeach.Core;
using DataPeach.Core.History;
using DataPeach.ViewModels;

namespace DataPeach.Views;

/// <summary>
/// The connection's delete history: what was deleted, when and from where, the rows it removed and the INSERTs that
/// put them back. Restoring asks first (always); the script can also go to a console to be edited.
/// </summary>
public static class DeleteHistoryDialog
{
    private static readonly int[] Retention = { 7, 30, 90, 0 };
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, DejaVu Sans Mono");

    public static async Task Show(ConnectionViewModel vm)
    {
        var owner = Dialogs.Owner;
        if (owner is null) return;
        var w = Dialogs.Make(Loc.F("history.title", vm.Entry.Name), 940);
        w.SizeToContent = SizeToContent.Manual;
        w.Height = 600;
        w.CanResize = true;

        var list = new ListBox { Background = Palette.Bg };
        var sql = new SelectableTextBlock { FontFamily = Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Text };
        var note = new TextBlock { Foreground = Palette.Amber, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var script = new SelectableTextBlock { FontFamily = Mono, FontSize = 12, TextWrapping = TextWrapping.NoWrap, Foreground = Palette.Text };
        var info = new TextBlock { Foreground = Palette.Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var restore = Dialogs.Btn(Loc.T("history.restore"), true);
        var toConsole = Dialogs.Btn(Loc.T("history.toConsole"));
        var copy = Dialogs.Btn(Loc.T("history.copy"));
        var remove = Dialogs.Btn(Loc.T("history.remove"));
        var empty = new TextBlock { Text = Loc.T("history.empty"), Foreground = Palette.Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 8) };

        List<DeleteRecord> records = new();
        DeleteRecord? Current() => list.SelectedItem as DeleteRecord;

        list.ItemTemplate = new FuncDataTemplate<DeleteRecord>((r, _) =>
        {
            if (r is null) return new TextBlock();
            var rows = r.HasBackup ? Loc.Plural("common.rows", r.Rows.Count) : Loc.T("history.noBackup");
            var state = r.Restored is not null ? " · " + Loc.T("history.restoredMark") : "";
            return new StackPanel
            {
                Margin = new Thickness(6, 4),
                Children =
                {
                    new TextBlock { Text = r.TableDisplay, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock
                    {
                        Text = $"{r.Time.ToLocalTime():yyyy-MM-dd HH:mm} · {Loc.T("history.source." + r.Source)} · {rows}{state}",
                        Foreground = r.Restored is not null ? Palette.Green : r.HasBackup ? Palette.Dim : Palette.Amber, FontSize = 11,
                    },
                },
            };
        });

        void ShowCurrent()
        {
            var r = Current();
            bool has = r is not null;
            // Restored rows are back in the table: a second restore would duplicate them or fail on their key.
            restore.IsEnabled = has && r!.HasBackup && r.Restored is null;
            toConsole.IsEnabled = copy.IsEnabled = has && r!.HasBackup;
            remove.IsEnabled = has;
            if (r is null)
            {
                sql.Text = script.Text = info.Text = note.Text = "";
                return;
            }
            sql.Text = r.Sql;
            note.Text = r.Note is null ? r.Partial ? Loc.T("history.script.partial") : "" : Loc.F("history.noteNoBackup", r.Note);
            note.IsVisible = note.Text.Length > 0;
            info.Text = Loc.F("history.info", r.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                r.Affected >= 0 ? Loc.Plural("common.rows", r.Affected) : "?",
                r.Restored is { } at ? Loc.F("history.restoredAt", at.ToLocalTime().ToString("yyyy-MM-dd HH:mm")) : "");
            if (!r.HasBackup) { script.Text = ""; return; }
            // Big backups: the preview shows the start; the console and the clipboard get all of it.
            var text = DeleteInverse.Script(r, vm.Dialect);
            var lines = text.Split('\n');
            script.Text = lines.Length > 200 ? string.Join('\n', lines.Take(200)) + "\n-- " + Loc.F("history.more", lines.Length - 200) : text;
        }

        void Reload(string? keepId = null)
        {
            records = vm.History.List(vm.Entry.Id);
            list.ItemsSource = records;
            empty.IsVisible = records.Count == 0;
            list.SelectedItem = records.FirstOrDefault(r => r.Id == keepId) ?? records.FirstOrDefault();
            ShowCurrent();
            vm.RefreshHistoryCount();
        }

        list.SelectionChanged += (_, _) => ShowCurrent();
        restore.Click += async (_, _) =>
        {
            if (Current() is not { } r) return;
            if (await vm.RestoreAsync(r)) Reload(r.Id);
        };
        toConsole.Click += (_, _) =>
        {
            if (Current() is not { } r) return;
            vm.OpenInConsole(DeleteInverse.Script(r, vm.Dialect), r.Database);
            w.Close();
        };
        copy.Click += async (_, _) =>
        {
            if (Current() is not { } r) return;
            await Dialogs.CopyAsync(DeleteInverse.Script(r, vm.Dialect));
            vm.Notice = Loc.T("history.copied");
        };
        remove.Click += async (_, _) =>
        {
            if (Current() is not { } r) return;
            if (!await Dialogs.Confirm(Loc.T("history.remove"), Loc.F("history.remove.text", r.TableDisplay, r.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm")), Loc.T("history.remove")))
                return;
            vm.History.Remove(r);
            Reload();
        };

        // Header: how long deletes are kept, as the joined switch of the design system.
        var keep = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < Retention.Length; i++)
        {
            int days = Retention[i];
            var b = new Button { Content = days == 0 ? Loc.T("history.keep.forever") : Loc.Plural("history.keep.days", days) };
            b.Classes.Add("mode");
            b.Classes.Add(i == 0 ? "left" : i == Retention.Length - 1 ? "right" : "mid");
            if (days == vm.HistoryDays) b.Classes.Add("active");
            b.Click += (_, _) =>
            {
                foreach (var other in keep.Children.OfType<Button>()) other.Classes.Set("active", ReferenceEquals(other, b));
                vm.SetHistoryDays(days);
                Reload(Current()?.Id);
            };
            keep.Children.Add(b);
        }
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 10),
            Children = { new TextBlock { Text = Loc.T("history.keep"), Foreground = Palette.Text, VerticalAlignment = VerticalAlignment.Center }, keep },
        };

        static Control Section(string title, Control body, double maxHeight) => new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, Foreground = Palette.Text, FontSize = 12 },
                new Border
                {
                    Background = Palette.Code, BorderBrush = Palette.Border, BorderThickness = new Thickness(1), Padding = new Thickness(8, 6),
                    Child = new ScrollViewer { MaxHeight = maxHeight, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = body },
                },
            },
        };

        var detail = new DockPanel { Margin = new Thickness(12, 0, 0, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 10, 0, 0), Children = { restore, toConsole, copy, remove } };
        DockPanel.SetDock(actions, Dock.Bottom);
        detail.Children.Add(actions);
        var top = new StackPanel { Spacing = 10, Children = { info, note, Section(Loc.T("history.ran"), sql, 110) } };
        DockPanel.SetDock(top, Dock.Top);
        detail.Children.Add(top);
        detail.Children.Add(new DockPanel
        {
            Margin = new Thickness(0, 10, 0, 0),
            Children = { Section(Loc.T("history.inverse"), script, 2000) },
        });

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("300,*") };
        var left = new Panel { Children = { list, empty } };
        body.Children.Add(new Border { BorderBrush = Palette.Border, BorderThickness = new Thickness(1), Child = left });
        Grid.SetColumn(detail, 1);
        body.Children.Add(detail);

        var close = Dialogs.Btn(Loc.T("common.close"));
        close.Click += (_, _) => w.Close();
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(close);
        footer.Children.Add(new TextBlock { Text = Loc.F("history.folder", vm.History.Root), Foreground = Palette.Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(body);
        w.Content = root;
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        Reload();
        await w.ShowDialog(owner);
    }
}
