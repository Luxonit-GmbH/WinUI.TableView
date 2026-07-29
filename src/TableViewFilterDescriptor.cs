using System;
using System.Collections.Generic;
using System.Globalization;

namespace WinUI.TableView;

/// <summary>
/// Describes one column's filter: the comparison to use and the value(s) to compare against.
/// </summary>
/// <remarks>
/// Carried by <see cref="TableView.Filtering"/> so an application can translate it into its own query, and
/// evaluated in-process by <see cref="Matches"/> (which the built-in filter handler uses), so both paths share one
/// definition of what each operator means.
/// </remarks>
public partial class TableViewFilterDescriptor
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TableViewFilterDescriptor"/> class.
    /// </summary>
    /// <param name="column">The filtered column.</param>
    /// <param name="op">The comparison to apply.</param>
    /// <param name="value">The value to compare against (the lower bound for <see cref="TableViewFilterOperator.Between"/>).</param>
    /// <param name="secondValue">The upper bound for <see cref="TableViewFilterOperator.Between"/>.</param>
    /// <param name="selectedValues">The accepted values for <see cref="TableViewFilterOperator.SelectedValues"/>.</param>
    public TableViewFilterDescriptor(
        TableViewColumn column,
        TableViewFilterOperator op = TableViewFilterOperator.SelectedValues,
        object? value = null,
        object? secondValue = null,
        ICollection<object?>? selectedValues = null)
    {
        Column = column;
        Operator = op;
        Value = value;
        SecondValue = secondValue;
        SelectedValues = selectedValues;
    }

    /// <summary>
    /// Gets the filtered column.
    /// </summary>
    public TableViewColumn Column { get; }

    /// <summary>
    /// Gets the comparison to apply.
    /// </summary>
    public TableViewFilterOperator Operator { get; }

    /// <summary>
    /// Gets the value to compare against; the lower bound for <see cref="TableViewFilterOperator.Between"/>.
    /// </summary>
    public object? Value { get; }

    /// <summary>
    /// Gets the upper bound for <see cref="TableViewFilterOperator.Between"/>.
    /// </summary>
    public object? SecondValue { get; }

    /// <summary>
    /// Gets the accepted values for <see cref="TableViewFilterOperator.SelectedValues"/>.
    /// </summary>
    public ICollection<object?>? SelectedValues { get; }

    /// <summary>
    /// Evaluates this filter against a single data item using the column's bound value.
    /// </summary>
    /// <param name="item">The data item to test.</param>
    /// <returns>Whether the item passes the filter.</returns>
    public bool MatchesItem(object? item) => Matches(Column.GetCellContent(item));

    /// <summary>
    /// Evaluates this filter against an already-extracted cell value.
    /// </summary>
    /// <param name="cellValue">The cell value to test.</param>
    /// <returns>Whether the value passes the filter.</returns>
    public bool Matches(object? cellValue)
    {
        var isBlank = cellValue is null || cellValue == DBNull.Value
            || (cellValue is string blank && string.IsNullOrWhiteSpace(blank));

        switch (Operator)
        {
            case TableViewFilterOperator.IsEmpty:
                return isBlank;
            case TableViewFilterOperator.IsNotEmpty:
                return !isBlank;
            case TableViewFilterOperator.SelectedValues:
                return SelectedValues?.Contains(isBlank ? null : cellValue) is true;
        }

        // Text comparisons work on the string form so they apply to any column type.
        var text = cellValue?.ToString() ?? string.Empty;
        var filterText = Value?.ToString() ?? string.Empty;

        switch (Operator)
        {
            case TableViewFilterOperator.Contains:
                return text.Contains(filterText, StringComparison.CurrentCultureIgnoreCase);
            case TableViewFilterOperator.NotContains:
                return !text.Contains(filterText, StringComparison.CurrentCultureIgnoreCase);
            case TableViewFilterOperator.StartsWith:
                return text.StartsWith(filterText, StringComparison.CurrentCultureIgnoreCase);
            case TableViewFilterOperator.EndsWith:
                return text.EndsWith(filterText, StringComparison.CurrentCultureIgnoreCase);
            case TableViewFilterOperator.Equals:
                return Compare(cellValue, Value) == 0;
            case TableViewFilterOperator.NotEquals:
                return Compare(cellValue, Value) != 0;
            case TableViewFilterOperator.GreaterThan:
                return Compare(cellValue, Value) > 0;
            case TableViewFilterOperator.GreaterThanOrEqual:
                return Compare(cellValue, Value) >= 0;
            case TableViewFilterOperator.LessThan:
                return Compare(cellValue, Value) < 0;
            case TableViewFilterOperator.LessThanOrEqual:
                return Compare(cellValue, Value) <= 0;
            case TableViewFilterOperator.Between:
                return Compare(cellValue, Value) >= 0 && Compare(cellValue, SecondValue) <= 0;
            default:
                return true;
        }
    }

    /// <summary>
    /// Compares a cell value with a filter value, converting the filter value to the cell's type when possible so
    /// that text typed in a filter box still compares numerically or chronologically. Values that cannot be
    /// compared fall back to a case-insensitive string comparison.
    /// </summary>
    private static int Compare(object? cellValue, object? filterValue)
    {
        if (cellValue is null || filterValue is null)
        {
            return cellValue is null && filterValue is null ? 0 : cellValue is null ? -1 : 1;
        }

        if (cellValue.GetType() != filterValue.GetType())
        {
            try
            {
                filterValue = Convert.ChangeType(filterValue, cellValue.GetType(), CultureInfo.CurrentCulture);
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or ArgumentException)
            {
                return string.Compare(cellValue.ToString(), filterValue.ToString(), StringComparison.CurrentCultureIgnoreCase);
            }
        }

        return cellValue is IComparable comparable
            ? comparable.CompareTo(filterValue)
            : string.Compare(cellValue.ToString(), filterValue.ToString(), StringComparison.CurrentCultureIgnoreCase);
    }
}
