using System.Collections.Generic;
using System.ComponentModel;

namespace WinUI.TableView;

/// <summary>
/// Provides data for the event that is raised when a column is being sorted in a TableView.
/// </summary>
/// <remarks>
/// Set <see cref="HandledEventArgs.Handled"/> to take over sorting entirely — the built-in sort is then skipped, and
/// the handler is responsible for reordering the data AND for reflecting the new state on the columns (assign
/// <see cref="TableViewColumn.SortDirection"/>/<see cref="TableViewColumn.SortPriority"/>, or call
/// <see cref="TableView.ApplySort"/> with <see cref="SortDescriptions"/> to have the grid do it).
/// </remarks>
public partial class TableViewSortingEventArgs : HandledEventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TableViewSortingEventArgs"/> class.
    /// </summary>
    /// <param name="column">The column whose header was acted on.</param>
    /// <param name="direction">The requested direction for that column, or <see langword="null"/> to unsort it.</param>
    /// <param name="isMultiSort">Whether the column is being added to the existing sort chain.</param>
    /// <param name="sortDescriptions">The complete sort chain that results from this action, in priority order.</param>
    public TableViewSortingEventArgs(
        TableViewColumn column,
        SortDirection? direction,
        bool isMultiSort,
        IReadOnlyList<TableViewSortDescription> sortDescriptions)
    {
        Column = column;
        Direction = direction;
        IsMultiSort = isMultiSort;
        SortDescriptions = sortDescriptions;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TableViewSortingEventArgs"/> class for a single-column sort.
    /// </summary>
    /// <param name="column">The column that is being sorted.</param>
    public TableViewSortingEventArgs(TableViewColumn column) : this(column, null, false, [])
    {
    }

    /// <summary>
    /// Gets the column whose header was acted on.
    /// </summary>
    public TableViewColumn Column { get; }

    /// <summary>
    /// Gets the requested direction for <see cref="Column"/>, or <see langword="null"/> when it is being unsorted.
    /// </summary>
    public SortDirection? Direction { get; }

    /// <summary>
    /// Gets whether the column is being added to the existing sort chain (Ctrl+click or Shift+click) rather than
    /// replacing it.
    /// </summary>
    public bool IsMultiSort { get; }

    /// <summary>
    /// Gets the complete sort chain resulting from this action, ordered by
    /// <see cref="TableViewSortDescription.Priority"/> (0 = primary). Already trimmed to
    /// <see cref="TableView.MaxSortColumns"/>. Empty when the action clears sorting.
    /// </summary>
    public IReadOnlyList<TableViewSortDescription> SortDescriptions { get; }
}
