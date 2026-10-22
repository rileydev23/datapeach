namespace DataPeach.Core.Diagram;

/// <summary>
/// Places the boxes of a diagram. Each group of connected tables is laid out in columns, a referenced table to the
/// left of the tables that point at it (the layered way of Graphviz), ordered inside each column to keep keys from
/// crossing; the groups, and then the tables with no keys, fill rows about as wide as the whole is tall.
/// </summary>
public static class DiagramLayout
{
    public const double GapX = 90, GapY = 30, GroupGap = 70;

    /// <summary>Top-left corner of each box, by index of <paramref name="sizes"/>; edges go from the referencing box to the referenced one.</summary>
    public static (double X, double Y)[] Arrange(IReadOnlyList<(double W, double H)> sizes, IReadOnlyList<(int From, int To)> edges)
    {
        int n = sizes.Count;
        var pos = new (double X, double Y)[n];
        var parents = new List<int>[n];
        var near = new List<int>[n];
        for (int i = 0; i < n; i++) { parents[i] = new(); near[i] = new(); }
        foreach (var (f, t) in edges)
        {
            if (f == t || f < 0 || t < 0 || f >= n || t >= n) continue;
            parents[f].Add(t);
            near[f].Add(t);
            near[t].Add(f);
        }

        // Groups of connected boxes, biggest first; the lone ones go last.
        var group = Enumerable.Repeat(-1, n).ToArray();
        var groups = new List<List<int>>();
        for (int i = 0; i < n; i++)
        {
            if (group[i] >= 0) continue;
            var members = new List<int> { i };
            group[i] = groups.Count;
            for (int k = 0; k < members.Count; k++)
                foreach (var m in near[members[k]])
                    if (group[m] < 0) { group[m] = groups.Count; members.Add(m); }
            members.Sort();
            groups.Add(members);
        }

        var blocks = new List<(List<int> Members, double W, double H)>();
        foreach (var members in groups.Where(g => g.Count > 1).OrderByDescending(g => g.Count))
        {
            var (w, h) = LayOut(members, sizes, parents, near, pos);
            blocks.Add((members, w, h));
        }
        foreach (var i in groups.Where(g => g.Count == 1).Select(g => g[0]))
        {
            pos[i] = (0, 0);
            blocks.Add((new List<int> { i }, sizes[i].W, sizes[i].H));
        }

        // Rows of blocks, about as wide as the whole is tall (and never narrower than the widest block).
        double area = blocks.Sum(b => (b.W + GroupGap) * (b.H + GroupGap));
        double rowWidth = Math.Max(blocks.Count == 0 ? 0 : blocks.Max(b => b.W), Math.Sqrt(area) * 1.4);
        double x = 0, y = 0, rowH = 0;
        foreach (var (members, w, h) in blocks)
        {
            if (x > 0 && x + w > rowWidth)
            {
                x = 0;
                y += rowH + GroupGap;
                rowH = 0;
            }
            foreach (var i in members) pos[i] = (pos[i].X + x, pos[i].Y + y);
            x += w + GroupGap;
            rowH = Math.Max(rowH, h);
        }
        return pos;
    }

    /// <summary>Lays out one connected group with its top-left at (0, 0); returns its size.</summary>
    private static (double W, double H) LayOut(List<int> members, IReadOnlyList<(double W, double H)> sizes, List<int>[] parents, List<int>[] near, (double X, double Y)[] pos)
    {
        // Column: 0 for a table that references nothing, else one past the furthest table it references. A key
        // that closes a cycle is ignored for this.
        var layer = new Dictionary<int, int>();
        var visiting = new HashSet<int>();
        int Layer(int i)
        {
            if (layer.TryGetValue(i, out var l)) return l;
            visiting.Add(i);
            l = 0;
            foreach (var p in parents[i])
                if (!visiting.Contains(p)) l = Math.Max(l, Layer(p) + 1);
            visiting.Remove(i);
            return layer[i] = l;
        }
        foreach (var i in members) Layer(i);

        var columns = members.GroupBy(i => layer[i]).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();

        // A few sweeps placing each table at the average height of its neighbours in the other columns.
        var rank = new Dictionary<int, double>();
        void Rank() { foreach (var col in columns) for (int k = 0; k < col.Count; k++) rank[col[k]] = (k + 0.5) / col.Count; }
        Rank();
        for (int sweep = 0; sweep < 6; sweep++)
        {
            foreach (var col in sweep % 2 == 0 ? columns : Enumerable.Reverse(columns))
            {
                var key = col.ToDictionary(i => i, i =>
                {
                    var others = near[i].Where(m => layer[m] != layer[i]).ToList();
                    return others.Count == 0 ? rank[i] : others.Average(m => rank[m]);
                });
                var sorted = col.OrderBy(i => key[i]).ThenBy(i => rank[i]).ToList();
                col.Clear();
                col.AddRange(sorted);
                for (int k = 0; k < col.Count; k++) rank[col[k]] = (k + 0.5) / col.Count;
            }
        }

        // Columns side by side, each centred on the tallest.
        var heights = columns.Select(col => col.Sum(i => sizes[i].H) + GapY * (col.Count - 1)).ToList();
        double tallest = heights.Max(), x = 0;
        for (int c = 0; c < columns.Count; c++)
        {
            double y = (tallest - heights[c]) / 2, width = columns[c].Max(i => sizes[i].W);
            foreach (var i in columns[c])
            {
                pos[i] = (x, y);
                y += sizes[i].H + GapY;
            }
            x += width + GapX;
        }
        return (x - GapX, tallest);
    }
}
