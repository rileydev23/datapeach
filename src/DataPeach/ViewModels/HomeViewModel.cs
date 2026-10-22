using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataPeach.Core;
using DataPeach.Services;
using DataPeach.Views;

namespace DataPeach.ViewModels;

/// <summary>The "Conexiones" tab: saved connections, organised in collapsible groups.</summary>
public partial class HomeViewModel : TabViewModel
{
    private readonly AppServices _app;
    private readonly Action<ConnectionEntry> _open;
    private readonly Func<ConnectionEntry, Task> _reopen;

    public override bool Closable => false;
    public ObservableCollection<HomeRow> Rows { get; } = new();

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _onlyFavorites;
    [ObservableProperty] private HomeRow? _selectedRow;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _isEmpty;

    public ConnectionEntry? Selected => (SelectedRow as ConnectionRow)?.Entry;
    private Store Store => _app.Store;
    private AppState State => _app.Store.State;
    private bool IsFiltering => OnlyFavorites || Filter.Trim().Length > 0;

    public HomeViewModel(AppServices app, Action<ConnectionEntry> open, Func<ConnectionEntry, Task> reopen)
    {
        _app = app;
        _open = open;
        _reopen = reopen;
        Title = Loc.T("main.home");
        Reload();
    }

    partial void OnFilterChanged(string value) => Reload();
    partial void OnOnlyFavoritesChanged(bool value) => Reload();

    // ---------------------------------------------------------------- list

    public void Reload()
    {
        var keep = Selected;
        var q = Filter.Trim();
        bool filtering = IsFiltering;
        foreach (var c in State.Connections) c.Missing = c.IsFile && !File.Exists(c.FilePath);
        var sections = ConnectionGrouping.Build(State,
            c => (!OnlyFavorites || c.Favorite) && Dialogs.Fuzzy(c.Name + " " + c.Target + " " + DbKinds.Name(c.Kind), q),
            hideEmpty: filtering);

        Rows.Clear();
        // With no groups at all the list stays flat.
        bool headers = State.Groups.Count > 0;
        foreach (var section in sections)
        {
            // While filtering every section is expanded, so matches are never hidden.
            bool collapsed = !filtering && (section.Group?.Collapsed ?? State.UngroupedCollapsed);
            if (headers)
            {
                var s = section;
                Rows.Add(new GroupHeaderRow
                {
                    Group = s.Group,
                    Count = s.Connections.Count,
                    IsCollapsed = collapsed,
                    ToggleCommand = new RelayCommand(() => ToggleGroup(s.Group)),
                    OpenAllCommand = new RelayCommand(() => OpenAll(s.Connections)),
                });
            }
            if (collapsed) continue;
            foreach (var c in section.Connections) Rows.Add(new ConnectionRow { Entry = c, Indented = headers });
        }

        SelectedRow = keep is null ? null : Rows.OfType<ConnectionRow>().FirstOrDefault(r => r.Entry == keep);
        IsEmpty = !Rows.OfType<ConnectionRow>().Any() && !Rows.OfType<GroupHeaderRow>().Any(h => h.Count > 0);
    }

    [RelayCommand]
    private void Refresh() => Reload();

    // ---------------------------------------------------------------- groups

    public void ToggleGroup(ConnectionGroup? group)
    {
        if (IsFiltering) return; // everything is expanded while filtering
        if (group is null) State.UngroupedCollapsed = !State.UngroupedCollapsed;
        else group.Collapsed = !group.Collapsed;
        Store.Save();
        Reload();
    }

    [RelayCommand]
    private void CollapseAll() => SetAllCollapsed(true);

    [RelayCommand]
    private void ExpandAll() => SetAllCollapsed(false);

    private void SetAllCollapsed(bool collapsed)
    {
        foreach (var g in State.Groups) g.Collapsed = collapsed;
        State.UngroupedCollapsed = collapsed;
        Store.Save();
        Reload();
    }

    private void OpenAll(IReadOnlyList<ConnectionEntry> connections)
    {
        foreach (var c in connections) _open(c);
        Status = Loc.F("home.opened", Plural(connections.Count));
    }

    [RelayCommand]
    private Task NewGroup() => MoveToNewGroup(Array.Empty<ConnectionEntry>());

