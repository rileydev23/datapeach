using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using DataPeach.ViewModels;

namespace DataPeach.Views;

/// <summary>Renders <see cref="MenuAction"/> lists produced by view models as context menus.</summary>
public static class MenuPresenter
{
    /// <summary>
    /// Wires right-click on the items of <paramref name="list"/>: the clicked item is selected and the
    /// menu returned by <paramref name="menuFor"/> for its data context is shown.
    /// </summary>
    public static void Attach<T>(ListBox list, Func<T, IReadOnlyList<MenuAction>> menuFor, bool select = true) where T : class
    {
        list.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            if (e.Source is not Control c || c.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not T item) return;
            if (select) list.SelectedItem = item;
            Show(list, menuFor(item));
        };
    }

    public static void Show(Control target, IReadOnlyList<MenuAction> actions)
    {
        if (actions.Count == 0) return;
        new ContextMenu { ItemsSource = Build(actions) }.Open(target);
    }

    private static List<Control> Build(IReadOnlyList<MenuAction> actions)
    {
        var items = new List<Control>();
        foreach (var a in actions)
        {
            if (a.IsSeparator) { items.Add(new Separator()); continue; }
            var mi = new MenuItem { Header = a.Header };
            if (a.Gesture is not null) mi.InputGesture = KeyGesture.Parse(a.Gesture);
            if (a.Children is { } children) mi.ItemsSource = Build(children);
            else
            {
                var run = a.Run!;
                mi.Click += async (_, _) => await run();
            }
            items.Add(mi);
        }
        return items;
    }
}
