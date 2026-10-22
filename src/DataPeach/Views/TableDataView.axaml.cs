using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DataPeach.ViewModels;

namespace DataPeach.Views;

public partial class TableDataView : UserControl
{
    public TableDataView()
    {
        InitializeComponent();
        foreach (var box in new[] { WhereBox, OrderBox })
        {
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && Vm is { } vm) { vm.LoadCommand.Execute(null); e.Handled = true; }
            };
        }
        Grid.MenuRequested += g =>
        {
            if (Vm is { Result: { } r } vm && Owner is { } conn)
                MenuPresenter.Show(g, GridMenus.ForGrid(g, conn, r, vm.Table.Name, vm));
        };
        Grid.Notice += text => { if (Owner is { } conn) conn.Notice = text; };
        ExportButton.Click += (_, _) =>
        {
            if (Vm is { Result: { HasGrid: true } r } vm && Owner is { } conn)
                MenuPresenter.Show(ExportButton, GridMenus.Export(conn, r, vm.Table.Name));
        };
        // Row editing keys, before the grid sees them.
        Grid.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (Vm is not { Result: { CanEdit: true } r }) return;
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (e.Key == Key.Insert && e.KeyModifiers.HasFlag(KeyModifiers.Alt)) { r.AddRow(); Grid.ScrollToEnd(); }
            else if (e.Key == Key.Delete && ctrl) r.DeleteRows(Grid.SelectedRows().ToList());
            else if (e.Key == Key.N && ctrl && shift) Grid.SetSelectionNull();
            else return;
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    private TableDataViewModel? Vm => DataContext as TableDataViewModel;
    private ConnectionViewModel? Owner => this.FindAncestorOfType<ConnectionView>()?.DataContext as ConnectionViewModel;
}
