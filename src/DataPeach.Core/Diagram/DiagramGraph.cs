using DataPeach.Core.Db;

namespace DataPeach.Core.Diagram;

/// <summary>A foreign key between two tables of the diagram: <see cref="From"/> holds the key, <see cref="To"/> is referenced.</summary>
public sealed record DiagramEdge(DbForeignKey Key, DbTable From, DbTable To);

/// <summary>The tables a diagram shows and the foreign keys between them.</summary>
public sealed class DiagramGraph
{
    public List<DbTable> Tables { get; } = new();
    public List<DiagramEdge> Edges { get; } = new();

    /// <summary>
    /// The tables of <paramref name="schema"/> (every schema when null) with the keys between them. With a
    /// <paramref name="focus"/>, only that table and the ones within <paramref name="hops"/> keys of it, in either
    /// direction and in any schema (0: all it is connected to). Views have no keys and stay out, unless focused.
    /// </summary>
    public static DiagramGraph Build(SchemaSnapshot s, IReadOnlyList<DbForeignKey> keys, string? schema = null, DbTable? focus = null, int hops = 1)
    {
        var byName = new Dictionary<(string?, string), DbTable>();
        foreach (var t in s.Tables) byName.TryAdd((t.Schema, t.Name), t);
        DbTable? Find(string? sc, string name) => byName.GetValueOrDefault((sc, name)) ?? s.Find(sc, name);

        var edges = new List<DiagramEdge>();
        foreach (var k in keys)
            if (Find(k.Schema, k.Table) is { } from && Find(k.RefSchema, k.RefTable) is { } to)
                edges.Add(new DiagramEdge(k, from, to));

        HashSet<DbTable> shown;
        if (focus is not null && Find(focus.Schema, focus.Name) is { } start)
        {
            shown = new HashSet<DbTable> { start };
            var frontier = new HashSet<DbTable> { start };
            for (int depth = 0; frontier.Count > 0 && (hops <= 0 || depth < hops); depth++)
            {
                var next = new HashSet<DbTable>();
                foreach (var e in edges)
                {
                    if (frontier.Contains(e.From) && shown.Add(e.To)) next.Add(e.To);
                    if (frontier.Contains(e.To) && shown.Add(e.From)) next.Add(e.From);
                }
                frontier = next;
            }
        }
        else shown = s.Tables.Where(t => t.Kind == DbObjectKind.Table && (schema is null || t.Schema == schema)).ToHashSet();

        var g = new DiagramGraph();
        // In the snapshot's order (by schema and name), so the same database always lays out the same way.
        g.Tables.AddRange(s.Tables.Where(shown.Contains));
        g.Edges.AddRange(edges.Where(e => shown.Contains(e.From) && shown.Contains(e.To)));
        return g;
    }
}
