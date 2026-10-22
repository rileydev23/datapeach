using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DataPeach.Core;
using DataPeach.Services;
using DataPeach.ViewModels;
using DataPeach.Views;

namespace DataPeach;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        TabStrip.Install();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var app = new AppServices(PeachApps.SettingsFolder("DataPeach"));
            var store = app.Store;
            // Before any view or view model exists: they read their texts once, when built.
            Loc.Init(store.State.Language);
#if !DEBUG
            // Debug runs stay anonymous so they never replace the location of the real build.
            PeachApps.Announce("DataPeach");
#endif

            var vm = new MainWindowViewModel(app);
            if (desktop.Args is { Length: > 0 } args) vm.OpenFromArgs(args);
            // Invisible until placed, so the user never sees it jump from a default spot to the saved one.
            var window = new MainWindow { DataContext = vm, Opacity = 0, WindowStartupLocation = WindowStartupLocation.Manual };
            PlaceWindow(window, store.State, shown: false);
            SingleInstance.Received += args => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                vm.OpenFromArgs(args);
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
            });
            window.Opened += (_, _) =>
            {
                PlaceWindow(window, store.State, shown: true);
                window.Opacity = 1;
                RunDebugActions(vm);
            };
            window.Closing += (_, _) =>
            {
                var st = store.State;
                st.Maximized = window.WindowState == WindowState.Maximized;
                if (window.WindowState == WindowState.Normal)
                {
                    st.Width = window.ClientSize.Width;
                    st.Height = window.ClientSize.Height;
                    st.X = window.Position.X;
                    st.Y = window.Position.Y;
                }
                vm.Shutdown();
            };
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Debug builds only: DATAPEACH_DEBUG_ACTIONS="driver|sql:select 1;|run|table:users|staging|history|select:name|about" drives the UI for
    /// screenshots without synthesizing keystrokes. Acts on the selected connection tab.
    /// </summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private static void RunDebugActions(MainWindowViewModel vm)
    {
        var spec = Environment.GetEnvironmentVariable("DATAPEACH_DEBUG_ACTIONS");
        if (string.IsNullOrEmpty(spec)) return;
        Avalonia.Threading.DispatcherTimer.RunOnce(async () =>
        {
            foreach (var action in spec.Split('|'))
            {
                var c = vm.SelectedTab as ConnectionViewModel;
                if (action == "about") vm.ShowAboutCommand.Execute(null);
                else if (action == "newconn") _ = vm.Home.AddCommand.ExecuteAsync(null);
                else if (action == "drivers") _ = vm.Home.DriversCommand.ExecuteAsync(null);
                else if (action.StartsWith("open:") && vm.Store.State.Connections.FirstOrDefault(x => x.Name == action[5..]) is { } e) vm.OpenConnection(e);
                else if (c is null) continue;
                else if (action.StartsWith("sql:") && c.SelectedDoc is ConsoleViewModel con) con.Document.Text = action[4..].Replace("\\n", "\n");
                else if (action == "run" && c.SelectedDoc is ConsoleViewModel run) await run.RunAllCommand.ExecuteAsync(null);
                else if (action.StartsWith("table:")) { await c.EnsureSessionAsync(); await c.RefreshSchemaAsync(); if (c.Schema.Find(null, action[6..]) is { } t) c.OpenTable(t); }
                else if (action.StartsWith("expand:") && c.SchemaRows.FirstOrDefault(r => r.Text == action[7..]) is { } row) c.Toggle(row);
                else if (action.StartsWith("select:") && c.SchemaRows.FirstOrDefault(r => r.Text == action[7..]) is { } pick) c.SelectedRow = pick;
                else if (action == "history") _ = c.ShowHistoryCommand.ExecuteAsync(null);
                else if (action.StartsWith("exec:") && c.Schema.Routines.FirstOrDefault(r => r.Name == action[5..]) is { } routine) _ = c.ExecuteRoutineAsync(routine);
                else if (action.StartsWith("code:") && c.Schema.Routines.FirstOrDefault(r => r.Name == action[5..]) is { } code) await c.OpenRoutineSourceAsync(code);
                else if (action == "driver") await c.DownloadDriverCommand.ExecuteAsync(null);
                else if (action == "staging" && c.SelectedDoc is TableDataViewModel tv && tv.Result is { } r)
                {
                    // Sample pending changes for screenshots: an edit, a deletion and a new row.
                    r.SetValue(0, 1, "Ana María Soto");
                    r.DeleteRows(new[] { 2 });
                    r.AddRow();
                    r.SetValue(r.RowCount - 1, 1, "Nuevo cliente");
                }
                else if (action == "apply") await c.ApplyStagingCommand.ExecuteAsync(null);
                else if (action.StartsWith("wait:")) await Task.Delay(int.Parse(action[5..]));
            }
        }, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Teams-style placement: default ~1200x760 DIP (never more than 92% of the work area), remembers
    /// size/position/maximized, picks the monitor the window was last on, and always keeps the whole
    /// frame (title bar included) inside the work area.
    /// </summary>
    private static void PlaceWindow(Window w, AppState st, bool shown)
    {
        var screens = w.Screens;
        var primary = screens.Primary;
        if (primary is null) return;
        // Maximized windows were fully set up before showing; moving them again would shift them.
        if (shown && st.Maximized) return;

        // Monitor: the one containing the saved position, otherwise the primary one.
        var screen = primary;
        if (st.X is int sx && st.Y is int sy)
            screen = screens.All.FirstOrDefault(s => s.WorkingArea.Contains(new PixelPoint(sx + 60, sy + 20))) ?? primary;

        double scale = screen.Scaling;
        var wa = screen.WorkingArea;

        // Window.Height excludes the title bar/borders: measure them once the window exists.
        double chrome = shown && w.FrameSize is { } f ? Math.Max(0, f.Height - w.Height) : 40;
        double frameW = shown && w.FrameSize is { } f2 ? Math.Max(0, f2.Width - w.Width) : 0;

        double maxW = wa.Width / scale - frameW;
        double maxH = wa.Height / scale - chrome;

        bool hasSize = st.Width >= 800 && st.Height >= 500;
        double width = hasSize ? st.Width : 1200;
        double height = hasSize ? st.Height : 760;
        width = Math.Max(Math.Min(width, maxW * (hasSize ? 0.96 : 0.92)), Math.Min(800, maxW));
        height = Math.Max(Math.Min(height, maxH * 0.96), Math.Min(500, maxH));
        w.Width = width;
        w.Height = height;

        // Position: saved one if it was on a screen, else centered; then pushed fully inside.
        double totalW = (width + frameW) * scale, totalH = (height + chrome) * scale;
        int x, y;
        if (st.X is int px && st.Y is int py && ReferenceEquals(screen, screens.All.FirstOrDefault(s => s.WorkingArea.Contains(new PixelPoint(px + 60, py + 20)))))
        {
            x = px; y = py;
        }
        else
        {
            x = wa.X + (int)((wa.Width - totalW) / 2);
            y = wa.Y + (int)((wa.Height - totalH) / 2);
        }
        x = Math.Max(wa.X, Math.Min(x, wa.X + wa.Width - (int)totalW));
        y = Math.Max(wa.Y, Math.Min(y, wa.Y + wa.Height - (int)totalH));
        w.Position = new PixelPoint(x, y);

        if (st.Maximized) w.WindowState = WindowState.Maximized;
    }
}
