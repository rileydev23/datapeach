using DataPeach.Controls;
using DataPeach.Core;
using DataPeach.Core.Data;
using DataPeach.ViewModels;

namespace DataPeach.Views;

/// <summary>Context and export menus of a result grid, shared by consoles and table tabs.</summary>
public static class GridMenus
{
    private static readonly ExportFormat[] Formats =
        { ExportFormat.Csv, ExportFormat.Tsv, ExportFormat.Json, ExportFormat.Markdown, ExportFormat.Insert, ExportFormat.TypeScript };

    private static string Name(ExportFormat f) => Loc.T("export." + f.ToString().ToLowerInvariant());

    /// <summary>"Copiar como" and "Guardar como" for the whole result.</summary>
    public static IReadOnlyList<MenuAction> Export(ConnectionViewModel conn, ResultViewModel r, string name) => new List<MenuAction>
    {
        MenuAction.Submenu(Loc.T("grid.copyAllAs"), Formats.Select(f => new MenuAction(Name(f), () => conn.ExportAsync(r, f, null, null, name))).ToList()),
        MenuAction.Submenu(Loc.T("grid.saveAs"), Formats.Select(f => new MenuAction(Name(f) + "...", () => conn.ExportAsync(r, f, null, null, name, toFile: true))).ToList()),
    };

    public static IReadOnlyList<MenuAction> ForGrid(ResultGrid grid, ConnectionViewModel conn, ResultViewModel r, string name, TableDataViewModel? table)
    {
        var rows = grid.SelectedRows().ToList();
        var cols = grid.SelectedColumns();
        var items = new List<MenuAction>
        {
            new(Loc.T("grid.copy"), () => grid.CopyAsync(false), "Ctrl+C"),
            new(Loc.T("grid.copyHeaders"), () => grid.CopyAsync(true), "Ctrl+Shift+C"),
            MenuAction.Submenu(Loc.T("grid.copyAs"), Formats.Select(f => new MenuAction(Name(f), () => conn.ExportAsync(r, f, rows, cols, name))).ToList()),
            MenuAction.Separator,
        };
        items.AddRange(Export(conn, r, name));
        if (table is not null && r.CanEdit)
        {
            items.Add(MenuAction.Separator);
            items.Add(MenuAction.Sync(Loc.T("grid.edit"), () => grid.BeginEdit(), "F2"));
            items.Add(MenuAction.Sync(Loc.T("grid.setNull"), grid.SetSelectionNull, "Ctrl+Shift+N"));
            items.Add(MenuAction.Sync(Loc.T("grid.addRow"), () => { r.AddRow(); grid.ScrollToEnd(); }, "Alt+Insert"));
            items.Add(MenuAction.Sync(Loc.Plural("grid.deleteRows", rows.Count), () => r.DeleteRows(rows), "Ctrl+Delete"));
        }
        return MenuList.Clean(items);
    }
}
