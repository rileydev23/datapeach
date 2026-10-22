namespace DataPeach.ViewModels;

/// <summary>
/// A context-menu entry produced by a view model (command pattern): the view only renders it,
/// all behaviour lives in <see cref="Run"/>. Entries with <see cref="Children"/> are submenus.
/// </summary>
public sealed record MenuAction(string Header, Func<Task>? Run = null, string? Gesture = null)
{
    public static readonly MenuAction Separator = new("-");

    public IReadOnlyList<MenuAction>? Children { get; init; }
    public bool IsSeparator => Run is null && Children is null;

    public static MenuAction Sync(string header, Action run, string? gesture = null)
        => new(header, () => { run(); return Task.CompletedTask; }, gesture);

    public static MenuAction Submenu(string header, IReadOnlyList<MenuAction> children)
        => new(header) { Children = children };
}

internal static class MenuList
{
    /// <summary>Drops leading, trailing and doubled separators left over by conditional entries.</summary>
    public static List<MenuAction> Clean(IEnumerable<MenuAction> items)
    {
        var list = new List<MenuAction>();
        foreach (var a in items)
        {
            if (a.IsSeparator && (list.Count == 0 || list[^1].IsSeparator)) continue;
            list.Add(a);
        }
        while (list.Count > 0 && list[^1].IsSeparator) list.RemoveAt(list.Count - 1);
        return list;
    }
}
