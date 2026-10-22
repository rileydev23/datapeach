using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using DataPeach.Core;
using DataPeach.Core.Sql;

namespace DataPeach.Controls;

/// <summary>
/// SQL coloring for the console editor with the "Peach Dark" colors (docs/DISENO.md 3.8). The whole document is
/// tokenized once per change (a few ms even for long scripts) and every visible line is painted from that list, so
/// strings and comments that span lines are always right. No TextMate: one small lexer we own (Core/Sql).
/// </summary>
public sealed class SqlColorizer : DocumentColorizingTransformer
{
    private static readonly IBrush Keyword = Palette.FromHex("#d96a9b");
    private static readonly IBrush Str = Palette.FromHex("#8fcf94");
    private static readonly IBrush Number = Palette.FromHex("#e0a24a");
    private static readonly IBrush Comment = Palette.FromHex("#6f737a");
    private static readonly IBrush Operator = Palette.FromHex("#a9acb2");
    private static readonly IBrush Punct = Palette.FromHex("#8a8d93");
    private static readonly IBrush Quoted = Palette.FromHex("#a8c8e8");
    private static readonly IBrush Param = Palette.FromHex("#c8c84a");
    private static readonly IBrush Function = Palette.FromHex("#7db4ea");

    private List<SqlToken> _tokens = new();
    private string _text = "";
    public DbKind Dialect { get; set; } = DbKind.Postgres;

    public void Update(string text)
    {
        _text = text;
        _tokens = SqlTokenizer.Tokenize(text, Dialect);
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        int from = line.Offset, to = line.EndOffset;
        if (_tokens.Count == 0 || to > _text.Length) return;
        // First token that ends after the line starts (binary search).
        int lo = 0, hi = _tokens.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_tokens[mid].End <= from) lo = mid + 1; else hi = mid;
        }
        for (int i = lo; i < _tokens.Count && _tokens[i].Start < to; i++)
        {
            var t = _tokens[i];
            IBrush? brush = t.Kind switch
            {
                SqlTokenKind.Keyword => Keyword,
                SqlTokenKind.String => Str,
                SqlTokenKind.Number => Number,
                SqlTokenKind.Comment => Comment,
                SqlTokenKind.Operator => Operator,
                SqlTokenKind.Punctuation => Punct,
                SqlTokenKind.QuotedIdentifier => Quoted,
                SqlTokenKind.Parameter => Param,
                SqlTokenKind.Identifier when NextIsParen(i) => Function,
                _ => null,
            };
            if (brush is null) continue;
            int s = Math.Max(t.Start, from), e = Math.Min(t.End, to);
            if (e <= s) continue;
            bool italic = t.Kind == SqlTokenKind.Comment;
            ChangeLinePart(s, e, el =>
            {
                el.TextRunProperties.SetForegroundBrush(brush);
                if (italic)
                {
                    var tf = el.TextRunProperties.Typeface;
                    el.TextRunProperties.SetTypeface(new Typeface(tf.FontFamily, FontStyle.Italic, tf.Weight));
                }
            });
        }
    }

    private bool NextIsParen(int i)
    {
        for (int j = i + 1; j < _tokens.Count; j++)
        {
            var t = _tokens[j];
            if (t.Kind == SqlTokenKind.Whitespace) continue;
            return t.Kind == SqlTokenKind.Punctuation && _text[t.Start] == '(';
        }
        return false;
    }
}

/// <summary>One entry of the completion list: kind letter in its color, name, and type or table in dim.</summary>
public sealed class SqlCompletionData(CompletionItem item) : ICompletionData
{
    public CompletionItem Item { get; } = item;
    public Avalonia.Media.IImage? Image => null;
    public string Text => Item.Label;
    public object Content => Build();
    public object? Description => Item.Detail;
    public double Priority => 0;

    private object Build()
    {
        var (glyph, color) = Item.Kind switch
        {
            CompletionKind.Column => ("c", "#4a90d9"),
            CompletionKind.Table => ("T", "#4ac7c0"),
            CompletionKind.View => ("V", "#4ac7c0"),
            CompletionKind.Schema => ("S", "#9a7ae0"),
            CompletionKind.Function => ("ƒ", "#7db4ea"),
            _ => ("k", "#d96a9b"),
        };
        var row = new Avalonia.Controls.StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new Avalonia.Controls.TextBlock { Text = glyph, Width = 12, FontWeight = FontWeight.Bold, Foreground = Palette.FromHex(color) });
        row.Children.Add(new Avalonia.Controls.TextBlock { Text = Item.Label });
        if (Item.Detail is { } d) row.Children.Add(new Avalonia.Controls.TextBlock { Text = d, Foreground = Palette.Dim, FontSize = 11, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        return row;
    }

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
        textArea.Document.Replace(completionSegment, Item.Insert);
}
