using System.Text;
using DataPeach.Core.Data;
using DataPeach.Core.Db;

namespace DataPeach.Core.Staging;

public enum ChangeKind { Update, Insert, Delete }

/// <summary>A row of a table identified by its primary key values (in <see cref="DbTable.PrimaryKey"/> order).</summary>
public sealed record RowKey(IReadOnlyList<object?> Values)
{
    public string Id { get; } = ValueText.Key(Values);
}

/// <summary>One pending change of the staging area.</summary>
public sealed class StagedChange
{
    internal StagedChange(ChangeKind kind, DbTable table, RowKey? key, int seq)
    {
        Kind = kind;
        Table = table;
        Key = key;
        Seq = seq;
    }

    public ChangeKind Kind { get; }
    public DbTable Table { get; }
    /// <summary>Update/Delete: the row. Null for an insert.</summary>
    public RowKey? Key { get; }
    /// <summary>Order in which the change was first made; statements run in this order.</summary>
    public int Seq { get; }
    /// <summary>Update: new values by column (null = NULL). Insert: the values typed so far; the rest use DEFAULT.</summary>
    public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);
    /// <summary>Update: values before the change, for the preview.</summary>
    public Dictionary<string, object?> Original { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// A change turned into SQL: what runs (with parameters) and what the user reviews (with literals). A DELETE also
/// carries the SELECT that reads its row first, for the delete history.
/// </summary>
public sealed record StagedStatement(StagedChange Change, SqlCommandSpec Command, string Preview, SqlCommandSpec? Capture = null);

/// <summary>
/// Edits made in a table grid wait here until the user reviews the generated SQL and applies it in one transaction
/// (docs/DISENO.md, "Propio de DataPeach"). Several edits of the same row become one UPDATE; editing an inserted row
/// changes its INSERT; deleting a row drops its pending edits; deleting an inserted row just forgets it. Pure.
/// </summary>
public sealed class StagingArea
{
    private readonly List<StagedChange> _changes = new();
    private int _seq;

    public IReadOnlyList<StagedChange> Changes => _changes;
    public int Count => _changes.Count;
    public event Action? Changed;

    private static string TableId(DbTable t) => t.Display;

    private StagedChange? Find(ChangeKind kind, DbTable t, RowKey key) =>
        _changes.FirstOrDefault(c => c.Kind == kind && c.Key?.Id == key.Id && TableId(c.Table) == TableId(t));

    public bool IsDeleted(DbTable t, RowKey key) => Find(ChangeKind.Delete, t, key) is not null;

    /// <summary>The pending value of a cell, if it was edited.</summary>
    public bool TryGetEdit(DbTable t, RowKey key, string column, out object? value)
    {
        value = null;
        return Find(ChangeKind.Update, t, key) is { } u && u.Values.TryGetValue(column, out value);
    }

    public IEnumerable<StagedChange> Inserts(DbTable t) =>
        _changes.Where(c => c.Kind == ChangeKind.Insert && TableId(c.Table) == TableId(t));

    /// <summary>
    /// Sets a cell of an existing row. Setting it back to <paramref name="original"/> removes the edit (and the
    /// UPDATE when no edit is left).
    /// </summary>
    public void Edit(DbTable t, RowKey key, string column, object? original, object? value)
    {
        if (!t.CanEdit) throw new InvalidOperationException(Loc.T("staging.noKey"));
        if (IsDeleted(t, key)) return;
        var u = Find(ChangeKind.Update, t, key);
        if (SameValue(original, value))
        {
            if (u is null) return;
            u.Values.Remove(column);
            u.Original.Remove(column);
            if (u.Values.Count == 0) _changes.Remove(u);
            Changed?.Invoke();
            return;
        }
        if (u is null)
        {
            u = new StagedChange(ChangeKind.Update, t, key, ++_seq);
            _changes.Add(u);
        }
        if (!u.Original.ContainsKey(column)) u.Original[column] = original;
        u.Values[column] = value;
        Changed?.Invoke();
    }

    /// <summary>A new empty row for <paramref name="t"/>; its columns are filled with <see cref="EditInsert"/>.</summary>
    public StagedChange AddInsert(DbTable t)
    {
        if (!t.CanEdit) throw new InvalidOperationException(Loc.T("staging.noKey"));
        var c = new StagedChange(ChangeKind.Insert, t, null, ++_seq);
        _changes.Add(c);
        Changed?.Invoke();
        return c;
    }

    public void EditInsert(StagedChange insert, string column, object? value)
    {
        insert.Values[column] = value;
        Changed?.Invoke();
    }

    public void Delete(DbTable t, RowKey key)
    {
        if (!t.CanEdit) throw new InvalidOperationException(Loc.T("staging.noKey"));
        if (IsDeleted(t, key)) return;
        if (Find(ChangeKind.Update, t, key) is { } u) _changes.Remove(u);
        _changes.Add(new StagedChange(ChangeKind.Delete, t, key, ++_seq));
        Changed?.Invoke();
    }

    /// <summary>Takes back one change (an insert, a delete or a whole row's edits).</summary>
    public void Remove(StagedChange c)
    {
        if (_changes.Remove(c)) Changed?.Invoke();
    }

    public void Clear()
    {
        if (_changes.Count == 0) return;
        _changes.Clear();
        Changed?.Invoke();
    }

    private static bool SameValue(object? a, object? b)
    {
        if (a is null or DBNull) return b is null or DBNull;
        if (b is null or DBNull) return false;
        return ValueText.Full(a) == ValueText.Full(b);
    }

    // ------------------------------------------------------------------ SQL

    /// <summary>The statements that apply every change, in the order the changes were made.</summary>
    public List<StagedStatement> Build(Dialect d) =>
        _changes.OrderBy(c => c.Seq).Select(c => Build(c, d)).ToList();

    public static StagedStatement Build(StagedChange c, Dialect d)
    {
        var args = new List<object?>();
        var exec = new StringBuilder();
        var view = new StringBuilder();
        var table = d.NameOf(c.Table);
        SqlCommandSpec? capture = null;

        void Value(object? v, DbColumn? col)
        {
            exec.Append(d.Param(args.Count, v, col));
            args.Add(v);
            view.Append(d.Literal(v));
        }

        void Both(string s)
        {
            exec.Append(s);
            view.Append(s);
        }

        void Where()
        {
            Both(" WHERE ");
            var pk = c.Table.PrimaryKey;
            for (int i = 0; i < pk.Count; i++)
            {
                if (i > 0) Both(" AND ");
                Both(d.Name(pk[i].Name));
                var v = c.Key!.Values[i];
                if (v is null) { Both(" IS NULL"); continue; }
                Both(" = ");
                Value(v, pk[i]);
            }
        }

        switch (c.Kind)
        {
            case ChangeKind.Update:
                Both("UPDATE " + table + " SET ");
                bool first = true;
                foreach (var col in c.Table.Columns.Where(x => c.Values.ContainsKey(x.Name)))
                {
                    if (!first) Both(", ");
                    first = false;
                    Both(d.Name(col.Name) + " = ");
                    Value(c.Values[col.Name], col);
                }
                Where();
                break;
            case ChangeKind.Delete:
                Both("DELETE FROM " + table);
                int whereAt = exec.Length, argsAt = args.Count;
                Where();
                // The same WHERE, with the same values, reads the row before it goes.
                var where = exec.ToString()[whereAt..];
                capture = new SqlCommandSpec("SELECT * FROM " + table + where, args.Skip(argsAt).ToList());
                break;
            case ChangeKind.Insert:
                var cols = c.Table.Columns.Where(x => c.Values.ContainsKey(x.Name)).ToList();
                if (cols.Count == 0)
                {
                    Both(d.Kind == DbKind.MySql ? $"INSERT INTO {table} () VALUES ()" : $"INSERT INTO {table} DEFAULT VALUES");
                    break;
                }
                Both("INSERT INTO " + table + " (" + string.Join(", ", cols.Select(x => d.Name(x.Name))) + ") VALUES (");
                for (int i = 0; i < cols.Count; i++)
                {
                    if (i > 0) Both(", ");
                    Value(c.Values[cols[i].Name], cols[i]);
                }
                Both(")");
                break;
        }
        return new StagedStatement(c, new SqlCommandSpec(exec.ToString(), args), view + ";", capture);
    }
}
