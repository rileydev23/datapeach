using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DataPeach.Controls;
using DataPeach.Core;
using DataPeach.Core.Db;

namespace DataPeach.ViewModels;

public enum SchemaRowKind { Database, Folder, Schema, Table, View, Column, Procedure, Function, Message }

/// <summary>
/// A row of the schema tree, drawn as a flat virtualized list (fast with thousands of tables): database → schema →
/// folders (tables, views, procedures, functions) → objects → columns. Letters and colors follow docs/DISENO.md
/// ("Árbol de estructura").
/// </summary>
public sealed partial class SchemaRow : ObservableObject
{
    public required SchemaRowKind Kind { get; init; }
    public required string Text { get; init; }
    public string? Detail { get; init; }
    public int Level { get; init; }
    public string? Schema { get; init; }
    public string? Database { get; init; }
    /// <summary>What remembers whether an expandable row is open ("d:ventas", "f:ventas/dbo/T"…).</summary>
    public string? Key { get; init; }
    /// <summary>The database the connection lands in, drawn in SemiBold.</summary>
    public bool IsCurrent { get; init; }
    public DbTable? Table { get; init; }
    public DbColumn? Column { get; init; }
    public DbRoutine? Routine { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chevron))]
    private bool _isExpanded;

    public bool CanExpand => Kind is SchemaRowKind.Database or SchemaRowKind.Folder or SchemaRowKind.Schema or SchemaRowKind.Table or SchemaRowKind.View;
    public string Chevron => CanExpand ? (IsExpanded ? "▾" : "▸") : "";
    public Thickness Indent => new(4 + Level * 14, 0, 0, 0);

    public string Glyph => Kind switch
    {
        SchemaRowKind.Database => "D",
        SchemaRowKind.Folder => "▤",
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
        SchemaRowKind.Database => Palette.Text,
        SchemaRowKind.Folder => Palette.Dim,
        SchemaRowKind.Schema => Palette.Purple,
        SchemaRowKind.Table or SchemaRowKind.View => Palette.Teal,
        SchemaRowKind.Procedure or SchemaRowKind.Function => Palette.Pink,
        SchemaRowKind.Column => Column?.IsPrimaryKey == true ? Palette.Amber : Palette.Blue,
        _ => Palette.Dim,
    };

    public bool IsMessage => Kind == SchemaRowKind.Message;
    public FontStyle Style => Kind == SchemaRowKind.View || IsMessage ? FontStyle.Italic : FontStyle.Normal;
    public FontWeight Weight => IsCurrent ? FontWeight.SemiBold : FontWeight.Normal;
    public string Tip => Kind == SchemaRowKind.Database ? (IsCurrent ? Loc.F("tree.database.current", Text) : Text)
        : Routine is { } r ? r.FullName + r.Signature + (r.ReturnType is null ? "" : " → " + r.ReturnType)
        : Table is { } t && Kind != SchemaRowKind.Column ? t.FullName : Column is { } c ? $"{c.Name} {c.DataType}{(c.Nullable ? "" : " NOT NULL")}{(c.Default is null ? "" : " DEFAULT " + c.Default)}" : Text;
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
    public string TableName => Statement.Change.Table.FullName;
}

/// <summary>A line of the detail panel: label in dim, value on the right.</summary>
public sealed record DetailLine(string Label, string Value, IBrush? ValueBrush = null)
{
    public IBrush Brush => ValueBrush ?? Palette.Text;
}
