namespace DataPeach.Core;

/// <summary>One section of the connection list: a group (or the ungrouped connections, when <see cref="Group"/> is null).</summary>
public sealed record ConnectionSection(ConnectionGroup? Group, IReadOnlyList<ConnectionEntry> Connections);

/// <summary>Group operations over <see cref="AppState"/>. Pure: the caller decides when to save.</summary>
public static class ConnectionGrouping
{
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    public static ConnectionGroup? Find(AppState s, string? name)
        => name is null ? null : s.Groups.FirstOrDefault(g => Cmp.Equals(g.Name, name));

    /// <summary>The group with that name (case-insensitive), created at the end if missing.</summary>
    public static ConnectionGroup Ensure(AppState s, string name)
    {
        name = name.Trim();
        if (Find(s, name) is { } g) return g;
        g = new ConnectionGroup { Name = name };
        s.Groups.Add(g);
        return g;
    }

    /// <summary>"name", or "name (2)", "name (3)"… when taken.</summary>
    public static string UniqueName(AppState s, string baseName)
    {
        baseName = string.IsNullOrWhiteSpace(baseName) ? Loc.T("home.group.default") : baseName.Trim();
        var name = baseName;
        for (int i = 2; Find(s, name) is not null; i++) name = $"{baseName} ({i})";
        return name;
    }

    public static void Move(AppState s, IEnumerable<ConnectionEntry> connections, ConnectionGroup? group)
    {
        foreach (var r in connections) r.Group = group?.Name;
    }

    /// <summary>Renames a group and its members. False when another group already has that name.</summary>
    public static bool Rename(AppState s, ConnectionGroup group, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0) return false;
        if (Find(s, newName) is { } other && !ReferenceEquals(other, group)) return false;
        foreach (var r in Members(s, group)) r.Group = newName;
        group.Name = newName;
        return true;
    }

    /// <summary>Deletes the group; its connections become ungrouped (or are removed from the list).</summary>
    public static void Delete(AppState s, ConnectionGroup group, bool removeConnections)
    {
        var members = Members(s, group).ToList();
        if (removeConnections)
        {
            foreach (var r in members)
            {
                s.Connections.Remove(r);
                s.OpenTabs.Remove(r.Id);
                s.Sessions.Remove(r.Id);
            }
        }
        else Move(s, members, null);
        s.Groups.Remove(group);
    }

    public static IEnumerable<ConnectionEntry> Members(AppState s, ConnectionGroup? group)
        => group is null
            ? s.Connections.Where(r => Find(s, r.Group) is null)   // unknown group names count as ungrouped
            : s.Connections.Where(r => Cmp.Equals(r.Group, group.Name));

    /// <summary>
    /// Sections in display order: groups as stored, then the ungrouped connections. Connections are sorted by
    /// favorite, then most recently opened. Empty groups are kept unless <paramref name="hideEmpty"/>.
    /// </summary>
    public static List<ConnectionSection> Build(AppState s, Func<ConnectionEntry, bool> include, bool hideEmpty)
    {
        IReadOnlyList<ConnectionEntry> Sorted(IEnumerable<ConnectionEntry> connections) => connections.Where(include)
            .OrderByDescending(r => r.Favorite).ThenByDescending(r => r.LastOpened).ToList();

        var sections = new List<ConnectionSection>();
        foreach (var g in s.Groups)
        {
            var connections = Sorted(Members(s, g));
            if (connections.Count > 0 || !hideEmpty) sections.Add(new ConnectionSection(g, connections));
        }
        var ungrouped = Sorted(Members(s, null));
        if (ungrouped.Count > 0) sections.Add(new ConnectionSection(null, ungrouped));
        return sections;
    }
}
