using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataPeach.Core;
using DataPeach.Core.Db;

namespace DataPeach.ViewModels;

/// <summary>
/// The rows of one table (F4 or double click in the tree), with WHERE and ORDER BY boxes. Tables with a primary
/// key are editable: edits, new rows and deletions wait in the connection's staging area.
/// </summary>
public sealed partial class TableDataViewModel : TabViewModel
{
    public const int PageSize = 1000;
    private readonly ConnectionViewModel _owner;
    private CancellationTokenSource? _load;

    public DbTable Table { get; private set; }
    [ObservableProperty] private ResultViewModel? _result;
    [ObservableProperty] private string _where = "";
    [ObservableProperty] private string _orderBy = "";
    [ObservableProperty] private int _limit = PageSize;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _hasMore;

    public bool CanEdit => Table.CanEdit && !_owner.IsReadOnly;
    /// <summary>Why the grid is read-only, shown above it; empty when it can be edited.</summary>
    public string ReadOnlyReason =>
        _owner.IsReadOnly ? Loc.T("table.readOnly.connection")
        : Table.Kind == DbObjectKind.View ? Loc.T("table.readOnly.view")
        : !Table.CanEdit ? Loc.T("table.readOnly.noKey") : "";
    public bool ShowReadOnlyReason => ReadOnlyReason.Length > 0;

    public TableDataViewModel(ConnectionViewModel owner, DbTable table)
    {
        _owner = owner;
        Table = table;
        Title = table.Name;
        ToolTip = table.FullName;
    }

    /// <summary>After the schema reloads the table object is new; keep the tab pointing at the current one.</summary>
    public void Rebind(DbTable table)
    {
        Table = table;
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(ReadOnlyReason));
        OnPropertyChanged(nameof(ShowReadOnlyReason));
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (await _owner.EnsureSessionAsync(Table.Database) is not { } session) return;
        _load?.Cancel();
        var cts = _load = new CancellationTokenSource();
        // One row more than the page tells whether there is more, without counting the whole table.
        var sql = session.Dialect.SelectTable(Table, Where, OrderBy, Limit + 1);
        var result = new ResultViewModel(sql, 1);
        IsLoading = true;
        Status = Loc.T("result.running");
        var clock = Stopwatch.StartNew();
        try
        {
            var sink = new DirectSink(result);
            await Task.Run(() => session.RunAsync(sql, sink, Limit + 1, cts.Token));
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
            if (cts.IsCancellationRequested) return;
            HasMore = result.Rows.Count > Limit;
            if (HasMore) result.TrimTo(Limit);
            result.Finish(-1, false, clock.ElapsedMilliseconds);
            result.Edit = CanEdit ? EditContext(result) : null;
            Result = result;
            Status = result.Summary + (HasMore ? " " + Loc.T("table.more") : "");
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            Status = ex.Message;
            result.Fail(ex.Message, clock.ElapsedMilliseconds);
            Result = result;
        }
        finally
        {
            if (_load == cts) IsLoading = false;
        }
    }

    private TableEditContext? EditContext(ResultViewModel r)
    {
        var keys = Table.PrimaryKey.Select(k => r.Columns.ToList().FindIndex(c => c.Name == k.Name)).ToArray();
        if (keys.Length == 0 || keys.Any(i => i < 0)) return null;
        return new TableEditContext { Table = Table, Staging = _owner.Staging, KeyColumns = keys };
    }

    [RelayCommand]
    private Task LoadMore()
    {
        Limit += PageSize;
        return LoadAsync();
    }

    [RelayCommand]
    private void AddRow()
    {
        if (Result?.CanEdit == true) Result.AddRow();
    }

    public void DeleteRows(IEnumerable<int> rows) => Result?.DeleteRows(rows);

    /// <summary>"SELECT * FROM t WHERE …" in a console, to keep working on it by hand.</summary>
    [RelayCommand]
    private void OpenInConsole() =>
        _owner.OpenInConsole(_owner.Dialect.SelectTable(Table, Where, OrderBy, Limit) + ";", Table.Database);

    /// <summary>Writes rows straight into one result (a table tab has exactly one).</summary>
    private sealed class DirectSink(ResultViewModel r) : IResultSink
    {
        public void OnResult(IReadOnlyList<ResultColumn> columns) => Avalonia.Threading.Dispatcher.UIThread.Post(() => r.SetColumns(columns));
        public void OnRows(IReadOnlyList<object?[]> rows) => Avalonia.Threading.Dispatcher.UIThread.Post(() => r.Append(rows));
        public void OnEnd(long recordsAffected, bool truncated) { }
    }
}
