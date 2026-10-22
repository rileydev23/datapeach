using System.Windows.Input;
using Avalonia.Media;
using DataPeach.Controls;
using DataPeach.Core;

namespace DataPeach.ViewModels;

/// <summary>A row of the connection list: a group header or a connection.</summary>
public abstract class HomeRow;

public sealed class GroupHeaderRow : HomeRow
{
    /// <summary>Null for the "Sin grupo" section.</summary>
    public required ConnectionGroup? Group { get; init; }
    public required int Count { get; init; }
    public required bool IsCollapsed { get; init; }
    public required ICommand ToggleCommand { get; init; }
    public required ICommand OpenAllCommand { get; init; }

    public string Name => Group?.Name ?? Loc.T("home.ungrouped");
    public string Chevron => IsCollapsed ? "▸" : "▾";
    public string CountText => Loc.Plural("common.connections", Count);
    public bool CanOpenAll => Count > 0;
}

public sealed class ConnectionRow : HomeRow
{
    public required ConnectionEntry Entry { get; init; }
    /// <summary>Connections inside a group are indented under its header.</summary>
    public required bool Indented { get; init; }
    public Avalonia.Thickness Pad => new(Indented ? 26 : 10, 8, 10, 8);

    public string Engine => DbKinds.Name(Entry.Kind);
    public IBrush EngineBrush => Palette.FromHex(DbKinds.Color(Entry.Kind));
    public bool IsProduction => Entry.Mode == ConnectionMode.Production;
    public bool IsReadOnly => Entry.Mode == ConnectionMode.ReadOnly;
}
