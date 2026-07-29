namespace WinUI.TableView;

/// <summary>
/// One entry of a multi-column sort chain: which column is sorted, in which direction, and at which priority.
/// </summary>
/// <remarks>
/// Priority 0 is the primary sort; higher numbers are applied as tie-breakers in order. Instances are handed to
/// <see cref="TableView.Sorting"/> as the complete prospective chain, and can be handed back to
/// <see cref="TableView.ApplySort"/> to restore a saved sort.
/// </remarks>
public partial class TableViewSortDescription
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TableViewSortDescription"/> class.
    /// </summary>
    /// <param name="column">The sorted column.</param>
    /// <param name="propertyPath">The property path to sort by, when known.</param>
    /// <param name="direction">The sort direction.</param>
    /// <param name="priority">The zero-based position in the sort chain; 0 is the primary sort.</param>
    public TableViewSortDescription(TableViewColumn column, string? propertyPath, SortDirection direction, int priority)
    {
        Column = column;
        PropertyPath = propertyPath;
        Direction = direction;
        Priority = priority;
    }

    /// <summary>
    /// Gets the sorted column.
    /// </summary>
    public TableViewColumn Column { get; }

    /// <summary>
    /// Gets the property path to sort by (<see cref="TableViewColumn.SortMemberPath"/> when set, otherwise the
    /// bound column's path), or <see langword="null"/> when the column exposes none.
    /// </summary>
    public string? PropertyPath { get; }

    /// <summary>
    /// Gets the sort direction.
    /// </summary>
    public SortDirection Direction { get; }

    /// <summary>
    /// Gets the zero-based position in the sort chain; 0 is the primary sort.
    /// </summary>
    public int Priority { get; }

    /// <summary>
    /// Returns a copy of this description at a different chain position.
    /// </summary>
    /// <param name="priority">The new zero-based priority.</param>
    public TableViewSortDescription WithPriority(int priority)
        => new(Column, PropertyPath, Direction, priority);

    /// <inheritdoc/>
    public override string ToString() => $"{Priority}: {PropertyPath ?? Column.Header?.ToString()} {Direction}";
}
