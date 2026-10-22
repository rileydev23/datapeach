using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataPeach.Core;
using DataPeach.Services;
using DataPeach.Views;

namespace DataPeach.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    public AppServices App { get; }
    public Store Store => App.Store;
    public HomeViewModel Home { get; }
    /// <summary>Every tab, Home first (order used by Ctrl+Tab and Ctrl+1..9).</summary>
    public ObservableCollection<TabViewModel> Tabs { get; } = new();
    /// <summary>Connection tabs only: the tab strip. Home has its own button.</summary>
    public ObservableCollection<ConnectionViewModel> ConnectionTabs { get; } = new();

    [ObservableProperty] private TabViewModel? _selectedTab;
    private readonly bool _ready;

    public MainWindowViewModel(AppServices app)
    {
        App = app;
        Home = new HomeViewModel(app, OpenConnection, ReopenAsync);
        Tabs.Add(Home);
        SelectedTab = Home;

        var active = Store.State.ActiveTab;
        foreach (var id in Store.State.OpenTabs.ToList())
            if (Store.Find(id) is { } e) AddTab(e, select: false);
        SelectedTab = Tabs.OfType<ConnectionViewModel>().FirstOrDefault(t => t.Entry.Id == active) ?? (TabViewModel)Home;
        _ready = true;
    }

    /// <summary><c>DataPeach.exe &lt;archivo.db&gt;</c>: a SQLite file becomes (or reuses) a connection and opens.</summary>
    public void OpenFromArgs(IEnumerable<string> args)
    {
        foreach (var raw in args)
        {
            string path;
            try { path = Path.GetFullPath(raw); } catch { continue; }
            if (File.Exists(path)) OpenConnection(Store.ForFile(path));
        }
        Home.Reload();
    }

    public void OpenConnection(ConnectionEntry entry)
    {
        Store.Touch(entry);
        AddTab(entry, select: true);
        SaveTabs();
    }

    private void AddTab(ConnectionEntry entry, bool select)
    {
        var existing = ConnectionTabs.FirstOrDefault(t => t.Entry.Id == entry.Id);
        if (existing is not null)
        {
            if (select) SelectedTab = existing;
            return;
        }
        var tab = new ConnectionViewModel(entry, App);
        tab.CloseCommand = new AsyncRelayCommand(() => CloseTabAsync(tab));
        Tabs.Add(tab);
        ConnectionTabs.Add(tab);
        if (select) SelectedTab = tab;
    }

    /// <summary>A connection was edited in the Home: its open tab starts over with the new settings.</summary>
    public async Task ReopenAsync(ConnectionEntry entry)
    {
        var tab = ConnectionTabs.FirstOrDefault(t => t.Entry.Id == entry.Id);
        if (tab is null) return;
        int index = ConnectionTabs.IndexOf(tab);
        bool selected = SelectedTab == tab;
        if (!await CloseTabAsync(tab)) return;
        AddTab(entry, selected);
        // Back where it was in the strip.
        var fresh = ConnectionTabs[^1];
        ConnectionTabs.Move(ConnectionTabs.Count - 1, Math.Min(index, ConnectionTabs.Count - 1));
        Tabs.Move(Tabs.IndexOf(fresh), Math.Min(index + 1, Tabs.Count - 1));
        SaveTabs();
    }

    public async Task<bool> CloseTabAsync(TabViewModel tab)
    {
        if (!tab.Closable) return false;
        if (tab is ConnectionViewModel c && c.HasUnsavedChanges &&
            !await Dialogs.Confirm(Loc.T("common.unsaved"), Loc.F("main.closeConnection.text", c.Title, Loc.Plural("staging.count", c.Staging.Count)), Loc.T("common.close")))
            return false;
        int idx = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (tab is ConnectionViewModel conn)
        {
            ConnectionTabs.Remove(conn);
            conn.SaveSession();
            await conn.DisposeAsync();
        }
        if (SelectedTab == tab || SelectedTab is null)
            SelectedTab = Tabs[Math.Clamp(idx - 1, 0, Tabs.Count - 1)];
        SaveTabs();
        return true;
    }

    /// <summary>Ctrl+W: closes the current console or table; with none open, the connection tab.</summary>
    [RelayCommand]
    private async Task CloseCurrentAsync()
    {
        if (SelectedTab is ConnectionViewModel c && c.CloseCurrentDoc()) return;
        if (SelectedTab is { Closable: true } t) await CloseTabAsync(t);
    }

    [RelayCommand]
    private void NextTab()
    {
        if (Tabs.Count < 2 || SelectedTab is null) return;
        SelectedTab = Tabs[(Tabs.IndexOf(SelectedTab) + 1) % Tabs.Count];
    }

    [RelayCommand]
    private void PreviousTab()
    {
        if (Tabs.Count < 2 || SelectedTab is null) return;
        SelectedTab = Tabs[(Tabs.IndexOf(SelectedTab) - 1 + Tabs.Count) % Tabs.Count];
    }

    [RelayCommand]
    private void SelectTab(string? number)
    {
        if (int.TryParse(number, out var n) && n >= 1 && n <= Tabs.Count) SelectedTab = Tabs[n - 1];
    }

    /// <summary>The settings folder; Debug builds can show a generic one (DATAPEACH_DEMO_DATAFOLDER) for screenshots.</summary>
    private string ShownDataFolder =>