    private async Task MoveToNewGroup(IReadOnlyList<ConnectionEntry> connections)
    {
        var name = await Dialogs.Prompt(Loc.T("home.newGroup.title"), Loc.T("home.newGroup.label"), ConnectionGrouping.UniqueName(State, Loc.T("home.group.default")));
        if (string.IsNullOrWhiteSpace(name)) return;
        var group = ConnectionGrouping.Ensure(State, name);
        group.Collapsed = false;
        MoveTo(connections, group);
    }

    private void MoveTo(IReadOnlyList<ConnectionEntry> connections, ConnectionGroup? group)
    {
        ConnectionGrouping.Move(State, connections, group);
        Store.Save();
        Reload();
        if (connections.Count > 0)
            Status = group is null
                ? Loc.F("home.movedOut", Plural(connections.Count))
                : Loc.F("home.movedTo", Plural(connections.Count), group.Name);
    }

    private async Task RenameGroup(ConnectionGroup group)
    {
        var name = await Dialogs.Prompt(Loc.T("home.renameGroup.title"), Loc.T("home.renameGroup.label"), group.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == group.Name) return;
        if (!ConnectionGrouping.Rename(State, group, name))
        {
            await Dialogs.Message(Loc.T("home.renameGroup.title"), Loc.F("home.renameGroup.exists", name.Trim()));
            return;
        }
        Store.Save();
        Reload();
    }

    private async Task DeleteGroup(ConnectionGroup group, bool removeConnections)
    {
        var count = ConnectionGrouping.Members(State, group).Count();
        var message = removeConnections
            ? Loc.F("home.deleteGroup.withConnections", group.Name, Plural(count))
            : Loc.F("home.deleteGroup.keepConnections", group.Name, Plural(count));
        if (count > 0 && !await Dialogs.Confirm(Loc.T("home.deleteGroup.title"), message, Loc.T("home.deleteGroup.ok"))) return;
        ConnectionGrouping.Delete(State, group, removeConnections);
        Store.Save();
        Reload();
    }

    private static string Plural(int n) => Loc.Plural("common.connections", n);

    // ---------------------------------------------------------------- menus

    public IReadOnlyList<MenuAction> MenuForGroup(GroupHeaderRow header)
    {
        var g = header.Group;
        var connections = ConnectionGrouping.Members(State, g).ToList();
        var items = new List<MenuAction>
        {
            MenuAction.Sync(Loc.F("home.menu.openAll", connections.Count), () => OpenAll(connections)),
            MenuAction.Sync(header.IsCollapsed ? Loc.T("home.menu.expand") : Loc.T("home.menu.collapse"), () => ToggleGroup(g)),
        };
        if (g is not null)
        {
            items.Add(MenuAction.Separator);
            items.Add(new MenuAction(Loc.T("home.menu.renameGroup"), () => RenameGroup(g)));
            items.Add(new MenuAction(Loc.T("home.menu.deleteGroup"), () => DeleteGroup(g, removeConnections: false)));
            items.Add(new MenuAction(Loc.T("home.menu.removeGroup"), () => DeleteGroup(g, removeConnections: true)));
        }
        return MenuList.Clean(items);
    }

