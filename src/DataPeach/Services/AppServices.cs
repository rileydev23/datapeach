using DataPeach.Core;
using DataPeach.Core.Drivers;

namespace DataPeach.Services;

/// <summary>What every screen shares: saved state, passwords and drivers, all under the settings folder.</summary>
public sealed class AppServices
{
    public Store Store { get; }
    public Secrets Secrets { get; }
    public DriverManager Drivers { get; }
    public string DataFolder => Store.DataFolder;

    public AppServices(string folder)
    {
        Store = new Store(folder);
        Secrets = new Secrets(folder);
        Drivers = new DriverManager(Path.Combine(folder, "drivers"));
    }

    /// <summary>Where a connection's consoles live: its project's .peach/queries, or the settings folder.</summary>
    public string QueriesFolder(ConnectionEntry e) =>
        !string.IsNullOrWhiteSpace(e.QueriesFolder)
            ? Path.Combine(e.QueriesFolder, ".peach", "queries")
            : Path.Combine(DataFolder, "queries", e.Id);
}
