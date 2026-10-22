using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace DataPeach.Views;

/// <summary>
/// Behaviour of every <c>ListBox.tabstrip</c> (see Styles.axaml): the mouse wheel scrolls the tabs sideways,
/// since a horizontal strip has nothing to scroll vertically. Installed once for the whole app.
/// </summary>
public static class TabStrip
{
    public static void Install() =>
        InputElement.PointerWheelChangedEvent.AddClassHandler<ListBox>(OnWheel, RoutingStrategies.Tunnel);

    private static void OnWheel(ListBox list, PointerWheelEventArgs e)
    {
        if (!list.Classes.Contains("tabstrip") || list.FindDescendantOfType<ScrollViewer>() is not { } sv) return;
        if (sv.Extent.Width <= sv.Viewport.Width) return;
        double delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
        sv.Offset = sv.Offset.WithX(Math.Clamp(sv.Offset.X - delta * 60, 0, sv.Extent.Width - sv.Viewport.Width));
        e.Handled = true;
    }
}