    public IReadOnlyList<MenuAction> MenuForConnections(IReadOnlyList<ConnectionEntry> connections)
    {
        if (connections.Count == 0) return Array.Empty<MenuAction>();
        var single = connections.Count == 1 ? connections[0] : null;
        var items = new List<MenuAction>
        {
            single is not null
                ? MenuAction.Sync(Loc.T("common.open"), () => Open(single))
                : MenuAction.Sync(Loc.F("home.menu.openN", connections.Count), () => OpenAll(connections)),
        };
        if (single is not null)
        {
            items.Add(new MenuAction(Loc.T("home.menu.edit"), () => EditAsync(single)));
            items.Add(new MenuAction(Loc.T("home.menu.duplicate"), () => DuplicateAsync(single)));
            items.Add(MenuAction.Sync(Loc.T("home.menu.favorite"), () => ToggleFavorite(single)));
        }

        items.Add(MenuAction.Separator);
        var targets = new List<MenuAction>();
        foreach (var g in State.Groups)
        {
            var group = g;
            if (connections.All(c => ConnectionGrouping.Find(State, c.Group) == group)) continue;
            targets.Add(MenuAction.Sync(group.Name, () => MoveTo(connections, group)));
        }
        if (targets.Count > 0) targets.Add(MenuAction.Separator);
        targets.Add(new MenuAction(Loc.T("home.menu.newGroup"), () => MoveToNewGroup(connections)));
        items.Add(MenuAction.Submenu(connections.Count == 1 ? Loc.T("home.menu.moveTo") : Loc.F("home.menu.moveNTo", connections.Count), targets));
        if (connections.Any(c => ConnectionGrouping.Find(State, c.Group) is not null))
            items.Add(MenuAction.Sync(Loc.T("home.menu.ungroup"), () => MoveTo(connections, null)));

        if (single is { IsFile: true })
        {
            items.Add(MenuAction.Separator);
            items.Add(MenuAction.Sync(Loc.T("home.menu.showFile"), () => ShowInFolder(single.FilePath)));
        }
        items.Add(MenuAction.Separator);
        items.Add(new MenuAction(single is not null ? Loc.T("home.menu.remove") : Loc.F("home.menu.removeN", connections.Count), () => RemoveManyAsync(connections)));
        return MenuList.Clean(items);
    }

    // ---------------------------------------------------------------- connections

    [RelayCommand]
    private void Open(ConnectionEntry? entry)
    {
        entry ??= Selected;
        if (entry is null) return;
        _open(entry);
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var entry = await ConnectionDialog.Show(null, _app);
        if (entry is null) return;
        Store.Add(entry);
        Reload();
        SelectedRow = Rows.OfType<ConnectionRow>().FirstOrDefault(r => r.Entry == entry);
        Status = Loc.F("home.added", entry.Name);
        _open(entry);
    }

    private async Task EditAsync(ConnectionEntry entry)
    {
        var edited = await ConnectionDialog.Show(entry, _app);
        if (edited is null) return;
        Store.Save();
        Reload();
        Status = Loc.F("home.saved", entry.Name);
        await _reopen(entry);
    }

    private async Task DuplicateAsync(ConnectionEntry entry)
    {
        var copy = entry.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = entry.Name + " " + Loc.T("home.copySuffix");
        copy.Favorite = false;
        var pwd = _app.Secrets.Get(entry.Id);
        var edited = await ConnectionDialog.Show(copy, _app, isNew: true, password: pwd);
        if (edited is null) return;
        Store.Add(copy);
        Reload();
    }

    /// <summary>Dropped SQLite files: each becomes a connection (or reuses its own) and opens.</summary>
    public void AddFiles(IEnumerable<string> files)
    {
        ConnectionEntry? last = null;
        foreach (var f in files)
        {
            last = Store.ForFile(f);
            _open(last);
        }
        Reload();
        if (last is not null) Status = Loc.F("home.added", last.Name);
    }

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        var path = await Dialogs.PickFile(Loc.T("home.pickFile"), Loc.T("home.pickFile.filter"), DbKinds.FilePatterns);
        if (path is not null) AddFiles(new[] { path });
    }

    [RelayCommand]
    private Task Drivers() => DriversDialog.Show(_app);

    public async Task RemoveManyAsync(IReadOnlyList<ConnectionEntry> connections)
    {
        var text = connections.Count == 1 ? Loc.F("home.remove.one", connections[0].Name) : Loc.F("home.remove.many", connections.Count);
        if (!await Dialogs.Confirm(Loc.T("home.remove.title"), text, Loc.T("home.remove.ok"))) return;
        foreach (var c in connections)
        {
            Store.Remove(c);
            _app.Secrets.Set(c.Id, null);
        }
        Reload();
        Status = connections.Count == 1
            ? Loc.F("home.removedOne", connections[0].Name)
            : Loc.F("home.removedMany", connections.Count);
    }

    [RelayCommand]
    private void ToggleFavorite(ConnectionEntry? entry)
    {
        entry ??= Selected;
        if (entry is null) return;
        entry.Favorite = !entry.Favorite;
        Store.Save();
        Reload();
    }

    private void ShowInFolder(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex) { Status = ex.Message; }
    }
}
