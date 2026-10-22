using System.Text.Json;
using System.Text.Json.Serialization;
using DataPeach.Core.Drivers;

namespace DataPeach.Core;

/// <summary>Consoles left open in a connection tab, restored with it.</summary>
public sealed class ConnectionSession
{
    /// <summary>File names of the open consoles, in tab order.</summary>
    public List<string> Consoles { get; set; } = new();
    public string? Active { get; set; }
}

public sealed class AppState
{
    public List<ConnectionEntry> Connections { get; set; } = new();
    /// <summary>Groups in display order.</summary>
    public List<ConnectionGroup> Groups { get; set; } = new();
    public bool UngroupedCollapsed { get; set; }
    /// <summary>Ids of the open connection tabs.</summary>
    public List<string> OpenTabs { get; set; } = new();
    public string? ActiveTab { get; set; }
    public Dictionary<string, ConnectionSession> Sessions { get; set; } = new();
    public double Width { get; set; }
    public double Height { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
    public bool Maximized { get; set; }
    public double FontSize { get; set; } = 13;
    /// <summary>Rows a console reads at most per result.</summary>
    public int MaxRows { get; set; } = 50_000;
    /// <summary>Interface language: "en", "es" or null to follow Windows.</summary>
    public string? Language { get; set; }
    /// <summary>Days the delete history keeps each delete (0 = forever).</summary>
    public int DeleteHistoryDays { get; set; } = 30;
}

[JsonSerializable(typeof(AppState))]
[JsonSerializable(typeof(History.DeleteRecord))]
[JsonSerializable(typeof(InstalledDriver))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(string))]
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
internal sealed partial class CoreJson : JsonSerializerContext { }

/// <summary><c>state.json</c> in the settings folder: connections, groups, open tabs and preferences.</summary>
public sealed class Store
{
    private readonly string _file;
    private readonly object _lock = new();

    public AppState State { get; private set; } = new();
    public string DataFolder { get; }

    public Store(string folder)
    {
        DataFolder = folder;
        Directory.CreateDirectory(folder);
        _file = Path.Combine(folder, "state.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_file))
                State = JsonSerializer.Deserialize(File.ReadAllText(_file), CoreJson.Default.AppState) ?? new AppState();
        }
        catch
        {
            State = new AppState();
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                var tmp = _file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(State, CoreJson.Default.AppState));
                File.Move(tmp, _file, true);
            }
            catch { /* best effort */ }
        }
    }

    public ConnectionEntry? Find(string id) => State.Connections.FirstOrDefault(c => c.Id == id);

    /// <summary>The saved connection for a database file, or a new one (not saved until the caller says so).</summary>
    public ConnectionEntry ForFile(string path)
    {
        var full = Path.GetFullPath(path);
        var existing = State.Connections.FirstOrDefault(c => c.IsFile && string.Equals(Path.GetFullPath(c.FilePath), full, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        var e = new ConnectionEntry { Kind = DbKinds.DetectFile(full) ?? DbKind.Sqlite, FilePath = full, Name = Path.GetFileName(full), LastOpened = DateTimeOffset.Now };
        State.Connections.Add(e);
        Save();
        return e;
    }

    public void Add(ConnectionEntry e)
    {
        if (!State.Connections.Contains(e)) State.Connections.Add(e);
        Save();
    }

    public void Remove(ConnectionEntry e)
    {
        State.Connections.Remove(e);
        State.OpenTabs.Remove(e.Id);
        State.Sessions.Remove(e.Id);
        Save();
    }

    public ConnectionSession SessionFor(string id)
    {
        if (!State.Sessions.TryGetValue(id, out var s)) State.Sessions[id] = s = new ConnectionSession();
        return s;
    }

    public void Touch(ConnectionEntry e)
    {
        e.LastOpened = DateTimeOffset.Now;
        Save();
    }
}
