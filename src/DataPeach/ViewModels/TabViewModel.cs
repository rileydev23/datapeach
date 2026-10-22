using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DataPeach.ViewModels;

/// <summary>A tab in the window strip (Conexiones / one per connection) or in a connection's document strip.</summary>
public abstract partial class TabViewModel : ObservableObject
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _toolTip = "";
    public virtual bool Closable => true;
    public ICommand? CloseCommand { get; set; }
}
