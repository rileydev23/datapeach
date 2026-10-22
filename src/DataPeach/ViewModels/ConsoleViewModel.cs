using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataPeach.Core;
using DataPeach.Core.Sql;
using DataPeach.Views;

namespace DataPeach.ViewModels;

/// <summary>
/// A SQL console: a .sql file (saved by itself as you type) and the results of what you run from it. Lives in the
/// connection's queries folder, so with a project folder it can be versioned with git.
/// </summary>
public sealed partial class ConsoleViewModel : TabViewModel
{
    private readonly ConnectionViewModel _owner;
    private readonly DispatcherTimer _saveTimer;
    private CancellationTokenSource? _run;
    private int _resultIndex;

    public string FilePath { get; private set; }
    public TextDocument Document { get; }
    public ObservableCollection<ResultViewModel> Results { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResults))]
    private ResultViewModel? _selectedResult;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isRunning;
    [ObservableProperty] private string _runStatus = "";

    public bool HasResults => SelectedResult is not null;
    partial void OnSelectedResultChanged(ResultViewModel? oldValue, ResultViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }
    public bool ManyResults => Results.Count > 1;
    public DbKind Kind => _owner.Entry.Kind;

    /// <summary>Set by the view: caret offset, selection start and length.</summary>
    public Func<(int Caret, int SelStart, int SelLength)>? Caret { get; set; }

    public ConsoleViewModel(ConnectionViewModel owner, string file)
    {
        _owner = owner;
        FilePath = file;
        Title = Path.GetFileNameWithoutExtension(file);
        ToolTip = file;
        string text = "";
        try { if (File.Exists(file)) text = File.ReadAllText(file); } catch { /* starts empty */ }
        Document = new TextDocument(text);
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        Document.TextChanged += (_, _) => { _saveTimer.Stop(); _saveTimer.Start(); };
        Results.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ManyResults));
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, Document.Text);
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex) { _owner.Notice = ex.Message; }
    }

    public bool Rename(string newName)
    {
        var target = Path.Combine(Path.GetDirectoryName(FilePath)!, newName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ? newName : newName + ".sql");
        if (File.Exists(target)) return false;
        Save();
        File.Move(FilePath, target);
        FilePath = target;
        Title = Path.GetFileNameWithoutExtension(target);
        ToolTip = target;
        return true;
    }

    /// <summary>Ctrl+Enter: the selection's statements, or else the one under the caret.</summary>
    [RelayCommand]
    private Task Run()
    {
        var text = Document.Text;
        var (caret, selStart, selLen) = Caret?.Invoke() ?? (0, 0, 0);
        List<string> statements;
        if (selLen > 0)
            statements = SqlStatements.Split(text.Substring(selStart, selLen), Kind).Select(s => s.Text).ToList();
        else
            statements = SqlStatements.At(text, caret, Kind) is { } s ? new List<string> { s.Text } : new();
        return RunStatementsAsync(statements);
    }

    /// <summary>Ctrl+Shift+Enter: every statement, one after the other; the first error stops the rest.</summary>
    [RelayCommand]
    private Task RunAll() => RunStatementsAsync(SqlStatements.Split(Document.Text, Kind).Select(s => s.Text).ToList());

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _run?.Cancel();

    private async Task RunStatementsAsync(List<string> statements)
    {
        if (statements.Count == 0 || IsRunning) return;
        if (!await _owner.ConfirmWritesAsync(statements)) return;
        if (await _owner.EnsureSessionAsync() is not { } session) return;

        Save();
        _run = new CancellationTokenSource();
        var ct = _run.Token;
        IsRunning = true;
        Results.Clear();
        SelectedResult = null;
        _resultIndex = 0;
        var total = Stopwatch.StartNew();
        int done = 0;
        bool changedStructure = false;
        try
        {
            foreach (var sql in statements)
            {
                RunStatus = statements.Count > 1 ? Loc.F("console.runningN", done + 1, statements.Count) : Loc.T("result.running");
                var clock = Stopwatch.StartNew();
                var collector = new ResultCollector(sql, () => ++_resultIndex, r =>
                {
                    Results.Add(r);
                    // The first grid wins the view; a later statement's grid only if nothing is shown yet.
                    if (SelectedResult is null || (!SelectedResult.HasGrid && r.HasGrid)) SelectedResult = r;
                }, clock);
                try
                {
                    await Task.Run(() => session.RunAsync(sql, collector, _owner.MaxRows, ct), ct);
                    done++;
                    if (SqlStatements.IsWrite(sql, Kind)) changedStructure |= IsDdl(sql);
                }
                catch (Exception ex)
                {
                    long ms = clock.ElapsedMilliseconds;
                    var msg = ct.IsCancellationRequested ? Loc.T("console.cancelled") : ex.Message;
                    // Let the rows already posted land first, then show the error at the end.
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    var r = new ResultViewModel(sql, ++_resultIndex);
                    r.Fail(msg, ms);
                    Results.Add(r);
                    SelectedResult = r;
                    foreach (var open in collector.All.Where(x => x.Running)) open.Finish(-1, true, ms);
                    break;
                }
            }
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            IsRunning = false;
            _run.Dispose();
            _run = null;
            var last = Results.LastOrDefault();
            RunStatus = statements.Count > 1
                ? Loc.F("console.ranN", done, statements.Count, ResultViewModel.FormatMs(total.ElapsedMilliseconds))
                : last?.Summary ?? "";
            _owner.Notice = RunStatus;
            if (changedStructure) _ = _owner.RefreshSchemaAsync();
        }
    }

    private static bool IsDdl(string sql)
    {
        var first = SqlTokenizer.Tokenize(sql).FirstOrDefault(t => t.Kind == SqlTokenKind.Keyword);
        return first.Length > 0 && sql.Substring(first.Start, first.Length).ToUpperInvariant() is "CREATE" or "DROP" or "ALTER";
    }

    public CompletionResult Complete(int caret) =>
        SqlCompletion.Suggest(Document.Text, caret, _owner.Schema, _owner.Dialect);

    /// <summary>Puts text at the end (or replaces an empty console) and selects it: "abrir en consola".</summary>
    public void InsertText(string sql)
    {
        if (Document.TextLength > 0 && !Document.Text.EndsWith('\n')) Document.Insert(Document.TextLength, "\n");
        if (Document.TextLength > 0) Document.Insert(Document.TextLength, "\n");
        Document.Insert(Document.TextLength, sql);
    }

    public Task ExportAsync(Core.Data.ExportFormat f) =>
        SelectedResult is { HasGrid: true } r ? _owner.ExportAsync(r, f, null, null, "result") : Task.CompletedTask;
}
