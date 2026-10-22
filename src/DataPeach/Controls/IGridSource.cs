namespace DataPeach.Controls;

/// <summary>How a cell is drawn besides its value (colors from docs/DISENO.md 3.6 and 3.4).</summary>
public enum CellState { Normal, Edited, Inserted, Deleted }

/// <summary>What <see cref="ResultGrid"/> shows. Rows are only read for the visible area, so any size is fine.</summary>
public interface IGridSource
{
    int ColumnCount { get; }
    string ColumnName(int c);
    /// <summary>Header tooltip and second line ("int4", "varchar(50)").</summary>
    string ColumnType(int c);
    bool IsNumeric(int c);
    /// <summary>Part of the primary key: a key glyph in the header.</summary>
    bool IsKey(int c);

    int RowCount { get; }
    object? Value(int row, int col);
    CellState State(int row, int col);

    /// <summary>Cells can be edited (a table with a primary key, not read-only).</summary>
    bool CanEdit { get; }
    /// <summary>Sets a cell from the edit box; null text means NULL. Returns an error to show, or null.</summary>
    string? SetValue(int row, int col, string? text);

    /// <summary>Rows were added at the end (streaming) or values/states changed.</summary>
    event Action? Changed;
    /// <summary>The columns changed: widths and scroll start over.</summary>
    event Action? Reset;
}
