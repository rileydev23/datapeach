using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using DataPeach.Core;
using DataPeach.ViewModels;

namespace DataPeach.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        ConnectionList.DoubleTapped += (_, e) =>
        {
            if (Vm is { } vm && RowAt(e.Source) is ConnectionRow r) vm.OpenCommand.Execute(r.Entry);
        };
        ConnectionList.KeyDown += (_, e) =>
        {
            if (Vm is not { } vm) return;
            if (e.Key == Key.Enter)
            {
                if (ConnectionList.SelectedItem is GroupHeaderRow h) h.ToggleCommand.Execute(null);
                else vm.OpenCommand.Execute(null);
            }
            else if (e.Key == Key.Delete && SelectedConnections() is { Count: > 0 } connections) _ = vm.RemoveManyAsync(connections);
        };
        ConnectionList.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            if (Vm is not { } vm) return;
            switch (RowAt(e.Source))
            {
                case GroupHeaderRow h:
                    MenuPresenter.Show(ConnectionList, vm.MenuForGroup(h));
                    break;
                case ConnectionRow r:
                    // Right-click outside the current selection acts on that row only.
                    if (!ConnectionList.Selection.SelectedItems.Contains(r)) ConnectionList.SelectedItem = r;
                    MenuPresenter.Show(ConnectionList, vm.MenuForConnections(SelectedConnections()));
                    break;
            }
        };
        AttachedToVisualTree += (_, _) => FilterBox.Focus();
    }

    private HomeViewModel? Vm => DataContext as HomeViewModel;

    private static HomeRow? RowAt(object? source)
        => (source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as HomeRow;

    private List<ConnectionEntry> SelectedConnections()
        => ConnectionList.Selection.SelectedItems.OfType<ConnectionRow>().Select(r => r.Entry).ToList();
}
