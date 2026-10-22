using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataPeach.Controls;
using DataPeach.Core;
using DataPeach.Core.Data;
using DataPeach.Core.Db;
using DataPeach.Core.Drivers;
using DataPeach.Core.History;
using DataPeach.Core.Sql;
using DataPeach.Core.Staging;
using DataPeach.Services;
using DataPeach.Views;

namespace DataPeach.ViewModels;

/// <summary>
/// One connection tab: schema tree on the left, consoles and table tabs in the middle, details and the staging
/// area on the right. The session opens when the tab is first shown; a missing driver is offered for download.
/// </summary>
public sealed partial class ConnectionViewModel : TabViewModel, IAsyncDisposable
{
    private readonly AppServices _app;
    private DbSession? _session;
    private Task<DbSession?>? _opening;
    private CancellationTokenSource? _detailsLoad;
    /// <summary>Keys of the expandable rows toggled away from how they start (most start closed).</summary>
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    /// <summary>Sessions on the server's other databases, made the first time something runs or reads there.</summary>
    private readonly Dictionary<string, DbSession> _dbSessions = new(StringComparer.Ordinal);
    private List<DatabaseNode> _databases = new();
    private bool _databasesListed;

    public ConnectionEntry Entry { get; }
    public Dialect Dialect { get; }
    public StagingArea Staging { get; } = new();
    /// <summary>The structure of the connection's own database (on servers with several, the one it lands in).</summary>
    public SchemaSnapshot Schema { get; private set; } = SchemaSnapshot.Empty;
    /// <summary>Whether the tree shows the server's databases (SQL Server, PostgreSQL, MySQL, SAP ASE).</summary>
    public bool HasDatabases => Dialect.HasDatabases;
    public IReadOnlyList<DatabaseNode> Databases => _databases;
    /// <summary>The database the connection lands in: the one it names, or the server's default for the user.</summary>
    public string? CurrentDatabase { get; private set; }
    public ObservableCollection<SchemaRow> SchemaRows { get; } = new();
    public ObservableCollection<TabViewModel> Docs { get; } = new();
    public ObservableCollection<StagedItem> StagedItems { get; } = new();
    public ObservableCollection<DetailLine> DetailColumns { get; } = new();
    public ObservableCollection<DetailLine> DetailExtras { get; } = new();

    [ObservableProperty] private TabViewModel? _selectedDoc;
    [ObservableProperty] private SchemaRow? _selectedRow;
    [ObservableProperty] private string _treeFilter = "";
    [ObservableProperty] private string _notice = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _serverVersion = "";
    [ObservableProperty] private string? _connectError;
    [ObservableProperty] private bool _needsDriver;
    [ObservableProperty] private string _driverProgress = "";
    [ObservableProperty] private DbTable? _detailTable;
    [ObservableProperty] private DbRoutine? _detailRoutine;
    [ObservableProperty] private int _historyCount;
    [ObservableProperty] private bool _isApplying;

    public int MaxRows => _app.Store.State.MaxRows;
    public bool IsReadOnly => Entry.Mode == ConnectionMode.ReadOnly;
    public bool IsProduction => Entry.Mode == ConnectionMode.Production;
    public string EngineName => DbKinds.Name(Entry.Kind);
    public Avalonia.Media.IBrush EngineBrush => Palette.FromHex(DbKinds.Color(Entry.Kind));
    public bool HasConnectError => ConnectError is not null;
    public bool HasStaging => Staging.Count > 0;
    public string StagingTitle => Loc.Plural("staging.count", Staging.Count);
    public bool HasDetail => DetailTable is not null || DetailRoutine is not null;
    public string DetailTitle => DetailRoutine?.FullName ?? DetailTable?.FullName ?? "";
    public string DetailKind => DetailRoutine is { } r ? Loc.T(r.Kind == DbRoutineKind.Procedure ? "detail.procedure" : "detail.function")
        : DetailTable is null ? "" : DetailTable.Kind == DbObjectKind.View ? Loc.T("detail.view") : Loc.T("detail.table");
    public string HistoryTitle => HistoryCount > 0 ? Loc.F("cv.history.count", HistoryCount) : Loc.T("cv.history");
    public DeleteHistory History => _app.History;
    public int HistoryDays => _app.Store.State.DeleteHistoryDays;
    public string DriverText => Loc.F("conn.needsDriver.text", EngineName, DriverCatalog.For(Entry.Kind).Package, DriverCatalog.For(Entry.Kind).Version);
    public bool HasUnsavedChanges => Staging.Count > 0;

    public ConnectionViewModel(ConnectionEntry entry, AppServices app)
    {
        _app = app;
        Entry = entry;
        Dialect = Dialect.For(entry.Kind);
        CurrentDatabase = HasDatabases && entry.Database.Length > 0 ? entry.Database : null;
        Title = entry.Name;
        ToolTip = $"{DbKinds.Name(entry.Kind)} · {entry.Target}";
        Staging.Changed += OnStagingChanged;
        RestoreConsoles();
        RefreshHistoryCount();
    }

    public void RefreshHistoryCount() => HistoryCount = _app.History.Count(Entry.Id);
    partial void OnHistoryCountChanged(int value) => OnPropertyChanged(nameof(HistoryTitle));

