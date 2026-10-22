using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataPeach.Controls;
using DataPeach.Core;
using DataPeach.Core.Data;
using DataPeach.Core.Db;
using DataPeach.Core.Drivers;
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
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

    public ConnectionEntry Entry { get; }
    public Dialect Dialect { get; }
    public StagingArea Staging { get; } = new();
    public SchemaSnapshot Schema { get; private set; } = SchemaSnapshot.Empty;
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
    [ObservableProperty] private bool _isApplying;

    public int MaxRows => _app.Store.State.MaxRows;
    public bool IsReadOnly => Entry.Mode == ConnectionMode.ReadOnly;
    public bool IsProduction => Entry.Mode == ConnectionMode.Production;
    public string EngineName => DbKinds.Name(Entry.Kind);
    public Avalonia.Media.IBrush EngineBrush => Palette.FromHex(DbKinds.Color(Entry.Kind));
    public bool HasConnectError => ConnectError is not null;
    public bool HasStaging => Staging.Count > 0;
    public string StagingTitle => Loc.Plural("staging.count", Staging.Count);
    public bool HasDetail => DetailTable is not null;
    public string DetailTitle => DetailTable?.Display ?? "";
    public string DetailKind => DetailTable is null ? "" : DetailTable.Kind == DbObjectKind.View ? Loc.T("detail.view") : Loc.T("detail.table");
    public string DriverText => Loc.F("conn.needsDriver.text", EngineName, DriverCatalog.For(Entry.Kind).Package, DriverCatalog.For(Entry.Kind).Version);
    public bool HasUnsavedChanges => Staging.Count > 0;

    public ConnectionViewModel(ConnectionEntry entry, AppServices app)
    {
        _app = app;
        Entry = entry;
        Dialect = Dialect.For(entry.Kind);
        Title = entry.Name;
        ToolTip = $"{DbKinds.Name(entry.Kind)} · {entry.Target}";
        Staging.Changed += OnStagingChanged;
        RestoreConsoles();
    }

    partial void OnConnectErrorChanged(string? value) => OnPropertyChanged(nameof(HasConnectError));
    partial void OnTreeFilterChanged(string value) => RebuildTree();
    partial void OnDetailTableChanged(DbTable? value)
    {
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(DetailTitle));
        OnPropertyChanged(nameof(DetailKind));
    }

    partial void OnSelectedRowChanged(SchemaRow? value)
    {
        if (value?.Table is { } t) ShowDetails(t);
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

    [RelayCommand]
    public async Task RefreshSchemaAsync()
    {
        if (await EnsureSessionAsync() is not { } session) return;
        IsBusy = true;
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Schema = await Task.Run(() => session.LoadSchemaAsync(CancellationToken.None));
            if (_expanded.Count == 0 && Schema.DefaultSchema is { } d) _expanded.Add("s:" + d);
            RebuildTree();
            foreach (var t in Docs.OfType<TableDataViewModel>())
                if (Schema.Find(t.Table.Schema, t.Table.Name) is { } fresh) t.Rebind(fresh);
            if (DetailTable is { } dt) ShowDetails(Schema.Find(dt.Schema, dt.Name));
            Notice = Loc.F("conn.schemaLoaded", Loc.Plural("common.tables", Schema.Tables.Count), ResultViewModel.FormatMs(clock.ElapsedMilliseconds));
        }
        catch (Exception ex) { Notice = ex.Message; }
        finally { IsBusy = false; }
    }

    // ------------------------------------------------------------------ schema tree

    private bool UseSchemaLevel => Schema.Schemas.Count > 1;

    public void RebuildTree()
    {
        var keep = SelectedRow;
        var rows = new List<SchemaRow>();
        var q = TreeFilter.Trim();
        if (q.Length > 0)
        {
            // Filtering: a flat list of matching tables, qualified when there are several schemas.
            foreach (var t in Schema.Tables.Where(t => Dialogs.Fuzzy(t.Name, q)).OrderBy(t => !t.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)).ThenBy(t => t.Name.Length).Take(500))
                AddTable(rows, t, 0, qualified: UseSchemaLevel);
        }
        else if (UseSchemaLevel)
        {
            foreach (var s in Schema.Schemas)
            {
                var key = "s:" + s;
                var tables = Schema.Tables.Where(t => t.Schema == s).ToList();
                rows.Add(new SchemaRow { Kind = SchemaRowKind.Schema, Text = s, Detail = tables.Count.ToString("N0"), Schema = s, IsExpanded = _expanded.Contains(key) });
                if (_expanded.Contains(key)) foreach (var t in tables) AddTable(rows, t, 1, false);
            }
        }
        else foreach (var t in Schema.Tables) AddTable(rows, t, 0, false);

        if (rows.Count == 0 && _session is not null)
            rows.Add(new SchemaRow { Kind = SchemaRowKind.Message, Text = q.Length > 0 ? Loc.T("tree.noMatch") : Loc.T("tree.empty") });

        SchemaRows.Clear();
        foreach (var r in rows) SchemaRows.Add(r);
        if (keep is not null)
            SelectedRow = SchemaRows.FirstOrDefault(r => r.Kind == keep.Kind && r.Table?.Display == keep.Table?.Display && r.Column?.Name == keep.Column?.Name && r.Schema == keep.Schema);
    }

    private void AddTable(List<SchemaRow> rows, DbTable t, int level, bool qualified)
    {
        var key = "t:" + t.Display;
        bool open = _expanded.Contains(key);
        rows.Add(new SchemaRow
        {
            Kind = t.Kind == DbObjectKind.View ? SchemaRowKind.View : SchemaRowKind.Table,
            Text = qualified ? t.Display : t.Name,
            Detail = t.CanEdit || t.Kind == DbObjectKind.View ? null : Loc.T("tree.noKey"),
            Level = level, Table = t, Schema = t.Schema, IsExpanded = open,
        });
        if (!open) return;
        foreach (var c in t.Columns)
            rows.Add(new SchemaRow { Kind = SchemaRowKind.Column, Text = c.Name, Detail = c.DataType, Level = level + 1, Table = t, Column = c });
    }

    public void Toggle(SchemaRow row)
    {
        if (!row.CanExpand) return;
        var key = row.Kind == SchemaRowKind.Schema ? "s:" + row.Schema : "t:" + row.Table!.Display;
        if (!_expanded.Remove(key)) _expanded.Add(key);
        SelectedRow = row;
        RebuildTree();
    }

    /// <summary>Double click / Enter / F4 on a table: its data; on a column: the table's data.</summary>
    public void Activate(SchemaRow row)
    {
        if (row.Kind == SchemaRowKind.Schema) Toggle(row);
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
        var existing = Docs.OfType<TableDataViewModel>().FirstOrDefault(x => x.Table.Display == t.Display);
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
            q => Schema.Tables.Where(x => Dialogs.Fuzzy(x.Display, q))
                .OrderBy(x => !x.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)).ThenBy(x => x.Name.Length).Take(200).ToList(),
            x => x.Name, x => (x.Schema is null ? "" : x.Schema + " · ") + Loc.Plural("common.columns", x.Columns.Count),
            x => x.Kind == DbObjectKind.View ? Palette.Teal : Palette.Teal);
        if (t is not null) OpenTable(t);
    }

    // ------------------------------------------------------------------ details

    private void ShowDetails(DbTable? t)
    {
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
        if (_session is not { } s) return;
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
            if (File.Exists(path)) AddConsole(path, select: false);
        }
        if (Docs.Count == 0) AddConsole(NextConsolePath(), select: false);
        SelectedDoc = Docs.OfType<ConsoleViewModel>().FirstOrDefault(c => Path.GetFileName(c.FilePath) == session.Active) ?? Docs.FirstOrDefault();
    }

    private string NextConsolePath()
    {
        for (int i = 1; ; i++)
        {
            var name = i == 1 ? "console.sql" : $"console-{i}.sql";
            var path = Path.Combine(QueriesFolder, name);
            if (!File.Exists(path) && Docs.OfType<ConsoleViewModel>().All(c => !string.Equals(c.FilePath, path, StringComparison.OrdinalIgnoreCase))) return path;
        }
    }

    private ConsoleViewModel AddConsole(string path, bool select)
    {
        var c = new ConsoleViewModel(this, path);
        c.CloseCommand = new RelayCommand(() => CloseDoc(c));
        Docs.Add(c);
        if (select) SelectedDoc = c;
        return c;
    }

    [RelayCommand]
    private void NewConsole()
    {
        var c = AddConsole(NextConsolePath(), select: true);
        c.Save();
        SaveSession();
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

    public void OpenInConsole(string sql)
    {
        var c = SelectedDoc as ConsoleViewModel ?? Docs.OfType<ConsoleViewModel>().FirstOrDefault() ?? AddConsole(NextConsolePath(), true);
        c.InsertText(sql);
        SelectedDoc = c;
    }

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
        if (!IsProduction) return true;
        var preview = string.Join("\n\n", writes.Take(3).Select(w => w.Length > 300 ? w[..300] + "…" : w));
        return await Dialogs.Confirm(Loc.T("conn.prodWrite.title"), Loc.F("conn.prodWrite.text", Entry.Name, preview), Loc.T("conn.prodWrite.ok"));
    }

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
        if (IsProduction && !await Dialogs.Confirm(Loc.T("staging.apply.prodTitle"),
                Loc.F("staging.apply.prodText", Entry.Name, Loc.Plural("staging.count", statements.Count)), Loc.T("staging.apply")))
            return;
        IsApplying = true;
        ApplyStagingCommand.NotifyCanExecuteChanged();
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var rows = await Task.Run(() => session.ApplyAsync(statements.Select(s => s.Command).ToList(), CancellationToken.None));
            Staging.Clear();
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
        if (_session is not null) await _session.DisposeAsync();
        _session = null;
    }
}
