using Avalonia.Controls;
using DataPeach.Core;
using DataPeach.ViewModels;

namespace DataPeach.Views;

public partial class DiagramView : UserControl
{
    public DiagramView()
    {
        InitializeComponent();
        FitButton.Click += (_, _) => Canvas.Fit();
        Canvas.TableActivated += t => Vm?.OpenTable(t);
        Canvas.SelectionChanged += t => { if (t is not null) Vm?.ShowDetails(t); };
        Canvas.TableMenuRequested += t =>
        {
            if (Vm is not { } vm) return;
            MenuPresenter.Show(Canvas, new List<MenuAction>
            {
                MenuAction.Sync(Loc.T("cv.menu.openData"), () => vm.OpenTable(t)),
                MenuAction.Sync(Loc.T("diagram.focus"), () => vm.Refocus(t)),
                MenuAction.Separator,
                new(Loc.T("cv.menu.copyName"), () => Dialogs.CopyAsync(t.FullName)),
            });
        };
    }

    private DiagramViewModel? Vm => DataContext as DiagramViewModel;
}
