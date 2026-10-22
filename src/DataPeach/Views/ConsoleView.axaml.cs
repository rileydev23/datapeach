using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit.CodeCompletion;
using DataPeach.Controls;
using DataPeach.ViewModels;

namespace DataPeach.Views;

/// <summary>
/// The SQL editor and its results. The view is recycled between consoles, so everything is re-attached in
/// <see cref="OnDataContextChanged"/>; the document belongs to the view model.
/// </summary>
public partial class ConsoleView : UserControl
{
    private readonly SqlColorizer _colorizer = new();
    private ConsoleViewModel? _vm;
    private CompletionWindow? _completion;

    public ConsoleView()
    {
        InitializeComponent();
        Editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.IndentationSize = 4;
        Editor.Options.HighlightCurrentLine = true;
        Editor.TextArea.TextView.CurrentLineBackground = Palette.FromHex("#232428");
        Editor.TextArea.TextView.CurrentLineBorder = new Avalonia.Media.Pen(Palette.FromHex("#232428"));
        Editor.TextArea.SelectionBrush = Palette.Selection;
        Editor.TextArea.Caret.CaretBrush = Palette.Accent;
        Editor.Options.EnableHyperlinks = false;
        Editor.TextChanged += (_, _) => Recolor();
        Editor.TextArea.TextEntered += OnTextEntered;
        // Tunnel: Ctrl+Enter must run before the editor turns it into a new line.
        Editor.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && _vm is { IsRunning: true }) { _vm.CancelCommand.Execute(null); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        Grid.MenuRequested += g =>
        {
            if (_vm?.SelectedResult is { } r && FindOwner() is { } conn)
                MenuPresenter.Show(g, GridMenus.ForGrid(g, conn, r, "result", null));
        };
        Grid.Notice += text => { if (FindOwner() is { } conn) conn.Notice = text; };
        ExportButton.Click += (_, _) =>
        {
            if (_vm?.SelectedResult is { HasGrid: true } r && FindOwner() is { } conn)
                MenuPresenter.Show(ExportButton, GridMenus.Export(conn, r, "result"));
        };
    }

    private ConnectionViewModel? FindOwner() => this.FindAncestorOfType<ConnectionView>()?.DataContext as ConnectionViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _completion?.Close();
        if (_vm is not null) _vm.Caret = null;
        _vm = DataContext as ConsoleViewModel;
        if (_vm is null) return;
        _colorizer.Dialect = _vm.Kind;
        Editor.Document = _vm.Document;
        _vm.Caret = () => (Editor.CaretOffset, Editor.SelectionStart, Editor.SelectionLength);
        Recolor();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Editor.TextArea.Focus(), Avalonia.Threading.DispatcherPriority.Background);
    }

    private void Recolor()
    {
        _colorizer.Update(Editor.Document?.Text ?? "");
        Editor.TextArea.TextView.Redraw();
    }

    private void OnResultClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null && (sender as Control)?.DataContext is ResultViewModel r) _vm.SelectedResult = r;
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (e.Key == Key.Enter && ctrl)
        {
            _completion?.Close();
            if (shift) _vm.RunAllCommand.Execute(null);
            else _vm.RunCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Space && ctrl)
        {
            ShowCompletion(force: true);
            e.Handled = true;
        }
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || _completion is not null) return;
        char c = e.Text[^1];
        // Like DataGrip: after "alias." at once, and after the second letter of a word.
        if (c == '.') ShowCompletion(force: false);
        else if (char.IsLetter(c) || c == '_')
        {
            int caret = Editor.CaretOffset, start = caret;
            var doc = Editor.Document;
            while (start > 0 && (char.IsLetterOrDigit(doc.GetCharAt(start - 1)) || doc.GetCharAt(start - 1) == '_')) start--;
            if (caret - start == 2) ShowCompletion(force: false);
        }
    }

    private void ShowCompletion(bool force)
    {
        if (_vm is null) return;
        var result = _vm.Complete(Editor.CaretOffset);
        if (result.Items.Count == 0) return;
        if (!force && result.Items.Count == 1 && result.Length == result.Items[0].Label.Length) return;
        _completion = new CompletionWindow(Editor.TextArea)
        {
            StartOffset = result.Start,
            EndOffset = result.Start + result.Length,
            CloseWhenCaretAtBeginning = true,
            MinWidth = 320,
        };
        foreach (var item in result.Items) _completion.CompletionList.CompletionData.Add(new SqlCompletionData(item));
        if (result.Length > 0) _completion.CompletionList.SelectItem(Editor.Document.GetText(result.Start, result.Length));
        _completion.Closed += (_, _) => _completion = null;
        _completion.Show();
    }
}
