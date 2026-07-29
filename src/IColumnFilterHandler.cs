using System.Collections.Generic;

namespace WinUI.TableView;

/// <summary>
/// Interface for handling column filtering in a TableView.
/// </summary>
public interface IColumnFilterHandler
{
    /// <summary>
    /// Gets or sets the selected values for the filter per column.
    /// </summary>
    IDictionary<TableViewColumn, ICollection<object?>> SelectedValues { get; }

    /// <summary>
    /// Get the filter items for the specified column.
    /// </summary>
    /// <param name="column">The column for which to prepare filter items.</param>
    /// <param name="searchText">The search text to filter the items.</param>
    IList<TableViewFilterItem> GetFilterItems(TableViewColumn column, string? searchText);

    /// <summary>
    /// Applies the filter to the specified column.
    /// </summary>
    /// <param name="column">The column to which the filter is applied.</param>
    void ApplyFilter(TableViewColumn column);

    /// <summary>
    /// Applies an operator based filter (Equals, Larger than, Contains, Between, …) to a column.
    /// </summary>
    /// <remarks>
    /// Has a default implementation that ignores the operator and falls back to
    /// <see cref="ApplyFilter(TableViewColumn)"/>, so existing handlers keep working; override it to support the
    /// operators offered by the filter flyout.
    /// </remarks>
    /// <param name="descriptor">The filter to apply: column, operator and value(s).</param>
    void ApplyFilter(TableViewFilterDescriptor descriptor) => ApplyFilter(descriptor.Column);

    /// <summary>
    /// Clears the filter from the specified column.
    /// </summary>
    /// <param name="column">The column from which the filter is cleared.</param>
    void ClearFilter(TableViewColumn? column);

    /// <summary>
    /// Determines whether the specified item passes the filter for the specified column.
    /// </summary>
    /// <param name="column">The column for which the filter is applied.</param>
    /// <param name="item">The item to check.</param>
    /// <returns>True if the item passes the filter; otherwise, false.</returns>
    bool Filter(TableViewColumn column, object? item);
}