#if DEBUG
        Environment.GetEnvironmentVariable("DATAPEACH_DEMO_DATAFOLDER") ??
#endif
        Store.DataFolder;

    [RelayCommand]
    private Task ShowAbout() => Dialogs.About(Loc.T("dlg.about.text"), ShownDataFolder, Shortcuts.Groups, Store, () =>
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Store.DataFolder) { UseShellExecute = true }); } catch { }
    });

    /// <summary>Ctrl+P: inside a connection, jump to a table; on the home tab, pick a connection.</summary>
    [RelayCommand]
    private async Task QuickOpenAsync()
    {
        if (SelectedTab is ConnectionViewModel c) await c.QuickOpenTableAsync();
        else await QuickOpenConnectionAsync();
    }

    [RelayCommand]
    private async Task QuickOpenConnectionAsync()
    {
        var picked = await Dialogs.QuickOpen(Store.State.Connections
            .OrderByDescending(r => r.Favorite).ThenByDescending(r => r.LastOpened).ToList());
        if (picked is not null) OpenConnection(picked);
    }

    public bool IsHomeSelected => SelectedTab == Home;

    /// <summary>Selection of the tab strip; null while Home is shown (a null coming back from the strip is ignored).</summary>
    public ConnectionViewModel? SelectedConnectionTab
    {
        get => SelectedTab as ConnectionViewModel;
        set { if (value is not null) SelectedTab = value; }
    }

    [RelayCommand]
    private void ShowHome() => SelectedTab = Home;

    /// <summary>Next to the app name in the title bar: the open connection and where it points.</summary>
    public string Subtitle => SelectedTab is ConnectionViewModel c ? $"{c.Title}  ·  {c.Entry.Target}" : "";
    /// <summary>Taskbar and Alt+Tab text.</summary>
    public string WindowTitle => SelectedTab is ConnectionViewModel c ? $"{c.Title} — DataPeach" : "DataPeach";

    partial void OnSelectedTabChanged(TabViewModel? value)
    {
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(IsHomeSelected));
        OnPropertyChanged(nameof(SelectedConnectionTab));
        if (value is ConnectionViewModel c) _ = c.ActivateAsync();
        if (_ready) SaveTabs();
    }

    public void SaveTabs()
    {
        Store.State.OpenTabs = ConnectionTabs.Select(t => t.Entry.Id).ToList();
        Store.State.ActiveTab = (SelectedTab as ConnectionViewModel)?.Entry.Id;
        Store.Save();
    }

    public bool HasUnsavedChanges => ConnectionTabs.Any(c => c.HasUnsavedChanges);

    public void Shutdown()
    {
        SaveTabs();
        foreach (var t in ConnectionTabs)
        {
            t.SaveSession();
            try { t.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { /* closing anyway */ }
        }
    }
}
