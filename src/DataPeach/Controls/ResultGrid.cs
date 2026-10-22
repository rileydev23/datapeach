using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DataPeach.Core.Data;

namespace DataPeach.Controls;

/// <summary>
/// The result grid, drawn by hand so its cost depends only on what fits on screen, never on the number of rows: a
/// fixed header, row numbers, its own 4 px scroll bars, range selection, copy, column resizing and in-place editing.
/// Colors and sizes follow docs/DISENO.md ("Rejilla de resultados").
/// </summary>
public sealed class ResultGrid : Control
{
    public static readonly StyledProperty<IGridSource?> SourceProperty =
        AvaloniaProperty.Register<ResultGrid, IGridSource?>(nameof(Source));

    public IGridSource? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>Right click: the owner shows its menu (it reads the selection from the grid).</summary>
    public event Action<ResultGrid>? MenuRequested;
    /// <summary>A message for the status bar (an edit that did not fit the column type…).</summary>
    public event Action<string>? Notice;

    private const double RowH = 22, HeaderH = 26, Bar = 4, MinCol = 44, MaxAutoCol = 360, Pad = 8;
    private static readonly Typeface Mono = new("Cascadia Mono, Consolas, Courier New, DejaVu Sans Mono, Liberation Mono");
    private static readonly Typeface Ui = new("Segoe UI, Inter, DejaVu Sans", FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface MonoItalic = new("Cascadia Mono, Consolas, Courier New, DejaVu Sans Mono, Liberation Mono", FontStyle.Italic);
    private const double FontSz = 12;

    private readonly ScrollBar _v = new() { Orientation = Orientation.Vertical, AllowAutoHide = false };
    private readonly ScrollBar _h = new() { Orientation = Orientation.Horizontal, AllowAutoHide = false };
    private readonly TextBox _editor = new() { IsVisible = false, BorderThickness = new Thickness(1), Padding = new Thickness(5, 2), MinHeight = 0, FontSize = FontSz, FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New, DejaVu Sans Mono") };

    private IGridSource? _src;
    private double[] _widths = Array.Empty<double>();
    private double _gutter = 40;
    private double _x, _y; // scroll offsets in pixels
    private int _anchorRow = -1, _anchorCol, _caretRow = -1, _caretCol;
    private bool _wholeRows;
    private bool _selecting;
    private int _resizing = -1;
    private double _resizeStartX, _resizeStartW;
    private int _editRow = -1, _editCol = -1;
    private readonly Dictionary<(string, int), FormattedText> _textCache = new();
    private double _charW = 7.2;

    public ResultGrid()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursor.Default;
        foreach (var c in new Control[] { _v, _h, _editor })
        {
            VisualChildren.Add(c);
            LogicalChildren.Add(c);
        }
        _v.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) { _y = _v.Value; PlaceEditor(); InvalidateVisual(); } };
        _h.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) { _x = _h.Value; PlaceEditor(); InvalidateVisual(); } };
        _editor.KeyDown += OnEditorKey;
        _editor.LostFocus += (_, _) => { if (_editor.IsVisible) CommitEdit(); };
        _charW = Text("0", Palette.Text, Mono).Width;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SourceProperty) return;
        if (_src is not null) { _src.Changed -= OnChanged; _src.Reset -= OnReset; }
        _src = Source;
        if (_src is not null) { _src.Changed += OnChanged; _src.Reset += OnReset; }
        OnReset();
    }

    private void OnReset()
    {
        CancelEdit();
        _x = _y = 0;
        _anchorRow = _caretRow = -1;
        _anchorCol = _caretCol = 0;
        _textCache.Clear();
        _widths = new double[_src?.ColumnCount ?? 0];
        AutoSizeColumns();
        UpdateScroll();
        InvalidateVisual();
    }

    private void OnChanged()
    {
        // First rows of a stream arrive in batches: widen the columns while there are few rows.
        if (_src is { RowCount: <= 1000 }) AutoSizeColumns();
        if (_textCache.Count > 6000) _textCache.Clear();
        UpdateScroll();
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ geometry

    private int Rows => _src?.RowCount ?? 0;
    private int Cols => _widths.Length;
    private double DataLeft => _gutter;
    private double DataTop => HeaderH;
    private double DataWidth => Math.Max(0, Bounds.Width - _gutter - Bar);
    private double DataHeight => Math.Max(0, Bounds.Height - HeaderH - Bar);
    private double TotalWidth => _widths.Sum();
    private double TotalHeight => Rows * RowH;

    private double ColX(int c)
    {
        double x = 0;
        for (int i = 0; i < c; i++) x += _widths[i];
        return x;
    }

    private void AutoSizeColumns()
    {
        if (_src is null) return;
        int sample = Math.Min(_src.RowCount, 200);
        for (int c = 0; c < _widths.Length; c++)
        {
            double w = Text(_src.ColumnName(c), Palette.Text, Ui).Width + Pad * 2 + (_src.IsKey(c) ? 14 : 0);
            int longest = 0;
            for (int r = 0; r < sample; r++)
            {
                var s = ValueText.Cell(_src.Value(r, c), 60);
                longest = Math.Max(longest, s?.Length ?? 4);
            }
            w = Math.Max(w, longest * _charW + Pad * 2);
            // Never shrink while streaming: columns that already grew stay put.
            _widths[c] = Math.Max(_widths[c], Math.Clamp(w, MinCol, MaxAutoCol));
        }
        _gutter = Math.Max(36, (Math.Max(_src.RowCount, 1).ToString(CultureInfo.InvariantCulture).Length) * _charW + Pad * 2);
    }

    private void UpdateScroll()
    {
        _v.Maximum = Math.Max(0, TotalHeight - DataHeight);
        _v.ViewportSize = DataHeight;
        _v.LargeChange = DataHeight;
        _v.SmallChange = RowH * 3;
        _h.Maximum = Math.Max(0, TotalWidth - DataWidth);
        _h.ViewportSize = DataWidth;
        _h.LargeChange = DataWidth;
        _h.SmallChange = 60;
        _y = Math.Clamp(_y, 0, _v.Maximum);
        _x = Math.Clamp(_x, 0, _h.Maximum);
        _v.Value = _y;
        _h.Value = _x;
        _v.IsVisible = _v.Maximum > 0;
        _h.IsVisible = _h.Maximum > 0;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var c in new Control[] { _v, _h, _editor }) c.Measure(availableSize);
        return new Size(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width,
                        double.IsInfinity(availableSize.Height) ? 300 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _v.Arrange(new Rect(finalSize.Width - Bar, HeaderH, Bar, Math.Max(0, finalSize.Height - HeaderH - Bar)));
        _h.Arrange(new Rect(_gutter, finalSize.Height - Bar, Math.Max(0, finalSize.Width - _gutter - Bar), Bar));
        Dispatcher(UpdateScroll);
        PlaceEditor();
        return finalSize;
    }

    private static void Dispatcher(Action a) => Avalonia.Threading.Dispatcher.UIThread.Post(a, Avalonia.Threading.DispatcherPriority.Render);

    // ------------------------------------------------------------------ drawing

    private FormattedText Text(string s, IBrush brush, Typeface face)
    {
        int key = brush.GetHashCode() ^ face.GetHashCode();
        if (_textCache.TryGetValue((s, key), out var ft)) return ft;
        ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, FontSz, brush);
        _textCache[(s, key)] = ft;
        return ft;
    }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        ctx.FillRectangle(Palette.Code, new Rect(0, 0, b.Width, b.Height));
        if (_src is null || Cols == 0) return;

        int first = Math.Max(0, (int)(_y / RowH));
        int last = Math.Min(Rows - 1, (int)((_y + DataHeight) / RowH));
        var (r0, c0, r1, c1) = Range();
        var gridLine = new Pen(Palette.Panel, 1);

        // Visible columns.
        int cFirst = 0;
        double x = -_x;
        while (cFirst < Cols && x + _widths[cFirst] < 0) { x += _widths[cFirst]; cFirst++; }
        double startX = x;

        using (ctx.PushClip(new Rect(DataLeft, DataTop, DataWidth, DataHeight)))
        {
            for (int r = first; r <= last; r++)
            {
                double y = DataTop + r * RowH - _y;
                double cx = DataLeft + startX;
                for (int c = cFirst; c < Cols && cx < DataLeft + DataWidth; c++)
                {
                    var rect = new Rect(cx, y, _widths[c], RowH);
                    var state = _src.State(r, c);
                    bool selected = r >= r0 && r <= r1 && c >= c0 && c <= c1;
                    IBrush? bg = selected ? Palette.Selection
                        : state switch
                        {
                            CellState.Edited => Palette.MatchBg,
                            CellState.Inserted => Palette.AddedBg,
                            CellState.Deleted => Palette.RemovedBg,
                            _ => null,
                        };
                    if (bg is not null) ctx.FillRectangle(bg, rect);
                    var v = _src.Value(r, c);
                    var s = ValueText.Cell(v);
                    var fg = state switch
                    {
                        CellState.Edited => Palette.AmberLight,
                        CellState.Inserted => Palette.AddedFg,
                        CellState.Deleted => Palette.RemovedFg,
                        _ => selected ? Brushes.White : Palette.Text,
                    };
                    var ft = s is null ? Text(ValueText.Null, Palette.Dim, MonoItalic) : Text(s, fg, Mono);
                    using (ctx.PushClip(rect.Deflate(new Thickness(Pad - 2, 0))))
                    {
                        double tx = _src.IsNumeric(c) && s is not null ? rect.Right - Pad - ft.Width : rect.X + Pad;
                        ctx.DrawText(ft, new Point(tx, y + (RowH - ft.Height) / 2));
                    }
                    if (state == CellState.Deleted) ctx.DrawLine(new Pen(Palette.RemovedFg, 1), new Point(rect.X + 2, y + RowH / 2), new Point(rect.Right - 2, y + RowH / 2));
                    ctx.DrawLine(gridLine, new Point(rect.Right - 0.5, y), new Point(rect.Right - 0.5, y + RowH));
                    cx += _widths[c];
                }
                ctx.DrawLine(gridLine, new Point(DataLeft, y + RowH - 0.5), new Point(DataLeft + DataWidth, y + RowH - 0.5));
            }

            // Current cell.
            if (_caretRow >= first && _caretRow <= last && _caretCol < Cols && !_wholeRows)
            {
                var cr = new Rect(DataLeft + ColX(_caretCol) - _x, DataTop + _caretRow * RowH - _y, _widths[_caretCol], RowH);
                ctx.DrawRectangle(new Pen(Palette.Accent, 1), cr.Deflate(0.5));
            }
        }

        // Header.
        ctx.FillRectangle(Palette.Panel, new Rect(0, 0, b.Width, HeaderH));
        using (ctx.PushClip(new Rect(DataLeft, 0, Math.Max(0, b.Width - DataLeft), HeaderH)))
        {
            double hx = DataLeft + startX;
            for (int c = cFirst; c < Cols && hx < b.Width; c++)
            {
                var rect = new Rect(hx, 0, _widths[c], HeaderH);
                bool colSel = c >= c0 && c <= c1 && _anchorRow >= 0;
                if (colSel) ctx.FillRectangle(Palette.Panel2, rect);
                double tx = rect.X + Pad;
                if (_src.IsKey(c))
                {
                    var key = Text("⚿", Palette.Amber, Ui);
                    ctx.DrawText(key, new Point(tx, (HeaderH - key.Height) / 2));
                    tx += key.Width + 4;
                }
                var ft = Text(_src.ColumnName(c), Palette.Text, Ui);
                using (ctx.PushClip(rect.Deflate(new Thickness(2, 0))))
                    ctx.DrawText(ft, new Point(tx, (HeaderH - ft.Height) / 2));
                ctx.DrawLine(new Pen(Palette.Border, 1), new Point(rect.Right - 0.5, 4), new Point(rect.Right - 0.5, HeaderH - 4));
                hx += _widths[c];
            }
        }
        ctx.DrawLine(new Pen(Palette.Border, 1), new Point(0, HeaderH - 0.5), new Point(b.Width, HeaderH - 0.5));

        // Row numbers.
        ctx.FillRectangle(Palette.Bg, new Rect(0, HeaderH, _gutter, b.Height - HeaderH));
        using (ctx.PushClip(new Rect(0, DataTop, _gutter, DataHeight)))
        {
            for (int r = first; r <= last; r++)
            {
                double y = DataTop + r * RowH - _y;
                bool sel = r >= r0 && r <= r1 && _anchorRow >= 0;
                var ft = Text((r + 1).ToString(CultureInfo.InvariantCulture), sel ? Palette.ContextFg : Palette.Gutter, Mono);
                ctx.DrawText(ft, new Point(_gutter - Pad - ft.Width, y + (RowH - ft.Height) / 2));
            }
        }
        ctx.DrawLine(new Pen(Palette.Border, 1), new Point(_gutter - 0.5, 0), new Point(_gutter - 0.5, b.Height));
    }

    // ------------------------------------------------------------------ selection

    /// <summary>Selected rectangle (rows and columns, inclusive); r0 = -1 when nothing is selected.</summary>
    public (int R0, int C0, int R1, int C1) Range()
    {
        if (_anchorRow < 0 || _caretRow < 0 || Rows == 0) return (-1, -1, -2, -2);
        int r0 = Math.Min(_anchorRow, _caretRow), r1 = Math.Min(Math.Max(_anchorRow, _caretRow), Rows - 1);
        if (_wholeRows) return (r0, 0, r1, Cols - 1);
        return (r0, Math.Min(_anchorCol, _caretCol), r1, Math.Max(_anchorCol, _caretCol));
    }

    public bool HasSelection => Range().R0 >= 0;
    public int CaretRow => _caretRow;
    public int CaretCol => _caretCol;

    public IEnumerable<int> SelectedRows()
    {
        var (r0, _, r1, _) = Range();
        for (int r = r0; r >= 0 && r <= r1; r++) yield return r;
    }

    public IReadOnlyList<int> SelectedColumns()
    {
        var (r0, c0, _, c1) = Range();
        if (r0 < 0) return Array.Empty<int>();
        return Enumerable.Range(c0, c1 - c0 + 1).ToList();
    }

    private void Select(int row, int col, bool extend, bool wholeRows = false)
    {
        if (Rows == 0 || Cols == 0) return;
        row = Math.Clamp(row, 0, Rows - 1);
        col = Math.Clamp(col, 0, Cols - 1);
        if (!extend || _anchorRow < 0) { _anchorRow = row; _anchorCol = col; _wholeRows = wholeRows; }
        _caretRow = row;
        _caretCol = col;
        ScrollIntoView(row, col);
        InvalidateVisual();
    }

    public void SelectAll()
    {
        if (Rows == 0) return;
        _anchorRow = 0; _anchorCol = 0; _caretRow = Rows - 1; _caretCol = Cols - 1; _wholeRows = false;
        InvalidateVisual();
    }

    private void ScrollIntoView(int row, int col)
    {
        double top = row * RowH, bottom = top + RowH;
        if (top < _y) _y = top;
        else if (bottom > _y + DataHeight) _y = bottom - DataHeight;
        if (!_wholeRows && col < Cols)
        {
            double left = ColX(col), right = left + _widths[col];
            if (left < _x) _x = left;
            else if (right > _x + DataWidth) _x = Math.Min(left, right - DataWidth);
        }
        UpdateScroll();
    }

    private (int Row, int Col, bool Header, bool Gutter, int Border) Hit(Point p)
    {
        int row = p.Y < DataTop ? -1 : (int)((p.Y - DataTop + _y) / RowH);
        int col = -1, border = -1;
        double x = DataLeft - _x;
        for (int c = 0; c < Cols; c++)
        {
            double right = x + _widths[c];
            if (p.X >= x && p.X < right) col = c;
            if (Math.Abs(p.X - right) <= 4 && p.X > DataLeft) border = c;
            x = right;
        }
        if (p.X >= DataLeft && col < 0 && Cols > 0) col = Cols - 1;
        return (row, col, p.Y < HeaderH, p.X < _gutter, border);
    }

    // ------------------------------------------------------------------ input

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_src is null) return;
        Focus();
        var p = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        var hit = Hit(p);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (hit.Header && hit.Border >= 0 && props.IsLeftButtonPressed)
        {
            _resizing = hit.Border;
            _resizeStartX = p.X;
            _resizeStartW = _widths[hit.Border];
            e.Pointer.Capture(this);
            return;
        }
        if (_editor.IsVisible) CommitEdit();

        if (hit.Header)
        {
            if (hit.Col >= 0 && Rows > 0)
            {
                // Whole column.
                _anchorRow = 0; _caretRow = Rows - 1; _wholeRows = false;
                if (!shift || _anchorRow < 0) _anchorCol = hit.Col;
                _caretCol = hit.Col;
                InvalidateVisual();
            }
            return;
        }
        if (hit.Row < 0 || hit.Row >= Rows) return;

        bool inside = Range() is var (r0, c0, r1, c1) && hit.Row >= r0 && hit.Row <= r1 && (hit.Gutter || hit.Col >= c0 && hit.Col <= c1);
        if (props.IsRightButtonPressed)
        {
            if (!inside) Select(hit.Row, Math.Max(hit.Col, 0), false, hit.Gutter);
            MenuRequested?.Invoke(this);
            e.Handled = true;
            return;
        }
        if (e.ClickCount == 2 && !hit.Gutter && hit.Col >= 0)
        {
            BeginEdit();
            return;
        }
        Select(hit.Row, Math.Max(hit.Col, 0), shift, hit.Gutter);
        _selecting = true;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_resizing >= 0)
        {
            _widths[_resizing] = Math.Max(MinCol, _resizeStartW + p.X - _resizeStartX);
            UpdateScroll();
            InvalidateVisual();
            return;
        }
        var hit = Hit(p);
        Cursor = hit.Header && hit.Border >= 0 ? new Cursor(StandardCursorType.SizeWestEast) : Cursor.Default;
        if (_selecting && Rows > 0)
        {
            int row = Math.Clamp((int)((p.Y - DataTop + _y) / RowH), 0, Rows - 1);
            Select(row, Math.Max(hit.Col, 0), extend: true, _wholeRows);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _selecting = false;
        _resizing = -1;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        bool horizontal = e.KeyModifiers.HasFlag(KeyModifiers.Shift) || Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y);
        if (horizontal) _x -= (Math.Abs(e.Delta.X) > 0 ? e.Delta.X : e.Delta.Y) * 60;
        else _y -= e.Delta.Y * RowH * 3;
        UpdateScroll();
        PlaceEditor();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _src is null || _editor.IsVisible) return;
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        int page = Math.Max(1, (int)(DataHeight / RowH) - 1);
        int r = Math.Max(_caretRow, 0), c = _caretCol;
        switch (e.Key)
        {
            case Key.Up: Select(r - 1, c, shift); break;
            case Key.Down: Select(_caretRow < 0 ? 0 : r + 1, c, shift); break;
            case Key.Left: Select(r, c - 1, shift); break;
            case Key.Right: Select(r, c + 1, shift); break;
            case Key.PageUp: Select(r - page, c, shift); break;
            case Key.PageDown: Select(r + page, c, shift); break;
            case Key.Home: Select(ctrl ? 0 : r, 0, shift); break;
            case Key.End: Select(ctrl ? Rows - 1 : r, Cols - 1, shift); break;
            case Key.Tab: Select(r, shift ? c - 1 : c + 1, false); break;
            case Key.A when ctrl: SelectAll(); break;
            case Key.C when ctrl: _ = CopyAsync(withHeader: shift); break;
            case Key.F2 or Key.Enter: BeginEdit(); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        // Typing on a cell starts editing it with what was typed, like a spreadsheet.
        if (e.Handled || _editor.IsVisible || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        if (BeginEdit(e.Text)) e.Handled = true;
    }

    // ------------------------------------------------------------------ copy

    /// <summary>Selected cells as tab-separated text (what spreadsheets paste).</summary>
    public string SelectionText(bool withHeader)
    {
        if (_src is null) return "";
        var (r0, c0, r1, c1) = Range();
        if (r0 < 0) return "";
        var sb = new StringBuilder();
        if (withHeader)
        {
            for (int c = c0; c <= c1; c++) sb.Append(c > c0 ? "\t" : "").Append(_src.ColumnName(c));
            sb.Append("\r\n");
        }
        for (int r = r0; r <= r1; r++)
        {
            for (int c = c0; c <= c1; c++)
            {
                var s = ValueText.Full(_src.Value(r, c)) ?? "";
                if (s.Contains('\t') || s.Contains('\n') || s.Contains('"')) s = "\"" + s.Replace("\"", "\"\"") + "\"";
                sb.Append(c > c0 ? "\t" : "").Append(s);
            }
            if (r < r1 || r0 != r1 || c0 != c1) sb.Append("\r\n");
        }
        return r0 == r1 && c0 == c1 && !withHeader ? sb.ToString().TrimEnd('\r', '\n') : sb.ToString();
    }

    public async Task CopyAsync(bool withHeader)
    {
        var text = SelectionText(withHeader);
        if (text.Length == 0 || TopLevel.GetTopLevel(this)?.Clipboard is not { } cb) return;
        await cb.SetTextAsync(text);
        var (r0, c0, r1, c1) = Range();
        Notice?.Invoke(Core.Loc.F("grid.copied", (r1 - r0 + 1) * (c1 - c0 + 1)));
    }

    // ------------------------------------------------------------------ editing

    private Rect CellRect(int row, int col) =>
        new(DataLeft + ColX(col) - _x, DataTop + row * RowH - _y, _widths[col], RowH);

    /// <summary>Edits the current cell; <paramref name="initial"/> replaces its text (typing on a cell).</summary>
    public bool BeginEdit(string? initial = null)
    {
        if (_src is not { CanEdit: true } || _caretRow < 0 || _caretRow >= Rows || _caretCol >= Cols) return false;
        if (_src.State(_caretRow, _caretCol) == CellState.Deleted) return false;
        ScrollIntoView(_caretRow, _caretCol);
        _editRow = _caretRow;
        _editCol = _caretCol;
        var current = ValueText.Full(_src.Value(_editRow, _editCol));
        _editor.Text = initial ?? current ?? "";
        _editor.Watermark = current is null && initial is null ? ValueText.Null : null;
        _editor.IsVisible = true;
        PlaceEditor();
        _editor.Focus();
        if (initial is null) _editor.SelectAll();
        else _editor.CaretIndex = _editor.Text.Length;
        return true;
    }

    private void PlaceEditor()
    {
        if (!_editor.IsVisible || _editRow < 0 || _editCol >= Cols) return;
        var r = CellRect(_editRow, _editCol);
        _editor.Arrange(new Rect(r.X, r.Y, Math.Max(r.Width, 120), r.Height + 2));
    }

    private void OnEditorKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                CommitEdit();
                Select(_caretRow + 1, _caretCol, false);
                Focus();
                e.Handled = true;
                break;
            case Key.Tab:
                CommitEdit();
                Select(_caretRow, _caretCol + (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1), false);
                Focus();
                e.Handled = true;
                break;
            case Key.Escape:
                CancelEdit();
                Focus();
                e.Handled = true;
                break;
        }
    }

    private void CommitEdit()
    {
        if (!_editor.IsVisible || _src is null) return;
        int row = _editRow, col = _editCol;
        var text = _editor.Text ?? "";
        _editor.IsVisible = false;
        _editRow = -1;
        var current = ValueText.Full(_src.Value(row, col));
        // An untouched NULL stays NULL (the box shows it as a watermark).
        if (current is null && text.Length == 0) return;
        if (text == current) return;
        if (_src.SetValue(row, col, text) is { } error) Notice?.Invoke(error);
        InvalidateVisual();
    }

    private void CancelEdit()
    {
        _editor.IsVisible = false;
        _editRow = -1;
    }

    /// <summary>Sets the selected cells to NULL (menu "Poner NULL").</summary>
    public void SetSelectionNull()
    {
        if (_src is not { CanEdit: true }) return;
        var (r0, c0, r1, c1) = Range();
        for (int r = r0; r >= 0 && r <= r1; r++)
            for (int c = c0; c <= c1; c++)
                if (_src.SetValue(r, c, null) is { } error) { Notice?.Invoke(error); return; }
        InvalidateVisual();
    }

    public void ScrollToEnd()
    {
        if (Rows == 0) return;
        Select(Rows - 1, 0, false);
    }
}
