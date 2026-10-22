using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DataPeach.Controls;
using DataPeach.Core;
using DataPeach.Core.Data;
using DataPeach.Core.Db;
using DataPeach.Core.Staging;

namespace DataPeach.ViewModels;

/// <summary>
/// Editing of a table's rows: which result columns are the primary key, and the staging area the edits go to.
/// </summary>
public sealed class TableEditContext
{
    public required DbTable Table { get; init; }
    public required StagingArea Staging { get; init; }
    /// <summary>Index in the result of each primary key column, in key order.</summary>
    public required int[] KeyColumns { get; init; }
}

/// <summary>
/// One result set (or the outcome of a statement without rows) and the grid source for it. Rows arrive in batches
/// from <see cref="ResultCollector"/> on the UI thread; with a <see cref="TableEditContext"/> the grid shows and
/// takes pending edits.
/// </summary>
public sealed partial class ResultViewModel : ObservableObject, IGridSource
{
    private IReadOnlyList<ResultColumn> _columns = Array.Empty<ResultColumn>();
    private readonly List<object?[]> _rows = new();
    private List<StagedChange> _inserts = new();
    private TableEditContext? _edit;

    public ResultViewModel(string statement, int index)
    {
        Statement = statement;
        Index = index;
    }

    public string Statement { get; }
    public int Index { get; }
    public string Label => Loc.F("result.n", Index);
    public IReadOnlyList<ResultColumn> Columns => _columns;
    public IReadOnlyList<object?[]> Rows => _rows;
    public bool HasGrid => _columns.Count > 0;

