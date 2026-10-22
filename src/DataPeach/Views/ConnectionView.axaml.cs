using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using DataPeach.Core;
using DataPeach.ViewModels;

namespace DataPeach.Views;

public partial class ConnectionView : UserControl
{
    public ConnectionView()
    {
        InitializeComponent();
        // A click on the chevron expands; a double click (or Enter) opens the table's data.
        SchemaList.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (Vm is { } vm && e.Source is TextBlock { Name: "Chevron" } tb && tb.DataContext is SchemaRow row)
            {
                vm.Toggle(row);
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        SchemaList.DoubleTapped += (_, e) =>
        {
            if (Vm is { } vm && RowAt(e.Source) is { } row) vm.Activate(row);
        };
        SchemaList.KeyDown += (_, e) =>
        {
            if (Vm is not { } vm || SchemaList.SelectedItem is not SchemaRow row) return;
            if (e.Key == Key.Enter) vm.Activate(row);
            else if (e.Key == Key.Right && row.CanExpand && !row.IsExpanded) vm.Toggle(row);
            else if (e.Key == Key.Left && row.CanExpand && row.IsExpanded) vm.Toggle(row);
            else return;
            e.Handled = true;
        };
        SchemaList.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            if (Vm is not { } vm || RowAt(e.Source) is not { } row) return;
            if (row.Kind == SchemaRowKind.Database)
            {
                SchemaList.SelectedItem = row;
                MenuPresenter.Show(SchemaList, DatabaseMenu(vm, row.Text));
                return;
            }
            if (row.Routine is { } r)
            {
                SchemaList.SelectedItem = row;
                MenuPresenter.Show(SchemaList, RoutineMenu(vm, r));
                return;
            }
            if (row.Table is not { } t) return;
            SchemaList.SelectedItem = row;
            MenuPresenter.Show(SchemaList, TableMenu(vm, t));
        };
        TreeFilterBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { TreeFilterBox.Text = ""; e.Handled = true; }
            else if (e.Key == Key.Down && SchemaList.ItemCount > 0) { SchemaList.SelectedIndex = 0; SchemaList.Focus(); e.Handled = true; }
            else if (e.Key == Key.Enter && Vm is { } vm && vm.SchemaRows.FirstOrDefault(r => r.Table is not null) is { } first) { vm.Activate(first); e.Handled = true; }
        };
        DocTabs.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            if (Vm is not { } vm || (e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not TabViewModel doc) return;
            var items = new List<MenuAction>();
            if (doc is ConsoleViewModel c)
            {
                items.Add(new MenuAction(Loc.T("cv.console.rename"), () => vm.RenameConsoleAsync(c)));
                items.Add(MenuAction.Sync(Loc.T("cv.console.showFile"), () => ShowFile(c.FilePath)));
                items.Add(MenuAction.Separator);
            }
            items.Add(MenuAction.Sync(Loc.T("cv.closeOthers"), () => { foreach (var d in vm.Docs.Where(d => d != doc).ToList()) vm.CloseDoc(d); }));
            items.Add(MenuAction.Sync(Loc.T("common.close"), () => vm.CloseDoc(doc)));
            MenuPresenter.Show(DocTabs, items);
        };
        StagingStatus.Click += (_, _) => StagingPanel.BringIntoView();
        KeyBindings.Add(new KeyBinding { Gesture = KeyGesture.Parse("Ctrl+Shift+E"), Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { TreeFilterBox.Focus(); TreeFilterBox.SelectAll(); }) });
    }

    private ConnectionViewModel? Vm => DataContext as ConnectionViewModel;

    private static SchemaRow? RowAt(object? source) =>
        (source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as SchemaRow;

    private static IReadOnlyList<MenuAction> TableMenu(ConnectionViewModel vm, Core.Db.DbTable t) => new List<MenuAction>
    {
        MenuAction.Sync(Loc.T("cv.menu.openData"), () => vm.OpenTable(t), "F4"),
        MenuAction.Sync(Loc.T("cv.menu.select"), () => vm.OpenInConsole(vm.Dialect.SelectTable(t, null, null, 100) + ";", t.Database)),
        MenuAction.Sync(Loc.T("cv.menu.count"), () => vm.OpenInConsole(vm.Dialect.CountTable(t, null) + ";", t.Database)),
        MenuAction.Separator,
        new(Loc.T("cv.menu.copyName"), () => Dialogs.CopyAsync(vm.Dialect.NameOf(t))),
        new(Loc.T("cv.menu.copyColumns"), () => Dialogs.CopyAsync(string.Join(", ", t.Columns.Select(c => vm.Dialect.Name(c.Name))))),
    };

    private static IReadOnlyList<MenuAction> DatabaseMenu(ConnectionViewModel vm, string database) => new List<MenuAction>
    {
        MenuAction.Sync(Loc.T("cv.menu.consoleHere"), () => vm.NewConsoleOn(database)),
        new(Loc.T("cv.menu.refreshDatabase"), () => vm.RefreshDatabaseAsync(database)),
        MenuAction.Separator,
        new(Loc.T("cv.menu.copyName"), () => Dialogs.CopyAsync(vm.Dialect.Name(database))),
    };

    private static IReadOnlyList<MenuAction> RoutineMenu(ConnectionViewModel vm, Core.Db.DbRoutine r) => new List<MenuAction>
    {
        new(Loc.T("cv.menu.run"), () => vm.ExecuteRoutineAsync(r)),
        new(Loc.T("cv.menu.code"), () => vm.OpenRoutineSourceAsync(r), "Enter"),
        MenuAction.Sync(Loc.T("cv.menu.drop"), () => vm.ScriptDropRoutine(r)),
        MenuAction.Separator,
        new(Loc.T("cv.menu.copyName"), () => Dialogs.CopyAsync(vm.Dialect.NameOf(r))),
    };

    private static void ShowFile(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.GetDirectoryName(path)!) { UseShellExecute = true });
        }
        catch { /* nothing to show */ }
    }
}
