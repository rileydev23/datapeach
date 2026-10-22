using DataPeach.Core;
using DataPeach.Core.Drivers;
using DataPeach.Core.History;

namespace DataPeach.Services;

/// <summary>What every screen shares: saved state, passwords, drivers and the delete history, all under the settings folder.</summary>
public sealed class AppServices
{
    public Store Store { get; }
    public Secrets Secrets { get; }
    public DriverManager Drivers { get; }
    public DeleteHistory History { get; }
    public string DataFolder => Store.DataFolder;

    public AppServices(string folder)
    {
        Store = new Store(folder);
        Secrets = new Secrets(folder);
        Drivers = new DriverManager(Path.Combine(folder, "drivers"));
        History = new DeleteHistory(folder);
        // Old deletes leave at start-up, so the folder never grows past the retention the user chose.
        try { History.Purge(Store.State.DeleteHistoryDays, DateTimeOffset.Now); } catch { /* next time */ }
    }

    /// <summary>Where a connection's consoles live: its project's .peach/queries, or the settings folder.</summary>
    public string QueriesFolder(ConnectionEntry e) =>
        !string.IsNullOrWhiteSpace(e.QueriesFolder)
            ? Path.Combine(e.QueriesFolder, ".peach", "queries")
            : Path.Combine(DataFolder, "queries", e.Id);
}
