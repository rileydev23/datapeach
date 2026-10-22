using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DataPeach.Core;
using DataPeach.Core.Db;
using DataPeach.Core.Diagram;

namespace DataPeach.Controls;

/// <summary>
/// What a diagram tab shows, kept apart from the canvas because the view is reused between tabs: the graph, where
/// each table is (filled by the canvas the first time) and the zoom and scroll it was left at.
/// </summary>
public sealed class DiagramSource(DiagramGraph graph)
{
    public DiagramGraph Graph { get; } = graph;
    /// <summary>Top-left corner of each table, in diagram units.</summary>
    public Dictionary<DbTable, Point> Positions { get; } = new();
    public double Zoom { get; set; } = 1;
    /// <summary>Where the diagram's origin is on screen: screen = diagram × zoom + offset.</summary>
    public Vector Offset { get; set; }
    public DbTable? Selected { get; set; }
    /// <summary>Whether it has been fitted to the view once.</summary>
    public bool Fitted { get; set; }
}

/// <summary>
/// The diagram, drawn by hand: one box per table with its columns (primary key amber, foreign key teal) and a curve
/// per foreign key, from the referencing column to the referenced one. Drag a table to move it, the background to
/// pan; Ctrl+wheel zooms. Colors follow docs/DISENO.md.
/// </summary>
public sealed class DiagramCanvas : Control
{
    public static readonly StyledProperty<DiagramSource?> SourceProperty =
        AvaloniaProperty.Register<DiagramCanvas, DiagramSource?>(nameof(Source));

    public DiagramSource? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>Double click on a table.</summary>
    public event Action<DbTable>? TableActivated;
    /// <summary>Right click on a table: the owner shows its menu.</summary>
    public event Action<DbTable>? TableMenuRequested;
    /// <summary>A table was selected (null: the background was clicked).</summary>
    public event Action<DbTable?>? SelectionChanged;

    private const double HeaderH = 26, RowH = 19, PadX = 10, MinW = 150, MaxW = 380, FontSz = 12, MinZoom = 0.08, MaxZoom = 2.5;
    /// <summary>Below it the columns are not drawn (the boxes and names still are); the first view stays above.</summary>
    private const double DetailZoom = 0.4, ReadableZoom = 0.6;
    /// <summary>Columns a box lists before "… N more".</summary>
    private const int MaxRows = 40;
    private static readonly Typeface Mono = new("Cascadia Mono, Consolas, Courier New, DejaVu Sans Mono, Liberation Mono");
    private static readonly Typeface Title = new("Segoe UI, Inter, DejaVu Sans", FontStyle.Normal, FontWeight.SemiBold);
    private static readonly IPen BoxPen = new Pen(Palette.Border, 1), SelectedPen = new Pen(Palette.Accent, 2);
    private static readonly IPen EdgePen = new Pen(Palette.Gutter, 1.3), HotEdgePen = new Pen(Palette.Accent, 1.8);

    private sealed class Box
    {
        public required DbTable Table;
        public double W, H;
        public required FormattedText Name;
        public readonly List<(FormattedText Name, FormattedText Type)> Rows = new();
        public FormattedText? More;
    }

    private DiagramSource? _src;
    private readonly Dictionary<DbTable, Box> _boxes = new();
    /// <summary>Drawing order: the last one is on top (the one moved last).</summary>
    private readonly List<Box> _order = new();

    private enum DragKind { None, Pan, Move }
    private DragKind _drag;
    private Point _dragStart, _startPos;
    private Vector _startOffset;
    private Box? _dragBox;

