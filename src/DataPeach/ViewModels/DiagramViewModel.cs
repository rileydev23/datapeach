using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataPeach.Controls;
using DataPeach.Core;
using DataPeach.Core.Db;
using DataPeach.Core.Diagram;

namespace DataPeach.ViewModels;

/// <summary>
/// The tables of a database or schema drawn with the foreign keys between them, or one table and the ones a few keys
/// away. Read only: it shows the structure, it never changes it.
/// </summary>
public sealed partial class DiagramViewModel : TabViewModel
{
    private readonly ConnectionViewModel _owner;
    private SchemaSnapshot _schema = SchemaSnapshot.Empty;
    private List<DbForeignKey>? _keys;
    private CancellationTokenSource? _load;

    /// <summary>The database it draws (null: the connection's own, or an engine without databases).</summary>
    public string? Database { get; }
    /// <summary>The schema it draws; null for every schema, or when it is drawn around <see cref="Focus"/>.</summary>
    public string? Schema { get; }
    public DbTable? Focus { get; private set; }

    [ObservableProperty] private int _hops = 1;
    [ObservableProperty] private DiagramSource? _source;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _status = "";

    public bool HasFocus => Focus is not null;
    public string FocusText => Focus is null ? "" : Loc.F("diagram.around", Focus.Display);
    public bool IsHops1 => Hops == 1;
    public bool IsHops2 => Hops == 2;
    public bool IsHopsAll => Hops == 0;

    public DiagramViewModel(ConnectionViewModel owner, string? database, string? schema, DbTable? focus)
    {
        _owner = owner;
        Database = database;
        Schema = focus is null ? schema : null;
        SetFocus(focus);
    }

    /// <summary>Whether this tab already draws what <paramref name="focus"/> or <paramref name="schema"/> asks for.</summary>
    public bool Shows(string? database, string? schema, DbTable? focus) =>
        Database == database && (focus is null ? Focus is null && Schema == schema : Focus?.FullName == focus.FullName);

    private void SetFocus(DbTable? focus)
    {
        Focus = focus;
        var name = focus?.Name ?? Schema ?? Database ?? _owner.CurrentDatabase ?? _owner.Entry.Name;
        Title = Loc.F("diagram.title", name);
        ToolTip = Loc.F("diagram.title", focus?.FullName ?? (Database is null || Schema is null ? name : Database + "." + Schema));
        OnPropertyChanged(nameof(Focus));
        OnPropertyChanged(nameof(HasFocus));
        OnPropertyChanged(nameof(FocusText));
    }

    partial void OnHopsChanged(int value)
    {
        OnPropertyChanged(nameof(IsHops1));
        OnPropertyChanged(nameof(IsHops2));
        OnPropertyChanged(nameof(IsHopsAll));
        Build();
    }

    /// <summary>Reads the structure (if it was not read yet) and every foreign key of the database, then draws.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        _load?.Cancel();
        var cts = _load = new CancellationTokenSource();
        IsLoading = true;
        Status = Loc.T("diagram.reading");
        try
        {
            var schema = await _owner.LoadedSchemaAsync(Database);
            var keys = await _owner.LoadForeignKeysAsync(Database, cts.Token);
            if (cts.IsCancellationRequested) return;
            (_schema, _keys) = (schema, keys);
            if (Focus is not null) SetFocus(_schema.Find(Focus.Schema, Focus.Name) ?? Focus);
            Build();
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) Status = ex.Message;
        }
        finally
        {
            if (_load == cts) IsLoading = false;
        }
    }

    /// <summary>Draws again from what was read, every table back in its automatic place.</summary>
    private void Build()
    {
        if (_keys is null) return;
        var g = DiagramGraph.Build(_schema, _keys, Schema, Focus, Hops);
        Source = new DiagramSource(g) { Selected = Focus is null ? null : g.Tables.FirstOrDefault(t => t.FullName == Focus.FullName) };
        IsEmpty = g.Tables.Count == 0;
        Status = Loc.Plural("common.tables", g.Tables.Count) + " · " + Loc.Plural("diagram.keys", g.Edges.Count);
    }

    [RelayCommand]
    private void Arrange() => Build();

    [RelayCommand]
    private void SetHops(string hops) => Hops = int.Parse(hops, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// "Diagrama alrededor de esta tabla" from a table of the diagram: a diagram around another table moves to it;
    /// one of a whole database or schema stays, and the new one opens in its own tab.
    /// </summary>
    public void Refocus(DbTable t)
    {
        if (Focus is null)
        {
            _owner.OpenDiagram(Database, null, t);
            return;
        }
        SetFocus(t);
        Build();
    }

    public void OpenTable(DbTable t) => _owner.OpenTable(t);

    /// <summary>A table selected in the diagram: its columns, indexes and keys in the detail panel.</summary>
    public void ShowDetails(DbTable t) => _owner.ShowTableDetails(t);

    /// <summary>After the structure is read again its objects are new; the next load draws with them.</summary>
    public void Rebind(SchemaSnapshot fresh)
    {
        _schema = fresh;
        if (Focus is not null && fresh.Find(Focus.Schema, Focus.Name) is { } t) SetFocus(t);
    }
}
