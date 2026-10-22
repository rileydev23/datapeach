using DataPeach.Core;

namespace DataPeach.ViewModels;

/// <summary>Single source for the keyboard shortcuts shown in the help dialog (F1).</summary>
public static class Shortcuts
{
    public static (string Gesture, string Description)[] Global =>
    [
        ("Ctrl+P", Loc.T("keys.ctrlP")),
        ("Ctrl+Shift+O", Loc.T("keys.ctrlShiftO")),
        ("Ctrl+Tab / Ctrl+Shift+Tab", Loc.T("keys.tabs")),
        ("Ctrl+1 … Ctrl+9", Loc.T("keys.tabN")),
        ("Ctrl+W / Ctrl+F4", Loc.T("keys.close")),
        ("F1", Loc.T("keys.about")),
    ];

    public static (string Gesture, string Description)[] Connection =>
    [
        ("Ctrl+Enter", Loc.T("keys.run")),
        ("Ctrl+Shift+Enter", Loc.T("keys.runAll")),
        ("Esc", Loc.T("keys.cancel")),
        ("Ctrl+Space", Loc.T("keys.complete")),
        ("Ctrl+N", Loc.T("keys.newConsole")),
        ("F4", Loc.T("keys.openTable")),
        ("Ctrl+Shift+E", Loc.T("keys.filterTree")),
        ("Ctrl+Shift+K", Loc.T("keys.applyStaging")),
        ("F5", Loc.T("keys.refresh")),
    ];

    public static (string Gesture, string Description)[] Grid =>
    [
        ("F2 / Enter", Loc.T("keys.editCell")),
        ("Ctrl+C / Ctrl+Shift+C", Loc.T("keys.copy")),
        ("Alt+Insert", Loc.T("keys.addRow")),
        ("Ctrl+Delete", Loc.T("keys.deleteRows")),
        ("Ctrl+Shift+N", Loc.T("keys.setNull")),
    ];

    public static (string Title, (string Gesture, string Description)[] Items)[] Groups =>
        new[] { (Loc.T("keys.general"), Global), (Loc.T("keys.connection"), Connection), (Loc.T("keys.grid"), Grid) };
}
