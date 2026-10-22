using DataPeach.Core;

namespace DataPeach.Services;

/// <summary>XAML: <c>Text="{l:L main.home}"</c>.</summary>
public sealed class LExtension : Avalonia.Markup.Xaml.MarkupExtension
{
    public LExtension(string key) => Key = key;
    public string Key { get; set; }
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
