using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DataPeach.Controls;
using DataPeach.Core;
using DataPeach.Core.Db;
using DataPeach.Core.Drivers;
using DataPeach.Services;

namespace DataPeach.Views;

/// <summary>
/// New / edit connection: engine, where it is, how careful to be (normal, production, read-only) and where its
/// consoles are saved. "Probar" connects once (downloading the driver first if needed); "Guardar" stores the
/// settings in state.json and the password, encrypted, in secrets.json.
/// </summary>
public static class ConnectionDialog
{
    /// <summary>
    /// Edits <paramref name="entry"/> in place (or a new one when null) and returns it; null when cancelled.
    /// <paramref name="isNew"/> with an entry: a prefilled new connection (duplicate).
    /// </summary>
    public static async Task<ConnectionEntry?> Show(ConnectionEntry? entry, AppServices app, bool isNew = false, string? password = null)
    {
        var owner = Dialogs.Owner;
        if (owner is null) return null;
        bool creating = entry is null || isNew;
        var e = entry?.Clone() ?? new ConnectionEntry { Kind = DbKind.Postgres, Host = "localhost", Port = 5432 };
        password ??= entry is not null && !isNew ? app.Secrets.Get(entry.Id) : null;

        var w = Dialogs.Make(creating ? Loc.T("conn.dlg.newTitle") : Loc.T("conn.dlg.editTitle"), 560);
        ConnectionEntry? result = null;

        // ---- fields
        TextBox Box(string text, string? watermark = null) => new() { Text = text, Watermark = watermark };
        var name = Box(e.Name, Loc.T("conn.dlg.name.watermark"));
        var host = Box(e.Host, "localhost");
        var port = Box(e.Port > 0 ? e.Port.ToString() : "", "");
        var database = Box(e.Database);
        var user = Box(e.User);
        var pwd = new TextBox { Text = password ?? "", PasswordChar = '•', Watermark = Loc.T("conn.dlg.password.watermark") };
        var file = Box(e.FilePath, Loc.T("conn.dlg.file.watermark"));
        var options = Box(e.Options, Loc.T("conn.dlg.options.watermark"));
        var queries = Box(e.QueriesFolder ?? "", Loc.T("conn.dlg.queries.watermark"));
        var testResult = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, IsVisible = false };
        var driverInfo = new TextBlock { Classes = { "dim" }, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        var modeNote = new TextBlock { Classes = { "dim" }, FontSize = 11, TextWrapping = TextWrapping.Wrap };

        Control Labeled(string label, Control field) => new StackPanel
        {
            Spacing = 4,
            Children = { new TextBlock { Text = label, Foreground = Palette.Dim, FontSize = 12 }, field },
        };

        Control WithBrowse(TextBox box, Func<Task<string?>> pick)
        {
            var b = new Button { Content = Loc.T("common.browse") };
            b.Click += async (_, _) => { if (await pick() is { } p) box.Text = p; };
            var dp = new DockPanel();
            DockPanel.SetDock(b, Dock.Right);
            b.Margin = new Thickness(6, 0, 0, 0);
            dp.Children.Add(b);
            dp.Children.Add(box);
            return dp;
        }

        // Engine switch.
        var kinds = DbKinds.All;
        var engineButtons = new StackPanel { Orientation = Orientation.Horizontal };
        // Mode switch.
        var modes = new[] { ConnectionMode.Normal, ConnectionMode.Production, ConnectionMode.ReadOnly };
        var modeButtons = new StackPanel { Orientation = Orientation.Horizontal };

        var serverFields = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,110"), RowDefinitions = new RowDefinitions("Auto,10,Auto,10,Auto") };
        void Place(Control c, int row, int col, int span = 1) { Grid.SetRow(c, row); Grid.SetColumn(c, col); Grid.SetColumnSpan(c, span); serverFields.Children.Add(c); }
        Place(Labeled(Loc.T("conn.dlg.host"), host), 0, 0);
        Place(Labeled(Loc.T("conn.dlg.port"), port), 0, 2);
        Place(Labeled(Loc.T("conn.dlg.database"), database), 2, 0, 3);
        var userGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*") };
        var userField = Labeled(Loc.T("conn.dlg.user"), user);
        var pwdField = Labeled(Loc.T("conn.dlg.password"), pwd);
        Grid.SetColumn(pwdField, 2);
        userGrid.Children.Add(userField);
        userGrid.Children.Add(pwdField);
        Place(userGrid, 4, 0, 3);

        var fileField = Labeled(Loc.T("conn.dlg.file"), WithBrowse(file, () => Dialogs.PickFile(Loc.T("home.pickFile"), Loc.T("home.pickFile.filter"), DbKinds.FilePatterns)));

        void UpdateKind()
        {
            for (int i = 0; i < kinds.Length; i++) ((Button)engineButtons.Children[i]).Classes.Set("active", kinds[i] == e.Kind);
            serverFields.IsVisible = !e.IsFile;
            fileField.IsVisible = e.IsFile;
            var spec = DriverCatalog.For(e.Kind);
            var installed = app.Drivers.Installed(e.Kind);
            driverInfo.Text = installed is null
                ? Loc.F("conn.dlg.driver.missing", spec.Package, spec.Version)
                : Loc.F("conn.dlg.driver.installed", installed.Package, installed.Version);
        }

        void UpdateMode()
        {
            for (int i = 0; i < modes.Length; i++) ((Button)modeButtons.Children[i]).Classes.Set("active", modes[i] == e.Mode);
            modeNote.Text = Loc.T("conn.dlg.mode." + e.Mode.ToString().ToLowerInvariant() + ".note");
        }

        for (int i = 0; i < kinds.Length; i++)
        {
            var k = kinds[i];
            var b = new Button { Content = DbKinds.Name(k) };
            b.Classes.Add("mode");
            b.Classes.Add(i == 0 ? "left" : i == kinds.Length - 1 ? "right" : "mid");
            b.Click += (_, _) =>
            {
                if (e.Kind == k) return;
                // Keep a port the user typed; swap the default one.
                if (port.Text == DbKinds.DefaultPort(e.Kind).ToString() || string.IsNullOrWhiteSpace(port.Text))
                    port.Text = DbKinds.DefaultPort(k) > 0 ? DbKinds.DefaultPort(k).ToString() : "";
                e.Kind = k;
                UpdateKind();
            };
            engineButtons.Children.Add(b);
        }
        for (int i = 0; i < modes.Length; i++)
        {
            var m = modes[i];
            var b = new Button { Content = Loc.T("conn.mode." + m.ToString().ToLowerInvariant()) };
            b.Classes.Add("mode");
            b.Classes.Add(i == 0 ? "left" : i == modes.Length - 1 ? "right" : "mid");
            b.Click += (_, _) => { e.Mode = m; UpdateMode(); };
            modeButtons.Children.Add(b);
        }

        // Reads the boxes into the entry; returns an error to show, or null.
        string? Collect(ConnectionEntry target)
        {
            target.Kind = e.Kind;
            target.Mode = e.Mode;
            target.Host = host.Text?.Trim() ?? "";
            target.Database = database.Text?.Trim() ?? "";
            target.User = user.Text?.Trim() ?? "";
            target.FilePath = file.Text?.Trim().Trim('"') ?? "";
            target.Options = options.Text?.Trim() ?? "";
            target.QueriesFolder = string.IsNullOrWhiteSpace(queries.Text) ? null : queries.Text.Trim().Trim('"');
            var portText = port.Text?.Trim() ?? "";
            if (portText.Length == 0) target.Port = 0;
            else if (int.TryParse(portText, out var p) && p is > 0 and < 65536) target.Port = p;
            else return Loc.F("conn.dlg.badPort", portText);
            if (target.IsFile && target.FilePath.Length == 0) return Loc.T("conn.dlg.needFile");
            if (!target.IsFile && target.Host.Length == 0) return Loc.T("conn.dlg.needHost");
            var n = name.Text?.Trim() ?? "";
            target.Name = n.Length > 0 ? n
                : target.IsFile ? Path.GetFileName(target.FilePath)
                : (target.Database.Length > 0 ? target.Database + "@" : "") + target.Host;
            return null;
        }

        void Show(string text, bool ok)
        {
            testResult.Text = text;
            testResult.Foreground = ok ? Palette.Green : Palette.Red;
            testResult.IsVisible = true;
        }

        var test = Dialogs.Btn(Loc.T("conn.dlg.test"));
        test.Click += async (_, _) =>
        {
            var probe = e.Clone();
            if (Collect(probe) is { } error) { Show(error, false); return; }
            test.IsEnabled = false;
            try
            {
                if (!app.Drivers.IsInstalled(probe.Kind))
                {
                    Show(Loc.F("drivers.downloading", DriverCatalog.For(probe.Kind).Package), true);
                    testResult.Foreground = Palette.Dim;
                    await Task.Run(() => app.Drivers.InstallAsync(probe.Kind, null, null, CancellationToken.None));
                    UpdateKind();
                }
                var factory = await Task.Run(() => app.Drivers.Load(probe.Kind));
                Show(Loc.T("conn.dlg.testing"), true);
                testResult.Foreground = Palette.Dim;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                await using var s = new DbSession(probe, pwd.Text, factory);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var version = await Task.Run(() => s.TestAsync(cts.Token));
                Show(Loc.F("conn.dlg.testOk", DbKinds.Name(probe.Kind), version, clock.ElapsedMilliseconds), true);
            }
            catch (Exception ex) { Show(ex.Message, false); }
            finally { test.IsEnabled = true; }
        };

        var save = Dialogs.Btn(Loc.T("common.save"), primary: true);
        var cancel = Dialogs.Btn(Loc.T("common.cancel"));
        void Accept()
        {
            var target = creating ? e : entry!;
            if (Collect(target) is { } error) { Show(error, false); return; }
            app.Secrets.Set(target.Id, pwd.Text);
            result = target;
            w.Close();
        }
        save.Click += (_, _) => Accept();
        cancel.Click += (_, _) => w.Close();

        var buttons = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(test, Dock.Left);
        buttons.Children.Add(test);
        buttons.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { save, cancel } });

        w.Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 12,
            Children =
            {
                Labeled(Loc.T("conn.dlg.engine"), engineButtons),
                driverInfo,
                Labeled(Loc.T("conn.dlg.name"), name),
                serverFields,
                fileField,
                Labeled(Loc.T("conn.dlg.mode"), modeButtons),
                modeNote,
                Labeled(Loc.T("conn.dlg.queries"), WithBrowse(queries, () => Dialogs.PickFolder(Loc.T("conn.dlg.queries")))),
                Labeled(Loc.T("conn.dlg.options"), options),
                testResult,
                buttons,
            },
        };
        UpdateKind();
        UpdateMode();
        w.KeyDown += (_, ev) =>
        {
            if (ev.Key == Key.Escape) w.Close();
            else if (ev.Key == Key.Enter && ev.Source is not Button) Accept();
        };
        w.Opened += (_, _) => (e.IsFile ? file : creating ? host : name).Focus();
        await w.ShowDialog(owner);
        return result;
    }
}
