using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DataPeach.Controls;
using DataPeach.Core.Db;

namespace DataPeach.ViewModels;

public enum SchemaRowKind { Schema, Table, View, Column, Procedure, Function, Message }

/// <summary>
/// A row of the schema tree, drawn as a flat virtualized list (fast with thousands of tables): schema → table or
/// view → columns, and its procedures and functions. Letters and colors follow docs/DISENO.md ("Árbol de esquema").
/// </summary>
public sealed partial class SchemaRow : ObservableObject
{
    public required SchemaRowKind Kind { get; init; }
    public required string Text { get; init; }
    public string? Detail { get; init; }
    public int Level { get; init; }
    public string? Schema { get; init; }
    public DbTable? Table { get; init; }
    public DbColumn? Column { get; init; }
    public DbRoutine? Routine { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chevron))]
    private bool _isExpanded;

    public bool CanExpand => Kind is SchemaRowKind.Schema or SchemaRowKind.Table or SchemaRowKind.View;
    public string Chevron => CanExpand ? (IsExpanded ? "▾" : "▸") : "";
    public Thickness Indent => new(4 + Level * 14, 0, 0, 0);

    public string Glyph => Kind switch
    {
        SchemaRowKind.Schema => "S",
        SchemaRowKind.Table => "T",
        SchemaRowKind.View => "V",
        SchemaRowKind.Procedure => "P",
        SchemaRowKind.Function => "F",
        SchemaRowKind.Column => Column?.IsPrimaryKey == true ? "⚿" : "c",
        _ => "",
    };

    public IBrush GlyphBrush => Kind switch
    {
        SchemaRowKind.Schema => Palette.Purple,
        SchemaRowKind.Table or SchemaRowKind.View => Palette.Teal,
        SchemaRowKind.Procedure or SchemaRowKind.Function => Palette.Pink,
        SchemaRowKind.Column => Column?.IsPrimaryKey == true ? Palette.Amber : Palette.Blue,
        _ => Palette.Dim,
    };

    public bool IsMessage => Kind == SchemaRowKind.Message;
    public FontStyle Style => Kind == SchemaRowKind.View || IsMessage ? FontStyle.Italic : FontStyle.Normal;
    public string Tip => Routine is { } r ? r.Display + r.Signature + (r.ReturnType is null ? "" : " → " + r.ReturnType)
        : Table is { } t && Kind != SchemaRowKind.Column ? t.Display : Column is { } c ? $"{c.Name} {c.DataType}{(c.Nullable ? "" : " NOT NULL")}{(c.Default is null ? "" : " DEFAULT " + c.Default)}" : Text;
}

/// <summary>A pending change in the staging panel: its SQL and a button to take it back.</summary>
public sealed class StagedItem
{
    public required Core.Staging.StagedStatement Statement { get; init; }
    public required System.Windows.Input.ICommand RemoveCommand { get; init; }
    public string Sql => Statement.Preview;
    public string Letter => Statement.Change.Kind switch
    {
        Core.Staging.ChangeKind.Insert => "+",
        Core.Staging.ChangeKind.Delete => "−",
        _ => "~",
    };
    public IBrush LetterBrush => Statement.Change.Kind switch
    {
        Core.Staging.ChangeKind.Insert => Palette.Green,
        Core.Staging.ChangeKind.Delete => Palette.Red,
        _ => Palette.Amber,
    };
    public string TableName => Statement.Change.Table.Display;
}

/// <summary>A line of the detail panel: label in dim, value on the right.</summary>
public sealed record DetailLine(string Label, string Value, IBrush? ValueBrush = null)
{
    public IBrush Brush => ValueBrush ?? Palette.Text;
}