    [ObservableProperty] private bool _running = true;
    [ObservableProperty] private bool _truncated;
    [ObservableProperty] private long _elapsedMs;
    [ObservableProperty] private long _affected = -1;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _summary = "";
    /// <summary>Shown in the result switch of a console.</summary>
    [ObservableProperty] private bool _isSelected;
    public bool HasError => Error is not null;
    /// <summary>A finished statement without rows: only its summary is shown.</summary>
    public bool IsMessage => !HasGrid && Error is null && !Running;
    partial void OnErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsMessage));
    }
    partial void OnRunningChanged(bool value) => OnPropertyChanged(nameof(IsMessage));

    public TableEditContext? Edit
    {
        get => _edit;
        set
        {
            if (_edit is not null) _edit.Staging.Changed -= OnStagingChanged;
            _edit = value;
            if (_edit is not null) _edit.Staging.Changed += OnStagingChanged;
            RefreshInserts();
            Changed?.Invoke();
        }
    }

    private void OnStagingChanged()
    {
        RefreshInserts();
        Changed?.Invoke();
    }

    private void RefreshInserts() =>
        _inserts = _edit is null ? new() : _edit.Staging.Inserts(_edit.Table).ToList();

    // ------------------------------------------------------------------ streaming (UI thread)

    public void SetColumns(IReadOnlyList<ResultColumn> columns)
    {
        _columns = columns;
        _rows.Clear();
        OnPropertyChanged(nameof(HasGrid));
        OnPropertyChanged(nameof(IsMessage));
        Reset?.Invoke();
    }

    public void Append(IReadOnlyList<object?[]> rows)
    {
        _rows.AddRange(rows);
        UpdateSummary();
        Changed?.Invoke();
    }

    /// <summary>Drops the rows past <paramref name="count"/> (the probe row of a table page).</summary>
    public void TrimTo(int count)
    {
        if (_rows.Count > count) _rows.RemoveRange(count, _rows.Count - count);
        Changed?.Invoke();
    }

    public void Finish(long affected, bool truncated, long elapsedMs)
    {
        Affected = affected;
        Truncated |= truncated;
        ElapsedMs = elapsedMs;
        Running = false;
        UpdateSummary();
    }

    public void Fail(string error, long elapsedMs)
    {
        Error = error;
        ElapsedMs = elapsedMs;
        Running = false;
        UpdateSummary();
    }

    public void UpdateSummary()
    {
        var time = ElapsedMs > 0 || !Running ? " · " + FormatMs(ElapsedMs) : "";
        if (Error is not null) Summary = Loc.T("result.error") + time;
        else if (HasGrid)
            Summary = Loc.Plural("common.rows", _rows.Count) + (Truncated ? " " + Loc.T("result.limited") : "") + (Running ? " …" : time);
        else if (Affected >= 0) Summary = Loc.Plural("result.affected", Affected) + time;
        else Summary = Running ? Loc.T("result.running") : Loc.T("result.done") + time;
    }

    public static string FormatMs(long ms) => ms < 1000 ? $"{ms} ms" : $"{ms / 1000.0:0.00} s";

    // ------------------------------------------------------------------ IGridSource

    public event Action? Changed;
    public event Action? Reset;

    public int ColumnCount => _columns.Count;
    public string ColumnName(int c) => _columns[c].Name;
    public string ColumnType(int c) => _columns[c].TypeName;
    public bool IsNumeric(int c) => ValueText.IsNumeric(_columns[c].ClrType);
    public bool IsKey(int c) => _edit is not null && Array.IndexOf(_edit.KeyColumns, c) >= 0;
    public int RowCount => _rows.Count + _inserts.Count;
    public bool CanEdit => _edit is not null;

    private RowKey KeyOf(object?[] row) => new(_edit!.KeyColumns.Select(i => row[i]).ToList());

    public object? Value(int row, int col)
    {
        if (row >= _rows.Count)
        {
            var ins = _inserts[row - _rows.Count];
            return ins.Values.TryGetValue(_columns[col].Name, out var v) ? v : null;
        }
        var r = _rows[row];
        if (_edit is not null && _edit.Staging.Count > 0 && _edit.Staging.TryGetEdit(_edit.Table, KeyOf(r), _columns[col].Name, out var edited))
            return edited;
        return r[col];
    }

    public CellState State(int row, int col)
    {
        if (_edit is null) return CellState.Normal;
        if (row >= _rows.Count) return CellState.Inserted;
        if (_edit.Staging.Count == 0) return CellState.Normal;
        var key = KeyOf(_rows[row]);
        if (_edit.Staging.IsDeleted(_edit.Table, key)) return CellState.Deleted;
        return _edit.Staging.TryGetEdit(_edit.Table, key, _columns[col].Name, out _) ? CellState.Edited : CellState.Normal;
    }

    public string? SetValue(int row, int col, string? text)
    {
        if (_edit is null) return null;
        var column = _columns[col];
        object? value = null;
        if (text is not null && !ValueText.TryParse(text, column.ClrType, out value))
            return Loc.F("grid.badValue", text, column.TypeName);
        try
        {
            if (row >= _rows.Count) _edit.Staging.EditInsert(_inserts[row - _rows.Count], column.Name, value);
            else _edit.Staging.Edit(_edit.Table, KeyOf(_rows[row]), column.Name, _rows[row][col], value);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public void DeleteRows(IEnumerable<int> rows)
    {
        if (_edit is null) return;
        foreach (var r in rows.OrderByDescending(x => x).ToList())
        {
            if (r >= _rows.Count)
            {
                if (r - _rows.Count < _inserts.Count) _edit.Staging.Remove(_inserts[r - _rows.Count]);
            }
            else _edit.Staging.Delete(_edit.Table, KeyOf(_rows[r]));
        }
    }

    public void AddRow()
    {
        if (_edit is null) return;
        _edit.Staging.AddInsert(_edit.Table);
    }

    /// <summary>Rows for export: the selection, or all of them.</summary>
    public IEnumerable<object?[]> RowsFor(IEnumerable<int>? rows, IReadOnlyList<int>? cols)
    {
        var list = rows ?? Enumerable.Range(0, RowCount);
        foreach (var r in list)
        {
            if (cols is null) yield return Enumerable.Range(0, ColumnCount).Select(c => Value(r, c)).ToArray();
            else yield return cols.Select(c => Value(r, c)).ToArray();
        }
    }
}

/// <summary>
/// Turns a running statement's output (background thread) into <see cref="ResultViewModel"/>s on the UI thread,
/// in order. <paramref name="added"/> is called for every new result.
/// </summary>
public sealed class ResultCollector(string statement, Func<int> nextIndex, Action<ResultViewModel> added, System.Diagnostics.Stopwatch clock) : IResultSink
{
    private ResultViewModel? _current;
    public List<ResultViewModel> All { get; } = new();

    public void OnResult(IReadOnlyList<ResultColumn> columns)
    {
        var r = new ResultViewModel(statement, nextIndex());
        All.Add(r);
        _current = r;
        Dispatcher.UIThread.Post(() =>
        {
            r.SetColumns(columns);
            r.UpdateSummary();
            added(r);
        });
    }

    public void OnRows(IReadOnlyList<object?[]> rows)
    {
        var r = _current!;
        Dispatcher.UIThread.Post(() => r.Append(rows));
    }

    public void OnEnd(long recordsAffected, bool truncated)
    {
        var r = _current;
        _current = null;
        if (r is null)
        {
            // A statement without rows: its outcome still gets an entry.
            r = new ResultViewModel(statement, nextIndex());
            All.Add(r);
            Dispatcher.UIThread.Post(() => added(r));
        }
        long ms = clock.ElapsedMilliseconds;
        Dispatcher.UIThread.Post(() => r.Finish(recordsAffected, truncated, ms));
    }
}
