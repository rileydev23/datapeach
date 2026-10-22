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
    public string DetailTitle => DetailRoutine?.Display ?? DetailTable?.Display ?? "";
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
            foreach (var r in Schema.Routines.Where(r => Dialogs.Fuzzy(r.Name, q)).OrderBy(r => !r.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)).ThenBy(r => r.Name.Length).Take(200))
                AddRoutine(rows, r, 0, qualified: UseSchemaLevel);
        }
        else if (UseSchemaLevel)
        {
            foreach (var s in Schema.Schemas)
            {
                var key = "s:" + s;
                var tables = Schema.Tables.Where(t => t.Schema == s).ToList();
                var routines = Schema.Routines.Where(r => r.Schema == s).ToList();
                rows.Add(new SchemaRow { Kind = SchemaRowKind.Schema, Text = s, Detail = (tables.Count + routines.Count).ToString("N0"), Schema = s, IsExpanded = _expanded.Contains(key) });
                if (!_expanded.Contains(key)) continue;
                foreach (var t in tables) AddTable(rows, t, 1, false);
                foreach (var r in routines) AddRoutine(rows, r, 1, false);
            }
        }
        else
        {
            foreach (var t in Schema.Tables) AddTable(rows, t, 0, false);
            foreach (var r in Schema.Routines) AddRoutine(rows, r, 0, false);
        }

        if (rows.Count == 0 && _session is not null)
            rows.Add(new SchemaRow { Kind = SchemaRowKind.Message, Text = q.Length > 0 ? Loc.T("tree.noMatch") : Loc.T("tree.empty") });

        SchemaRows.Clear();
        foreach (var r in rows) SchemaRows.Add(r);
        if (keep is not null)
            SelectedRow = SchemaRows.FirstOrDefault(r => r.Kind == keep.Kind && r.Table?.Display == keep.Table?.Display && r.Column?.Name == keep.Column?.Name
                                                         && r.Schema == keep.Schema && r.Routine?.Display == keep.Routine?.Display && r.Routine?.Id == keep.Routine?.Id);
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

    private static void AddRoutine(List<SchemaRow> rows, DbRoutine r, int level, bool qualified)
    {
        // Overloads (PostgreSQL) share a name: the signature tells them apart.
        rows.Add(new SchemaRow
        {
            Kind = r.Kind == DbRoutineKind.Procedure ? SchemaRowKind.Procedure : SchemaRowKind.Function,
            Text = qualified ? r.Display : r.Name,
            Detail = r.Parameters.Count == 0 ? "()" : r.Signature.Length <= 32 ? r.Signature : "(" + Loc.Plural("detail.params", r.Parameters.Count) + ")",
            Level = level, Routine = r, Schema = r.Schema,
        });
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
    public async Task<Dictionary<string, DeletePlan>?> ConfirmDeletesAsync(IReadOnlyList<string> statements)
    {
        var plans = new Dictionary<string, DeletePlan>();
        foreach (var sql in statements)
            if (DeletePlan.For(sql, Entry.Kind, Dialect, Schema) is { } plan) plans[sql] = plan;
        if (plans.Count == 0) return plans;
        if (await EnsureSessionAsync() is not { } session) return null;

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
        if (await EnsureSessionAsync() is not { } session) return false;
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
        if (await EnsureSessionAsync() is not { } session) return;
        try
        {
            var text = await Task.Run(() => session.LoadRoutineSourceAsync(r, CancellationToken.None));
            var c = AddConsole(NextConsolePath(r.Name), select: true);
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
        var console = SelectedDoc as ConsoleViewModel ?? Docs.OfType<ConsoleViewModel>().FirstOrDefault() ?? AddConsole(NextConsolePath(), true);
        SelectedDoc = console;
        var script = sql.TrimEnd().EndsWith('/') || sql.TrimEnd().EndsWith(';') ? sql : sql + ";";
        console.InsertText(script);
        await console.RunScriptAsync(script);
    }

    public void ScriptDropRoutine(DbRoutine r) => OpenInConsole(Dialect.DropSql(r) + ";");

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
            var byTable = string.Join("\n", deletes.GroupBy(s => s.Change.Table.Display).Select(g => $"• {g.Key}: {Loc.Plural("common.rows", g.Count())}"));
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
            var captured = new Dictionary<int, CapturedRows>();
            var rows = await Task.Run(() => session.ApplyAsync(statements.Select(s => s.Command).ToList(), CancellationToken.None,
                statements.Select(s => s.Capture).ToList(), captured));
            // One history entry per table, with every row its deletes removed.
            foreach (var g in captured.GroupBy(kv => statements[kv.Key].Change.Table.Display))
            {
                var first = statements[g.First().Key];
                var all = new CapturedRows(g.First().Value.Columns, g.SelectMany(kv => kv.Value.Rows).ToList(), false);
                var sql = string.Join("\n", g.Select(kv => statements[kv.Key].Preview));
                Remember(DeleteRecord.Create(Entry, "staging", first.Change.Table.Schema, first.Change.Table.Name, sql, g.Count(), all, first.Change.Table, null, DateTimeOffset.Now));
            }
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