    partial void OnConnectErrorChanged(string? value) => OnPropertyChanged(nameof(HasConnectError));
    partial void OnTreeFilterChanged(string value) => RebuildTree();
    partial void OnDetailRoutineChanged(DbRoutine? value) => OnDetailTableChanged(DetailTable);
    partial void OnDetailTableChanged(DbTable? value)
    {
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(DetailTitle));
        OnPropertyChanged(nameof(DetailKind));
    }

    partial void OnSelectedRowChanged(SchemaRow? value)
    {
        if (value?.Routine is { } r) ShowRoutineDetails(r);
        else if (value?.Table is { } t) ShowDetails(t);
    }

    partial void OnSelectedDocChanged(TabViewModel? value)
    {
        if (value is TableDataViewModel t) ShowDetails(t.Table);
        SaveSession();
    }

    // ------------------------------------------------------------------ session

    /// <summary>The open session, opening it (and loading the schema) the first time. Null when it failed.</summary>
    public async Task<DbSession?> EnsureSessionAsync()
    {
        if (_session is not null) return _session;
        // Callers that arrive while it is opening share the attempt; a failed one is never kept.
        _opening ??= OpenAsync();
        var task = _opening;
        var session = await task;
        if (_opening == task) _opening = null;
        return session;
    }

    /// <summary>The session that runs on <paramref name="database"/> (null: the connection's own).</summary>
    public async Task<DbSession?> EnsureSessionAsync(string? database) =>
        await EnsureSessionAsync() is { } main ? SessionFor(main, database) : null;

    private DbSession SessionFor(DbSession main, string? database)
    {
        if (!HasDatabases || database is null || database == CurrentDatabase) return main;
        if (!_dbSessions.TryGetValue(database, out var s)) _dbSessions[database] = s = main.ForDatabase(database);
        return s;
    }

    /// <summary>The database something marked with <paramref name="database"/> really runs on; null without databases.</summary>
    public string? EffectiveDatabase(string? database) => HasDatabases ? database ?? CurrentDatabase : null;

    /// <summary>
    /// The structure of <paramref name="database"/> (null: the connection's own). Empty while it has not been read;
    /// asking for it starts reading it, so a console on another database gets its completion a moment later.
    /// </summary>
    public SchemaSnapshot SchemaFor(string? database)
    {
        if (!HasDatabases) return Schema;
        var name = database ?? CurrentDatabase;
        if (_databases.FirstOrDefault(n => n.Name == name) is not { } node) return SchemaSnapshot.Empty;
        if (node.Schema is null && node.Loading is null && node.Error is null) _ = LoadDatabaseAsync(node);
        return node.Schema ?? SchemaSnapshot.Empty;
    }

    /// <summary>Every table read so far, of every database.</summary>
    public IEnumerable<DbTable> AllTables => HasDatabases ? _databases.SelectMany(n => n.Schema?.Tables ?? []) : Schema.Tables;
    private IEnumerable<DbRoutine> AllRoutines => HasDatabases ? _databases.SelectMany(n => n.Schema?.Routines ?? []) : Schema.Routines;

    private async Task<DbSession?> OpenAsync()
    {
        try
        {
            ConnectError = null;
            NeedsDriver = false;
            if (!_app.Drivers.IsInstalled(Entry.Kind))
            {
                NeedsDriver = true;
                return null;
            }
            IsBusy = true;
            Notice = Loc.F("conn.connecting", Entry.Target);
            var factory = await Task.Run(() => _app.Drivers.Load(Entry.Kind));
            var session = new DbSession(Entry, _app.Secrets.Get(Entry.Id), factory);
            await Task.Run(() => session.OpenAsync(CancellationToken.None));
            _session = session;
            ServerVersion = $"{EngineName} {session.ServerVersion}";
            Notice = Loc.F("conn.connected", Entry.Name);
            _ = RefreshSchemaAsync();
            return session;
        }
        catch (Exception ex)
        {
            ConnectError = ex.Message;
            Notice = ex.Message;
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Called when the tab is shown: connects and opens the table tabs' data.</summary>
    public async Task ActivateAsync()
    {
        if (_session is not null || _opening is not null || NeedsDriver || HasConnectError) return;
        if (await EnsureSessionAsync() is null) return;
        foreach (var t in Docs.OfType<TableDataViewModel>()) _ = t.LoadAsync();
    }

    [RelayCommand]
    private async Task Retry()
    {
        ConnectError = null;
        NeedsDriver = false;
        if (await EnsureSessionAsync() is not null)
            foreach (var t in Docs.OfType<TableDataViewModel>()) _ = t.LoadAsync();
    }

    [RelayCommand]
    private async Task DownloadDriver()
    {
        IsBusy = true;
        try
        {
            var progress = new Progress<string>(p => DriverProgress = Loc.F("drivers.downloading", p));
            if (!_app.Drivers.IsInstalled(Entry.Kind))
                await Task.Run(() => _app.Drivers.InstallAsync(Entry.Kind, null, progress, CancellationToken.None));
            DriverProgress = "";
            NeedsDriver = false;
            await Retry();
        }
        catch (Exception ex)
        {
            DriverProgress = ex.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// "Refrescar" / F5: the structure and the rows of the table being looked at (with its filter and order).
    /// Pending changes stay: they are kept by primary key, not by position.
    /// </summary>
    [RelayCommand]
    private async Task Refresh()
    {
        await RefreshSchemaAsync();
        if (SelectedDoc is TableDataViewModel current) await current.LoadAsync();
    }

    public async Task RefreshSchemaAsync()
    {
        if (await EnsureSessionAsync() is not { } session) return;
        IsBusy = true;
        try
        {
            if (HasDatabases)
            {
                await RefreshDatabasesAsync(session);
                return;
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Schema = await Task.Run(() => session.LoadSchemaAsync(CancellationToken.None));
            if (_expanded.Count == 0 && Schema.DefaultSchema is { } d && Schema.Schemas.Count > 1) _expanded.Add("s:/" + d);
            RebuildTree();
            Rebind(Schema);
            Notice = Loc.F("conn.schemaLoaded", Loc.Plural("common.tables", Schema.Tables.Count), ResultViewModel.FormatMs(clock.ElapsedMilliseconds));
        }
        catch (Exception ex) { Notice = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Lists the server's databases and reads again the ones already read, plus the connection's own (consoles
    /// complete with it). The others are read when they are opened.
    /// </summary>
    private async Task RefreshDatabasesAsync(DbSession session)
    {
        var (all, current) = await Task.Run(() => session.LoadDatabasesAsync(CancellationToken.None));
        CurrentDatabase = current;
        var old = _databases.ToDictionary(n => n.Name, StringComparer.Ordinal);
        _databases = all.Select(d => old.TryGetValue(d.Name, out var n) ? n : new DatabaseNode { Name = d.Name }).ToList();
        for (int i = 0; i < all.Count; i++) _databases[i].IsSystem = all[i].IsSystem;
        foreach (var gone in _dbSessions.Keys.Where(k => _databases.All(n => n.Name != k) || k == current).ToList())
        {
            _ = _dbSessions[gone].DisposeAsync().AsTask();
            _dbSessions.Remove(gone);
        }
        // The first time, the connection's own database opens, unless it is a system one nobody asked for
        // (SQL Server's master when the database field is empty).
        if (!_databasesListed && current is not null && _databases.FirstOrDefault(n => n.Name == current) is { } home
            && (!home.IsSystem || Entry.Database.Length > 0))
        {
            _expanded.Add("d:" + current);
            if (home.IsSystem) _expanded.Add("sys");
        }
        _databasesListed = true;
        foreach (var c in Docs.OfType<ConsoleViewModel>()) c.OnDatabasesChanged();
        RebuildTree();
        Notice = Loc.Plural("conn.databases", _databases.Count);
        var reload = _databases.Where(n => n.Schema is not null || n.Error is not null || n.Name == current || IsOpen("d:" + n.Name)).ToList();
        await Task.WhenAll(reload.Select(LoadDatabaseAsync));
    }

    /// <summary>Reads one database's structure; its row shows "Cargando…" meanwhile, or the error.</summary>
    private Task LoadDatabaseAsync(DatabaseNode node)
    {
        if (node.Loading is { } running) return running;
        var task = LoadAsync();
        if (!task.IsCompleted) node.Loading = task;
        return task;

        async Task LoadAsync()
        {
            try
            {
                if (await EnsureSessionAsync() is not { } main) return;
                node.Error = null;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var session = SessionFor(main, node.Name);
                var fresh = (await Task.Run(() => session.LoadSchemaAsync(CancellationToken.None))).Stamp(node.Name);
                if (node.Schema is null && fresh.DefaultSchema is { } d && fresh.Schemas.Count > 1) _expanded.Add($"s:{node.Name}/{d}");
                node.Schema = fresh;
                if (node.Name == CurrentDatabase) Schema = fresh;
                Rebind(fresh);
                Notice = Loc.F("conn.databaseLoaded", node.Name, Loc.Plural("common.tables", fresh.Tables.Count), ResultViewModel.FormatMs(clock.ElapsedMilliseconds));
            }
            catch (Exception ex)
            {
                node.Error = ex.Message;
                Notice = $"{node.Name}: {ex.Message}";
            }
            finally
            {
                node.Loading = null;
                RebuildTree();
            }
        }
    }

    /// <summary>"Refrescar" on a database row: reads just that one again.</summary>
    public Task RefreshDatabaseAsync(string database) =>
        _databases.FirstOrDefault(n => n.Name == database) is { } node ? LoadDatabaseAsync(node) : Task.CompletedTask;

    /// <summary>After a structure is read again its objects are new: open tabs and the detail panel move to them.</summary>
    private void Rebind(SchemaSnapshot fresh)
    {
        foreach (var t in Docs.OfType<TableDataViewModel>())
            if (t.Table.Database == fresh.Database && fresh.Find(t.Table.Schema, t.Table.Name) is { } table) t.Rebind(table);
        if (DetailTable is { } dt && dt.Database == fresh.Database) ShowDetails(fresh.Find(dt.Schema, dt.Name));
    }

    // ------------------------------------------------------------------ schema tree

    private bool IsOpen(string key, bool byDefault = false) => _expanded.Contains(key) != byDefault;

    public void RebuildTree()
    {
        var keep = SelectedRow;
        var rows = new List<SchemaRow>();
        var q = TreeFilter.Trim();
        if (q.Length > 0)
        {
            // Filtering: a flat list of matching objects of every database read so far, qualified when there are
            // several databases or schemas.
            bool qualified = HasDatabases || Schema.Schemas.Count > 1;
            foreach (var t in AllTables.Where(t => Dialogs.Fuzzy(t.Name, q)).OrderBy(t => !t.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)).ThenBy(t => t.Name.Length).Take(500))
                AddTable(rows, t, 0, qualified);
            foreach (var r in AllRoutines.Where(r => Dialogs.Fuzzy(r.Name, q)).OrderBy(r => !r.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)).ThenBy(r => r.Name.Length).Take(200))
                AddRoutine(rows, r, 0, qualified);
        }
        else if (HasDatabases)
        {
            foreach (var n in _databases.Where(n => !n.IsSystem)) AddDatabase(rows, n, 0);
            var system = _databases.Where(n => n.IsSystem).ToList();
            if (system.Count > 0)
            {
                bool open = IsOpen("sys");
                rows.Add(new SchemaRow { Kind = SchemaRowKind.Folder, Text = Loc.T("tree.system"), Detail = system.Count.ToString("N0"), Key = "sys", IsExpanded = open });
                if (open) foreach (var n in system) AddDatabase(rows, n, 1);
            }
        }
        else AddObjects(rows, Schema, null, 0);

        if (rows.Count == 0 && _session is not null)
            rows.Add(Message(q.Length > 0 ? Loc.T("tree.noMatch") : HasDatabases && !_databasesListed ? Loc.T("common.loading") : Loc.T("tree.empty"), 0));

        SchemaRows.Clear();
        foreach (var r in rows) SchemaRows.Add(r);
        if (keep is not null)
            SelectedRow = SchemaRows.FirstOrDefault(r => r.Kind == keep.Kind && r.Key == keep.Key && r.Table?.FullName == keep.Table?.FullName && r.Column?.Name == keep.Column?.Name
                                                         && r.Routine?.FullName == keep.Routine?.FullName && r.Routine?.Id == keep.Routine?.Id);
    }

    private static SchemaRow Message(string text, int level) => new() { Kind = SchemaRowKind.Message, Text = text, Level = level };

    private void AddDatabase(List<SchemaRow> rows, DatabaseNode n, int level)
    {
        var key = "d:" + n.Name;
        bool open = IsOpen(key);
        rows.Add(new SchemaRow
        {
            Kind = SchemaRowKind.Database, Text = n.Name, Database = n.Name, Key = key, Level = level, IsExpanded = open,
            IsCurrent = n.Name == CurrentDatabase, Detail = n.Schema is { } s ? (s.Tables.Count + s.Routines.Count).ToString("N0") : null,
        });
        if (!open) return;
        if (n.Error is { } error) rows.Add(Message(error, level + 1));
        else if (n.Schema is null) rows.Add(Message(Loc.T("common.loading"), level + 1));
        else if (!AddObjects(rows, n.Schema, n.Name, level + 1)) rows.Add(Message(Loc.T("tree.empty"), level + 1));
    }

    /// <summary>
    /// A database's schemas (when it has several) and in each one its folders of tables, views, procedures and
    /// functions. False when it holds nothing.
    /// </summary>
    private bool AddObjects(List<SchemaRow> rows, SchemaSnapshot s, string? db, int level)
    {
        if (s.Tables.Count == 0 && s.Routines.Count == 0) return false;
        if (s.Schemas.Count <= 1)
        {
            AddFolders(rows, s.Tables, s.Routines, db + "/", level);
            return true;
        }
        foreach (var name in s.Schemas)
        {
            var key = $"s:{db}/{name}";
            var tables = s.Tables.Where(t => t.Schema == name).ToList();
            var routines = s.Routines.Where(r => r.Schema == name).ToList();
            bool open = IsOpen(key);
            rows.Add(new SchemaRow
            {
                Kind = SchemaRowKind.Schema, Text = name, Detail = (tables.Count + routines.Count).ToString("N0"),
                Schema = name, Database = db, Key = key, Level = level, IsExpanded = open,
            });
            if (!open) continue;
            if (tables.Count + routines.Count == 0) rows.Add(Message(Loc.T("tree.empty"), level + 1));
            else AddFolders(rows, tables, routines, $"{db}/{name}", level + 1);
        }
        return true;
    }

    /// <summary>Folders as in SQL Server's explorer; the tables one starts open, empty ones are left out.</summary>
    private void AddFolders(List<SchemaRow> rows, IReadOnlyList<DbTable> tables, IReadOnlyList<DbRoutine> routines, string path, int level)
    {
        Folder("T", "tree.tables", tables.Where(t => t.Kind == DbObjectKind.Table).ToList(), true, (t, l) => AddTable(rows, t, l, false));
        Folder("V", "tree.views", tables.Where(t => t.Kind == DbObjectKind.View).ToList(), false, (t, l) => AddTable(rows, t, l, false));
        Folder("P", "tree.procedures", routines.Where(r => r.Kind == DbRoutineKind.Procedure).ToList(), false, (r, l) => AddRoutine(rows, r, l, false));
        Folder("F", "tree.functions", routines.Where(r => r.Kind == DbRoutineKind.Function).ToList(), false, (r, l) => AddRoutine(rows, r, l, false));

        void Folder<T>(string id, string label, List<T> items, bool byDefault, Action<T, int> add)
        {
            if (items.Count == 0) return;
            var key = $"f:{path}/{id}";
            bool open = IsOpen(key, byDefault);
            rows.Add(new SchemaRow { Kind = SchemaRowKind.Folder, Text = Loc.T(label), Detail = items.Count.ToString("N0"), Key = key, Level = level, IsExpanded = open });
            if (open) foreach (var x in items) add(x, level + 1);
        }
    }

    private void AddTable(List<SchemaRow> rows, DbTable t, int level, bool qualified)
    {
        var key = "t:" + t.FullName;
        bool open = _expanded.Contains(key);
        rows.Add(new SchemaRow
        {
            Kind = t.Kind == DbObjectKind.View ? SchemaRowKind.View : SchemaRowKind.Table,
            Text = qualified ? t.FullName : t.Name,
            Detail = t.CanEdit || t.Kind == DbObjectKind.View ? null : Loc.T("tree.noKey"),
            Level = level, Table = t, Schema = t.Schema, Database = t.Database, Key = key, IsExpanded = open,
        });
        if (!open) return;
        foreach (var c in t.Columns)
            rows.Add(new SchemaRow { Kind = SchemaRowKind.Column, Text = c.Name, Detail = c.DataType, Level = level + 1, Table = t, Column = c, Database = t.Database });
    }

    private static void AddRoutine(List<SchemaRow> rows, DbRoutine r, int level, bool qualified)
    {
        // Overloads (PostgreSQL) share a name: the signature tells them apart.
        rows.Add(new SchemaRow
        {
            Kind = r.Kind == DbRoutineKind.Procedure ? SchemaRowKind.Procedure : SchemaRowKind.Function,
            Text = qualified ? r.FullName : r.Name,
            Detail = r.Parameters.Count == 0 ? "()" : r.Signature.Length <= 32 ? r.Signature : "(" + Loc.Plural("detail.params", r.Parameters.Count) + ")",
            Level = level, Routine = r, Schema = r.Schema, Database = r.Database,
        });
    }

    public void Toggle(SchemaRow row)
    {
        if (!row.CanExpand) return;
        if (row.Key is not { } key) return;
        if (!_expanded.Remove(key)) _expanded.Add(key);
        SelectedRow = row;
        // Opening a database reads it; one that failed is tried again.
        if (row.Kind == SchemaRowKind.Database && !row.IsExpanded
            && _databases.FirstOrDefault(n => n.Name == row.Database) is { Schema: null, Loading: null } node)
        {
            node.Error = null;
            _ = LoadDatabaseAsync(node);
        }
        RebuildTree();
    }

    /// <summary>Double click / Enter / F4 on a table: its data; on a column: the table's data.</summary>
    public void Activate(SchemaRow row)
    {
        if (row.Kind is SchemaRowKind.Database or SchemaRowKind.Folder or SchemaRowKind.Schema) Toggle(row);
        else if (row.Routine is { } r) _ = OpenRoutineSourceAsync(r);
        else if (row.Table is { } t) OpenTable(t);
    }

    [RelayCommand]
    private void OpenSelectedTable()
    {
        if (SelectedRow?.Table is { } t) OpenTable(t);
        else if (SelectedDoc is TableDataViewModel tv) _ = tv.LoadAsync();
    }

    public void OpenTable(DbTable t)
    {
        var existing = Docs.OfType<TableDataViewModel>().FirstOrDefault(x => x.Table.FullName == t.FullName);
        if (existing is not null)
        {
            SelectedDoc = existing;
            return;
        }
        var tab = new TableDataViewModel(this, t);
        tab.CloseCommand = new RelayCommand(() => CloseDoc(tab));
        Docs.Add(tab);
        SelectedDoc = tab;
        _ = tab.LoadAsync();
    }

    /// <summary>Ctrl+P inside a connection: jump to a table.</summary>
    public async Task QuickOpenTableAsync()
    {
        var t = await Dialogs.Pick(Loc.T("dlg.goToTable"), Loc.T("dlg.goToTable.watermark"),
            q => AllTables.Where(x => Dialogs.Fuzzy(x.Display, q))
                .OrderBy(x => !x.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)).ThenBy(x => x.Name.Length).Take(200).ToList(),
            x => x.Name, x => (x.Database is null || x.Database == x.Schema ? "" : x.Database + " · ") + (x.Schema is null ? "" : x.Schema + " · ") + Loc.Plural("common.columns", x.Columns.Count),
            x => x.Kind == DbObjectKind.View ? Palette.Teal : Palette.Teal);
        if (t is not null) OpenTable(t);
    }

    // ------------------------------------------------------------------ details

    private void ShowRoutineDetails(DbRoutine r)
    {
        _detailsLoad?.Cancel();
        DetailTable = null;
        DetailRoutine = r;
        DetailColumns.Clear();
        DetailExtras.Clear();
        foreach (var p in r.Parameters)
            DetailColumns.Add(new DetailLine(p.Name.Length > 0 ? p.Name : "—", (p.Direction == ParamDirection.In ? "" : p.Direction.ToString().ToUpperInvariant() + " ") + p.DataType,
                p.Direction == ParamDirection.In ? null : Palette.Amber));
        if (r.Parameters.Count == 0) DetailColumns.Add(new DetailLine(Loc.T("detail.noParams"), ""));
        if (r.ReturnType is { } ret) DetailExtras.Add(new DetailLine(Loc.T("detail.returns"), ret, Palette.Pink));
    }

    private void ShowDetails(DbTable? t)
    {
        DetailRoutine = null;
        DetailTable = t;
        DetailColumns.Clear();
        DetailExtras.Clear();
        if (t is null) return;
        foreach (var c in t.Columns)
            DetailColumns.Add(new DetailLine((c.IsPrimaryKey ? "⚿ " : "") + c.Name, c.DataType + (c.Nullable ? "" : " NOT NULL"), c.IsPrimaryKey ? Palette.Amber : null));
        _ = LoadExtrasAsync(t);
    }

    private async Task LoadExtrasAsync(DbTable t)
    {
        if (_session is not { } main) return;
        var s = SessionFor(main, t.Database);
        _detailsLoad?.Cancel();
        var cts = _detailsLoad = new CancellationTokenSource();
        try
        {
            var d = await Task.Run(() => s.LoadDetailsAsync(t, cts.Token));
            if (cts.IsCancellationRequested || DetailTable != t) return;
            foreach (var i in d.Indexes) DetailExtras.Add(new DetailLine((i.Unique ? Loc.T("detail.unique") : Loc.T("detail.index")) + " " + i.Name, i.Definition));
            foreach (var f in d.ForeignKeys) DetailExtras.Add(new DetailLine("FK " + f.Name, f.Definition, Palette.Teal));
        }
        catch { /* details are optional */ }
    }

    // ------------------------------------------------------------------ consoles

    private string QueriesFolder => _app.QueriesFolder(Entry);

    private void RestoreConsoles()
    {
        var session = _app.Store.SessionFor(Entry.Id);
        foreach (var name in session.Consoles.ToList())
        {
            var path = Path.Combine(QueriesFolder, name);
            if (File.Exists(path)) AddConsole(path, select: false, session.ConsoleDatabases.GetValueOrDefault(name));
        }
        if (Docs.Count == 0) AddConsole(NextConsolePath(), select: false);
        SelectedDoc = Docs.OfType<ConsoleViewModel>().FirstOrDefault(c => Path.GetFileName(c.FilePath) == session.Active) ?? Docs.FirstOrDefault();
    }

    /// <summary>A free file name in the queries folder: console.sql, console-2.sql… or the same from <paramref name="baseName"/>.</summary>
    private string NextConsolePath(string? baseName = null)
    {
        var stem = baseName is null ? "console" : string.Concat(baseName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        for (int i = 1; ; i++)
        {
            var name = i == 1 ? $"{stem}.sql" : $"{stem}-{i}.sql";
            var path = Path.Combine(QueriesFolder, name);
            if (!File.Exists(path) && Docs.OfType<ConsoleViewModel>().All(c => !string.Equals(c.FilePath, path, StringComparison.OrdinalIgnoreCase))) return path;
        }
    }

    private ConsoleViewModel AddConsole(string path, bool select, string? database = null)
    {
        var c = new ConsoleViewModel(this, path) { Database = HasDatabases ? database : null };
        c.CloseCommand = new RelayCommand(() => CloseDoc(c));
        Docs.Add(c);
        if (select) SelectedDoc = c;
        return c;
    }

    [RelayCommand]
    private void NewConsole() => NewConsoleOn(null);

    /// <summary>A new console that runs on <paramref name="database"/> (null: the connection's own).</summary>
    public void NewConsoleOn(string? database)
    {
        var c = AddConsole(NextConsolePath(), select: true, database);
        c.Save();
        SaveSession();
    }

    /// <summary>
    /// The console to write into for <paramref name="database"/>: the selected one if it runs there, else the first
    /// one that does, else a new one on it.
    /// </summary>
    private ConsoleViewModel ConsoleOn(string? database)
    {
        var want = EffectiveDatabase(database);
        bool On(ConsoleViewModel c) => EffectiveDatabase(c.Database) == want;
        var console = SelectedDoc is ConsoleViewModel selected && On(selected) ? selected
            : Docs.OfType<ConsoleViewModel>().FirstOrDefault(On) ?? AddConsole(NextConsolePath(), false, database);
        SelectedDoc = console;
        return console;
    }

    /// <summary>Opens a .sql file of the queries folder that is not open yet.</summary>
    [RelayCommand]
    private async Task OpenConsoleFile()
    {
        var files = Directory.Exists(QueriesFolder)
            ? Directory.GetFiles(QueriesFolder, "*.sql").Where(f => Docs.OfType<ConsoleViewModel>().All(c => !string.Equals(c.FilePath, f, StringComparison.OrdinalIgnoreCase))).OrderBy(f => f).ToList()
            : new List<string>();
        var picked = await Dialogs.Pick(Loc.T("dlg.openConsole"), Loc.T("dlg.openConsole.watermark"),
            q => files.Where(f => Dialogs.Fuzzy(Path.GetFileName(f), q)).ToList(), Path.GetFileName, _ => QueriesFolder, null);
        if (picked is not null) AddConsole(picked, select: true);
        SaveSession();
    }

    /// <summary>Writes <paramref name="sql"/> in a console that runs on <paramref name="database"/>.</summary>
    public void OpenInConsole(string sql, string? database = null) => ConsoleOn(database).InsertText(sql);

    public void CloseDoc(TabViewModel doc)
    {
        int i = Docs.IndexOf(doc);
        if (i < 0) return;
        if (doc is ConsoleViewModel c)
        {
            c.Save();
            // An empty console leaves no file behind.
            if (c.Document.TextLength == 0) { try { File.Delete(c.FilePath); } catch { } }
        }
        Docs.RemoveAt(i);
        if (SelectedDoc == doc || SelectedDoc is null) SelectedDoc = Docs.Count == 0 ? null : Docs[Math.Clamp(i - 1, 0, Docs.Count - 1)];
        SaveSession();
    }

    /// <summary>Ctrl+W: closes the current document; false when there is none (the tab closes then).</summary>
    public bool CloseCurrentDoc()
    {
        if (SelectedDoc is null) return false;
        CloseDoc(SelectedDoc);
        return true;
    }

    public void SaveSession()
    {
        var s = _app.Store.SessionFor(Entry.Id);
        s.Consoles = Docs.OfType<ConsoleViewModel>().Select(c => Path.GetFileName(c.FilePath)).ToList();
        s.Active = SelectedDoc is ConsoleViewModel c ? Path.GetFileName(c.FilePath) : s.Active;
        s.ConsoleDatabases = Docs.OfType<ConsoleViewModel>().Where(x => x.Database is not null)
            .ToDictionary(x => Path.GetFileName(x.FilePath), x => x.Database!);
        _app.Store.Save();
    }

    public async Task RenameConsoleAsync(ConsoleViewModel c)
    {
        var name = await Dialogs.Prompt(Loc.T("console.rename.title"), Loc.T("console.rename.label"), c.Title);
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!c.Rename(name.Trim())) Notice = Loc.F("console.rename.exists", name.Trim());
        SaveSession();
    }

    // ------------------------------------------------------------------ safety

    /// <summary>
    /// Read-only connections refuse statements that write; production ones ask first. True when it may run.
    /// </summary>
    public async Task<bool> ConfirmWritesAsync(IReadOnlyList<string> statements)
    {
        var writes = statements.Where(s => SqlStatements.IsWrite(s, Entry.Kind)).ToList();
        if (writes.Count == 0) return true;
        if (IsReadOnly)
        {
            Notice = Loc.T("db.readOnly");
            await Dialogs.Message(Loc.T("conn.readOnly.title"), Loc.T("conn.readOnly.text"));
            return false;
        }
        // Deletes get their own confirmation (ConfirmDeletesAsync), which also warns about production.
        writes.RemoveAll(w => DeletePlan.IsDelete(w, Entry.Kind));
        if (!IsProduction || writes.Count == 0) return true;
        var preview = string.Join("\n\n", writes.Take(3).Select(w => w.Length > 300 ? w[..300] + "…" : w));
        return await Dialogs.Confirm(Loc.T("conn.prodWrite.title"), Loc.F("conn.prodWrite.text", Entry.Name, preview), Loc.T("conn.prodWrite.ok"));
    }

    /// <summary>Most rows a console delete keeps in the history; past it the backup is partial and the user is told.</summary>
    public const int MaxBackupRows = 100_000;

    /// <summary>
    /// Every DELETE or TRUNCATE of a console asks first, on any connection: how many rows each one removes and
    /// whether they are kept in the history (and why not, when they cannot be). Returns the plans by statement, or
    /// null when the user cancels.
    /// </summary>
    public async Task<Dictionary<string, DeletePlan>?> ConfirmDeletesAsync(IReadOnlyList<string> statements, string? database = null)
    {
        var plans = new Dictionary<string, DeletePlan>();
        var schema = SchemaFor(database);
        foreach (var sql in statements)
            if (DeletePlan.For(sql, Entry.Kind, Dialect, schema) is { } plan) plans[sql] = plan;
        if (plans.Count == 0) return plans;
        if (await EnsureSessionAsync(database) is not { } session) return null;

        var lines = new List<string>();
        foreach (var plan in plans.Values)
        {
            long? count = null;
            if (plan.CountSql is { } c)
                try { count = await Task.Run(() => CountAsync(session, c)); } catch { /* the statement itself will say what is wrong */ }
            var rows = count is { } n ? Loc.Plural("common.rows", n) : Loc.T("history.confirm.unknownRows");
            var keep = !plan.CanBackup ? Loc.F("history.confirm.noBackup", plan.Unsupported)
                : count > MaxBackupRows ? Loc.F("history.confirm.partial", MaxBackupRows)
                : HistoryDays > 0 ? Loc.Plural("history.confirm.kept", HistoryDays) : Loc.T("history.confirm.keptForever");
            lines.Add($"• {plan.TableDisplay}: {rows} — {keep}");
        }
        var text = Loc.F("history.confirm.text", string.Join("\n", lines));
        if (IsProduction) text = Loc.F("history.confirm.production", Entry.Name) + "\n\n" + text;
        return await Dialogs.Confirm(Loc.T("history.confirm.title"), text, Loc.T("history.confirm.ok")) ? plans : null;
    }

    private async Task<long> CountAsync(DbSession session, string sql)
    {
        var sink = new FirstValueSink();
        await session.RunAsync(sql, sink, 1, CancellationToken.None);
        return Convert.ToInt64(sink.Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class FirstValueSink : IResultSink
    {
        public object? Value;
        public void OnResult(IReadOnlyList<ResultColumn> columns) { }
        public void OnRows(IReadOnlyList<object?[]> rows) { if (Value is null && rows.Count > 0 && rows[0].Length > 0) Value = rows[0][0]; }
        public void OnEnd(long recordsAffected, bool truncated) { }
    }

    /// <summary>Saves a delete that just ran in the history.</summary>
    public void Remember(DeleteRecord r)
    {
        try
        {
            _app.History.Add(r);
            RefreshHistoryCount();
        }
        catch (Exception ex) { Notice = Loc.F("history.saveFailed", ex.Message); }
    }

    // ------------------------------------------------------------------ delete history

    [RelayCommand]
    private Task ShowHistory() => DeleteHistoryDialog.Show(this);

    public void SetHistoryDays(int days)
    {
        _app.Store.State.DeleteHistoryDays = days;
        _app.Store.Save();
        try { _app.History.Purge(days, DateTimeOffset.Now); } catch { /* next start */ }
        RefreshHistoryCount();
    }

    /// <summary>
    /// Puts a delete's rows back, after showing what will run and asking (always; production says so too). All the
    /// INSERTs run in one transaction: if one row cannot go back (its key is taken again…) none does.
    /// </summary>
    public async Task<bool> RestoreAsync(DeleteRecord r)
    {
        if (!r.HasBackup || r.Restored is not null) return false;
        if (IsReadOnly)
        {
            await Dialogs.Message(Loc.T("conn.readOnly.title"), Loc.T("conn.readOnly.text"));
            return false;
        }
        if (await EnsureSessionAsync(r.Database) is not { } session) return false;
        var preview = string.Join("\n", DeleteInverse.Statements(r, Dialect).Take(3).Select(s => s.Preview.Length > 200 ? s.Preview[..200] + "…" : s.Preview));
        var text = Loc.F("history.restore.text", Loc.Plural("common.rows", r.Rows.Count), r.TableDisplay,
            r.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), preview + (r.Rows.Count > 3 ? "\n…" : ""));
        if (r.Partial) text += "\n\n" + Loc.T("history.script.partial");
        if (IsProduction) text = Loc.F("history.confirm.production", Entry.Name) + "\n\n" + text;
        if (!await Dialogs.Confirm(Loc.T("history.restore.title"), text, Loc.T("history.restore.ok"))) return false;
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var n = await Task.Run(() => session.ApplyAsync(DeleteInverse.Commands(r, Dialect), CancellationToken.None));
            r.Restored = DateTimeOffset.Now;
            _app.History.Save(r);
            Notice = Loc.F("history.restored", Loc.Plural("common.rows", n), r.TableDisplay, ResultViewModel.FormatMs(clock.ElapsedMilliseconds));
            foreach (var t in Docs.OfType<TableDataViewModel>()) _ = t.LoadAsync();
            return true;
        }
        catch (Exception ex)
        {
            await Dialogs.Message(Loc.T("history.restore.failed"), Loc.F("history.restore.failed.text", ex.Message));
            return false;
        }
    }

    // ------------------------------------------------------------------ routines

    /// <summary>The routine's code in a console of its own, as a script that recreates it: edit and run.</summary>
    public async Task OpenRoutineSourceAsync(DbRoutine r)
    {
        if (await EnsureSessionAsync(r.Database) is not { } session) return;
        try
        {
            var text = await Task.Run(() => session.LoadRoutineSourceAsync(r, CancellationToken.None));
            var c = AddConsole(NextConsolePath(r.Name), select: true, r.Database);
            c.InsertText(text);
            c.Save();
            SaveSession();
        }
        catch (Exception ex) { Notice = ex.Message; }
    }

    /// <summary>
    /// Asks for the input parameters, writes the call in the current console and runs it there, so the usual
    /// safety (production, read-only) applies and the results show up as any query's.
    /// </summary>
    public async Task ExecuteRoutineAsync(DbRoutine r)
    {
        var inputs = Dialect.InputsOf(r);
        var values = new List<string>();
        if (inputs.Count > 0)
        {
            var fields = inputs.Select(p => ($"{(p.Name.Length > 0 ? p.Name : "?")}  {p.DataType}{(p.Direction == ParamDirection.InOut ? "  (IN/OUT)" : "")}", "")).ToArray();
            var typed = await Dialogs.Form(Loc.F("routine.run.title", r.Display), fields, Loc.T("routine.run.ok"), Loc.T("routine.run.hint"));
            if (typed is null) return;
            values = typed.Select((v, i) => Dialect.ArgLiteral(v, inputs[i])).ToList();
        }
        var sql = Dialect.CallSql(r, values);
        var console = ConsoleOn(r.Database);
        var script = sql.TrimEnd().EndsWith('/') || sql.TrimEnd().EndsWith(';') ? sql : sql + ";";
        console.InsertText(script);
        await console.RunScriptAsync(script);
    }

    public void ScriptDropRoutine(DbRoutine r) => OpenInConsole(Dialect.DropSql(r) + ";", r.Database);

    // ------------------------------------------------------------------ staging

    private void OnStagingChanged()
    {
        StagedItems.Clear();
        foreach (var st in Staging.Build(Dialect))
        {
            var change = st.Change;
            StagedItems.Add(new StagedItem { Statement = st, RemoveCommand = new RelayCommand(() => Staging.Remove(change)) });
        }
        OnPropertyChanged(nameof(HasStaging));
        OnPropertyChanged(nameof(StagingTitle));
        ApplyStagingCommand.NotifyCanExecuteChanged();
        DiscardStagingCommand.NotifyCanExecuteChanged();
    }

    private bool CanApply => Staging.Count > 0 && !IsApplying;

    /// <summary>Ctrl+Shift+K: runs every pending change in one transaction.</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyStaging()
    {
        if (await EnsureSessionAsync() is not { } session) return;
        var statements = Staging.Build(Dialect);
        if (statements.Count == 0) return;
        var deletes = statements.Where(s => s.Change.Kind == ChangeKind.Delete).ToList();
        if (deletes.Count > 0)
        {
            // Deleting always asks, on any connection; the rows go to the history.
            var byTable = string.Join("\n", deletes.GroupBy(s => s.Change.Table.FullName).Select(g => $"• {g.Key}: {Loc.Plural("common.rows", g.Count())}"));
            var keep = HistoryDays > 0 ? Loc.Plural("history.confirm.kept", HistoryDays) : Loc.T("history.confirm.keptForever");
            var text = Loc.F("staging.apply.deletes", Loc.Plural("staging.count", statements.Count), byTable, keep);
            if (IsProduction) text = Loc.F("history.confirm.production", Entry.Name) + "\n\n" + text;
            if (!await Dialogs.Confirm(Loc.T("history.confirm.title"), text, Loc.T("staging.apply"))) return;
        }
        else if (IsProduction && !await Dialogs.Confirm(Loc.T("staging.apply.prodTitle"),
                Loc.F("staging.apply.prodText", Entry.Name, Loc.Plural("staging.count", statements.Count)), Loc.T("staging.apply")))
            return;
        IsApplying = true;
        ApplyStagingCommand.NotifyCanExecuteChanged();
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long rows = 0;
            // One transaction per database (each has its own connection), in the order the changes were made; a
            // database that fails keeps its changes, the ones already applied leave the panel.
            foreach (var group in statements.GroupBy(s => s.Change.Table.Database).ToList())
            {
                var list = group.ToList();
                var target = SessionFor(session, group.Key);
                var captured = new Dictionary<int, CapturedRows>();
                rows += await Task.Run(() => target.ApplyAsync(list.Select(s => s.Command).ToList(), CancellationToken.None,
                    list.Select(s => s.Capture).ToList(), captured));
                // One history entry per table, with every row its deletes removed.
                foreach (var g in captured.GroupBy(kv => list[kv.Key].Change.Table.FullName))
                {
                    var first = list[g.First().Key];
                    var all = new CapturedRows(g.First().Value.Columns, g.SelectMany(kv => kv.Value.Rows).ToList(), false);
                    var sql = string.Join("\n", g.Select(kv => list[kv.Key].Preview));
                    Remember(DeleteRecord.Create(Entry, "staging", first.Change.Table.Schema, first.Change.Table.Name, sql, g.Count(), all, first.Change.Table, null, DateTimeOffset.Now));
                }
                Staging.RemoveAll(list.Select(s => s.Change));
            }
            Notice = Loc.F("staging.applied", Loc.Plural("common.rows", rows), ResultViewModel.FormatMs(clock.ElapsedMilliseconds));
            foreach (var t in Docs.OfType<TableDataViewModel>()) _ = t.LoadAsync();
        }
        catch (Exception ex)
        {
            Notice = Loc.T("staging.failed");
            await Dialogs.Message(Loc.T("staging.failed"), Loc.F("staging.failed.text", ex.Message));
        }
        finally
        {
            IsApplying = false;
            ApplyStagingCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task DiscardStaging()
    {
        if (Staging.Count == 0) return;
        if (!await Dialogs.Confirm(Loc.T("staging.discard.title"), Loc.F("staging.discard.text", Loc.Plural("staging.count", Staging.Count)), Loc.T("common.discard")))
            return;
        Staging.Clear();
    }

    [RelayCommand]
    private async Task CopyStaging()
    {
        var sql = string.Join("\n", Staging.Build(Dialect).Select(s => s.Preview));
        await Dialogs.CopyAsync(sql);
        Notice = Loc.T("staging.copied");
    }

    // ------------------------------------------------------------------ export

    public async Task ExportAsync(ResultViewModel r, ExportFormat f, IEnumerable<int>? rows, IReadOnlyList<int>? cols, string name, bool toFile = false)
    {
        var columns = cols is null ? r.Columns : cols.Select(i => r.Columns[i]).ToList();
        var tableName = DetailTable is { } t && SelectedDoc is TableDataViewModel ? Dialect.NameOf(t) : name;
        var text = Exporters.Export(f, columns, r.RowsFor(rows, cols), Dialect, tableName);
        if (toFile)
        {
            var path = await Dialogs.SaveFile(Loc.T("export.save"), (tableName.Split('.').Last().Trim('"', '`')) + "." + Exporters.FileExtension(f));
            if (path is null) return;
            await File.WriteAllTextAsync(path, text);
            Notice = Loc.F("export.saved", path);
        }
        else
        {
            await Dialogs.CopyAsync(text);
            Notice = Loc.F("export.copied", Loc.T("export." + f.ToString().ToLowerInvariant()));
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in Docs.OfType<ConsoleViewModel>()) c.Save();
        foreach (var s in _dbSessions.Values) await s.DisposeAsync();
        _dbSessions.Clear();
        if (_session is not null) await _session.DisposeAsync();
        _session = null;
    }
}

/// <summary>A database of the server in the tree: its structure is read the first time it is opened.</summary>
public sealed class DatabaseNode
{
    public required string Name { get; init; }
    public bool IsSystem { get; set; }
    public SchemaSnapshot? Schema { get; set; }
    public string? Error { get; set; }
    public Task? Loading { get; set; }
}