    public DiagramCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SourceProperty) return;
        _src = Source;
        _boxes.Clear();
        _order.Clear();
        _drag = DragKind.None;
        if (_src is not null) Prepare(_src);
        InvalidateVisual();
    }

    private static FormattedText Text(string s, IBrush brush, Typeface face) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, FontSz, brush);

    /// <summary>Measures every box and, the first time, lays them out.</summary>
    private void Prepare(DiagramSource src)
    {
        var g = src.Graph;
        bool qualified = g.Tables.Select(t => t.Schema).Distinct().Count() > 1;
        var fkColumns = g.Edges.SelectMany(e => e.Key.Columns.Select(c => (e.From, c))).ToHashSet();
        foreach (var t in g.Tables)
        {
            var box = new Box { Table = t, Name = Text(qualified ? t.Display : t.Name, Palette.Text, Title) };
            double nameW = 0, typeW = 0;
            foreach (var c in t.Columns.Take(MaxRows))
            {
                var brush = c.IsPrimaryKey ? Palette.Amber : fkColumns.Contains((t, c.Name)) ? Palette.Teal : Palette.Text;
                var row = (Text(c.Name, brush, Mono), Text(c.DataType, Palette.Dim, Mono));
                box.Rows.Add(row);
                nameW = Math.Max(nameW, row.Item1.Width);
                typeW = Math.Max(typeW, row.Item2.Width);
            }
            if (t.Columns.Count > MaxRows) box.More = Text(Loc.Plural("diagram.moreColumns", t.Columns.Count - MaxRows), Palette.Dim, Mono);
            box.W = Math.Clamp(Math.Max(box.Name.Width, nameW + 18 + typeW) + PadX * 2, MinW, MaxW);
            box.H = HeaderH + 4 + RowH * (box.Rows.Count + (box.More is null ? 0 : 1)) + 4;
            _boxes[t] = box;
            _order.Add(box);
        }
        if (src.Positions.Count == 0 && g.Tables.Count > 0)
        {
            var index = g.Tables.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i);
            var pos = DiagramLayout.Arrange(g.Tables.Select(t => (_boxes[t].W, _boxes[t].H)).ToList(),
                                            g.Edges.Select(e => (index[e.From], index[e.To])).ToList());
            for (int i = 0; i < pos.Length; i++) src.Positions[g.Tables[i]] = new Point(pos[i].X, pos[i].Y);
        }
    }

    private Rect RectOf(Box b) => new(_src!.Positions.GetValueOrDefault(b.Table), new Size(b.W, b.H));

    /// <summary>Vertical middle of a column's row, or of the header when the box does not list it.</summary>
    private double RowY(Box b, Rect r, string column)
    {
        int i = b.Table.Columns.FindIndex(c => c.Name == column);
        return i < 0 || i >= b.Rows.Count ? r.Y + HeaderH / 2 : r.Y + HeaderH + 4 + RowH * i + RowH / 2;
    }

    // ------------------------------------------------------------------ drawing

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        ctx.FillRectangle(Palette.Code, bounds);
        if (_src is not { } src || _order.Count == 0) return;
        if (!src.Fitted && bounds.Width > 0 && bounds.Height > 0) FitCore(readable: true);

        double z = src.Zoom;
        var view = new Rect(-src.Offset.X / z, -src.Offset.Y / z, bounds.Width / z, bounds.Height / z);
        bool detail = z >= DetailZoom;
        using (ctx.PushTransform(Matrix.CreateScale(z, z) * Matrix.CreateTranslation(src.Offset.X, src.Offset.Y)))
        {
            // Edges first, the selected table's on top of the rest.
            foreach (var hot in new[] { false, true })
                foreach (var e in src.Graph.Edges)
                {
                    bool isHot = src.Selected is { } s && (e.From == s || e.To == s);
                    if (isHot == hot) DrawEdge(ctx, e, isHot ? HotEdgePen : EdgePen, view);
                }
            foreach (var b in _order)
            {
                var r = RectOf(b);
                if (r.Intersects(view)) DrawBox(ctx, b, r, b.Table == src.Selected, detail);
            }
        }
    }

    private void DrawEdge(DrawingContext ctx, DiagramEdge e, IPen pen, Rect view)
    {
        if (!_boxes.TryGetValue(e.From, out var fb) || !_boxes.TryGetValue(e.To, out var tb)) return;
        Rect fr = RectOf(fb), tr = RectOf(tb);
        double fy = RowY(fb, fr, e.Key.Columns.FirstOrDefault() ?? ""), ty = RowY(tb, tr, e.Key.RefColumns.FirstOrDefault() ?? "");
        // Out of the side that faces the other table; both on the right when they share a column (or it is itself).
        Point a, b;
        double ca, cb;
        if (tr.Right <= fr.X) { a = new(fr.X, fy); b = new(tr.Right, ty); ca = -1; cb = 1; }
        else if (fr.Right <= tr.X) { a = new(fr.Right, fy); b = new(tr.X, ty); ca = 1; cb = -1; }
        else { a = new(fr.Right, fy); b = new(tr.Right, ty); ca = cb = 1; }
        if (!new Rect(a, b).Normalize().Inflate(80).Intersects(view)) return;

        // Facing sides: out horizontally halfway; same side: a loop past the further of the two.
        double reach = Math.Max(40, Math.Abs(a.X - b.X) / 2), loop = Math.Max(a.X, b.X) + 50;
        var c1 = ca == cb ? new Point(loop, a.Y) : new Point(a.X + ca * reach, a.Y);
        var c2 = ca == cb ? new Point(loop, b.Y) : new Point(b.X + cb * reach, b.Y);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(a, false);
            g.CubicBezierTo(c1, c2, b);
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geo);
        // The referencing end gets a dot, the referenced one a bar: "many" points at "one".
        ctx.DrawEllipse(pen.Brush, null, a, 2.6, 2.6);
        ctx.DrawLine(pen, new Point(b.X + cb * 7, b.Y - 5), new Point(b.X + cb * 7, b.Y + 5));
    }

    private static void DrawBox(DrawingContext ctx, Box b, Rect r, bool selected, bool detail)
    {
        ctx.DrawRectangle(Palette.Panel, null, r, 4, 4);
        var header = new Rect(r.X, r.Y, r.Width, HeaderH);
        ctx.DrawRectangle(Palette.Panel2, null, header, 4, 4);
        ctx.FillRectangle(Palette.Panel2, new Rect(r.X, r.Y + HeaderH / 2, r.Width, HeaderH / 2));
        ctx.DrawLine(BoxPen, new Point(r.X, r.Y + HeaderH), new Point(r.Right, r.Y + HeaderH));
        using (ctx.PushClip(r.Deflate(new Thickness(PadX - 2, 0))))
        {
            ctx.DrawText(b.Name, new Point(r.X + PadX, r.Y + (HeaderH - b.Name.Height) / 2));
            if (detail)
            {
                double y = r.Y + HeaderH + 4;
                foreach (var (name, type) in b.Rows)
                {
                    ctx.DrawText(type, new Point(r.Right - PadX - type.Width, y + (RowH - type.Height) / 2));
                    ctx.DrawText(name, new Point(r.X + PadX, y + (RowH - name.Height) / 2));
                    y += RowH;
                }
                if (b.More is { } more) ctx.DrawText(more, new Point(r.X + PadX, y + (RowH - more.Height) / 2));
            }
        }
        ctx.DrawRectangle(null, selected ? SelectedPen : BoxPen, r, 4, 4);
    }

    // ------------------------------------------------------------------ view

    /// <summary>Zooms and scrolls so the whole diagram fits (never past 100 %).</summary>
    public void Fit()
    {
        FitCore();
        InvalidateVisual();
    }

    /// <summary>
    /// The fit itself, also used while drawing (where asking for another draw is not allowed). The first time, a
    /// diagram too big to read whole opens at a readable zoom instead: on the selected table, or at its top left.
    /// </summary>
    private void FitCore(bool readable = false)
    {
        if (_src is not { } src || _order.Count == 0 || Bounds.Width <= 0) return;
        var all = _order.Select(RectOf).Aggregate((a, b) => a.Union(b)).Inflate(30);
        double z = Math.Clamp(Math.Min(Bounds.Width / all.Width, Bounds.Height / all.Height), MinZoom, 1);
        src.Fitted = true;
        if (readable && z < ReadableZoom)
        {
            src.Zoom = z = 0.75;
            var at = src.Selected is { } s && _boxes.TryGetValue(s, out var b) ? RectOf(b).Center : all.TopLeft;
            src.Offset = src.Selected is null ? new Vector(-at.X * z, -at.Y * z) : new Vector(Bounds.Width / 2 - at.X * z, Bounds.Height / 2 - at.Y * z);
            return;
        }
        src.Zoom = z;
        src.Offset = new Vector((Bounds.Width - all.Width * z) / 2 - all.X * z, (Bounds.Height - all.Height * z) / 2 - all.Y * z);
    }

    private void ZoomAt(Point p, double factor)
    {
        if (_src is not { } src) return;
        double z = Math.Clamp(src.Zoom * factor, MinZoom, MaxZoom);
        src.Offset = (Vector)p - ((Vector)p - src.Offset) * (z / src.Zoom);
        src.Zoom = z;
        InvalidateVisual();
    }

    private Box? BoxAt(Point screen)
    {
        if (_src is not { } src) return null;
        var p = new Point((screen.X - src.Offset.X) / src.Zoom, (screen.Y - src.Offset.Y) / src.Zoom);
        for (int i = _order.Count - 1; i >= 0; i--)
            if (RectOf(_order[i]).Contains(p)) return _order[i];
        return null;
    }

    private void Select(Box? b)
    {
        if (_src is null) return;
        if (_src.Selected != b?.Table) SelectionChanged?.Invoke(b?.Table);
        _src.Selected = b?.Table;
        if (b is not null)
        {
            _order.Remove(b);
            _order.Add(b);
        }
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ input

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_src is not { } src) return;
        Focus();
        var p = e.GetPosition(this);
        var box = BoxAt(p);
        var point = e.GetCurrentPoint(this);
        e.Handled = true;
        if (point.Properties.IsRightButtonPressed)
        {
            Select(box);
            if (box is not null) TableMenuRequested?.Invoke(box.Table);
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2 && box is not null)
        {
            TableActivated?.Invoke(box.Table);
            return;
        }
        Select(box);
        _dragStart = p;
        if (box is not null)
        {
            _drag = DragKind.Move;
            _dragBox = box;
            _startPos = src.Positions.GetValueOrDefault(box.Table);
        }
        else
        {
            _drag = DragKind.Pan;
            _startOffset = src.Offset;
        }
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_src is not { } src || _drag == DragKind.None) return;
        var d = e.GetPosition(this) - _dragStart;
        if (_drag == DragKind.Pan) src.Offset = _startOffset + d;
        else if (_dragBox is { } b) src.Positions[b.Table] = _startPos + d / src.Zoom;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _drag = DragKind.None;
        _dragBox = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_src is not { } src) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) ZoomAt(e.GetPosition(this), Math.Pow(1.15, e.Delta.Y));
        else
        {
            var d = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? new Vector(e.Delta.Y, e.Delta.X) : e.Delta;
            src.Offset += d * 60;
            InvalidateVisual();
        }
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        if (e.Key is Key.D0 or Key.NumPad0) Fit();
        else if (e.Key is Key.OemPlus or Key.Add) ZoomAt(center, 1.2);
        else if (e.Key is Key.OemMinus or Key.Subtract) ZoomAt(center, 1 / 1.2);
        else return;
        e.Handled = true;
    }
}
