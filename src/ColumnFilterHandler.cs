using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using WinUI.TableView.Extensions;

namespace WinUI.TableView;

/// <summary>
/// Default implementation of the IColumnFilterHandler interface.
/// </summary>
public class ColumnFilterHandler : IColumnFilterHandler
{
    private readonly TableView _tableView;

    /// <summary>
    /// Initializes a new instance of the ColumnFilterHandler class.
    /// </summary>
    public ColumnFilterHandler(TableView tableView)
    {
        _tableView = tableView;
    }

    /// <inheritdoc/>
    public virtual IList<TableViewFilterItem> GetFilterItems(TableViewColumn column, string? searchText = default)
    {
        if (column is { TableView.ItemsSource: { } })
        {
            var collectionView = new CollectionView(liveShapingEnabled: false);
            collectionView.FilterDescriptions.AddRange(
                column.TableView.FilterDescriptions.Where(
                x => x is not ColumnFilterDescription columnFilter || columnFilter.Column != column));
            if (searchText is { Length: > 0 })
            {
                collectionView.FilterDescriptions.Add(new FilterDescription(default, item =>
                    {
                        var value = column.GetCellContent(item);
                        return string.IsNullOrEmpty(searchText) ||
                               value?.ToString()?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true;
                    }));
            }

            collectionView.Source = (column.TableView.ItemsSource as IEnumerable) ?? Enumerable.Empty<object>();

            var items = _tableView.ShowFilterItemsCount ?
                        GetFilterItemsWithCount(column, searchText, collectionView) :
                        GetFilterItems(column, searchText, collectionView);

            return [.. items];
        }

        return [];
    }

    private IEnumerable<TableViewFilterItem> GetFilterItemsWithCount(TableViewColumn column, string? searchText, CollectionView collectionView)
    {
        var nullCount = 0;
        var isNullItemSelected = !column.IsFiltered || !string.IsNullOrEmpty(searchText) ||
                                 (column.IsFiltered && SelectedValues[column].Contains(null));
        var filterValues = new SortedDictionary<object, int>();

        foreach (var item in collectionView)
        {
            var value = column.GetCellContent(item);

            if (IsBlank(value)) nullCount++;
            else if (filterValues.TryGetValue(value, out var count)) filterValues[value] = ++count;
            else filterValues.Add(value, 1);
        }

        IEnumerable<TableViewFilterItem> nullFilterItem = nullCount > 0 ? [new TableViewFilterItem(isNullItemSelected, null, nullCount, true)] : [];

        return [.. nullFilterItem,.. filterValues.Select(x =>
        {
            var isSelected = !column.IsFiltered || !string.IsNullOrEmpty(searchText) ||
                             (column.IsFiltered && SelectedValues[column].Contains(x.Key));
            return new TableViewFilterItem(isSelected, x.Key, x.Value, true);
        }) .OrderByDescending(x=>x.Count)];
    }

    private IEnumerable<TableViewFilterItem> GetFilterItems(TableViewColumn column, string? searchText, CollectionView collectionView)
    {
        var filterValues = new SortedSet<object?>();

        foreach (var item in collectionView)
        {
            var value = column.GetCellContent(item);
            value = IsBlank(value) ? null : value;
            filterValues.Add(value);
        }

        return [.. filterValues.Select(x =>
        {
            var isSelected = !column.IsFiltered || !string.IsNullOrEmpty(searchText) ||
                             (column.IsFiltered && SelectedValues[column].Contains(x));
            return new TableViewFilterItem(isSelected, x, 0);
        })];
    }

    private static bool IsBlank([NotNullWhen(false)] object? value)
    {
        return value == null ||
               value == DBNull.Value ||
               (value is string str && string.IsNullOrWhiteSpace(str)) ||
               (value is Guid guid && guid == Guid.Empty);
    }

    /// <summary>
    /// Per-column operator filters set from the filter flyout. Columns filtered by the classic checkbox list have
    /// no entry here and are evaluated against <see cref="SelectedValues"/> instead.
    /// </summary>
    private readonly Dictionary<TableViewColumn, TableViewFilterDescriptor> _descriptors = [];

    /// <inheritdoc/>
    public virtual void ApplyFilter(TableViewFilterDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (descriptor.Operator is TableViewFilterOperator.SelectedValues)
        {
            _descriptors.Remove(descriptor.Column);

            if (descriptor.SelectedValues is not null)
            {
                SelectedValues[descriptor.Column] = descriptor.SelectedValues;
            }
        }
        else
        {
            // Remember the operator filter so Filter() evaluates it for every item.
            _descriptors[descriptor.Column] = descriptor;
        }

        ApplyFilter(descriptor.Column);
    }

    /// <inheritdoc/>
    public virtual void ApplyFilter(TableViewColumn column)
    {
        // Raise the Filtering event first so an app can take over (symmetric with the Sorting event). The
        // descriptor describes the checkbox selection the flyout produced; custom operators can be supplied by
        // calling ApplyFilter(descriptor) directly.
        if (column?.TableView is { } tableView)
        {
            var args = new TableViewFilteringEventArgs(new TableViewFilterDescriptor(
                column,
                TableViewFilterOperator.SelectedValues,
                selectedValues: SelectedValues.TryGetValue(column, out var values) ? values : null));

            tableView.OnFiltering(args);

            if (args.Handled)
            {
                column.IsFiltered = true;
                return;
            }
        }

        if (column is { TableView.CollectionView: CollectionView { } collectionView })
        {
            using var defer = collectionView.DeferRefresh();
            column.TableView.DeselectAll();

            if (!column.IsFiltered)
            {
                var boundColumn = column as TableViewBoundColumn;

                column.IsFiltered = true;
                collectionView.FilterDescriptions.Add(new ColumnFilterDescription(
                    column,
                    boundColumn?.PropertyPath,
                    (o) => Filter(column, o)));
            }
        }
    }

    /// <inheritdoc/>
    public virtual void ClearFilter(TableViewColumn? column)
    {
        var clearArgs = new TableViewClearFilterEventArgs(column);
        _tableView.OnClearFilter(clearArgs);

        if (clearArgs.Handled)
        {
            // The app cleared its own filter state; still reflect it on the column(s) so the funnel icon updates.
            if (column is not null)
            {
                column.IsFiltered = false;
                SelectedValues.RemoveWhere(x => x.Key == column);
                _descriptors.Remove(column);
            }
            else
            {
                SelectedValues.Clear();
                _descriptors.Clear();

                foreach (var col in _tableView.Columns)
                {
                    col?.IsFiltered = false;
                }
            }

            return;
        }

        if (column is { TableView.CollectionView: CollectionView { } collectionView })
        {
            using var defer = collectionView.DeferRefresh();
            column.IsFiltered = false;
            collectionView.FilterDescriptions.RemoveWhere(x => x is ColumnFilterDescription columnFilter && columnFilter.Column == column);
            SelectedValues.RemoveWhere(x => x.Key == column);
            _descriptors.Remove(column);
        }
        else
        {
            SelectedValues.Clear();
            _descriptors.Clear();
            _tableView.FilterDescriptions.Clear();

            foreach (var col in _tableView.Columns)
            {
                col?.IsFiltered = false;
            }
        }
    }

    /// <inheritdoc/>
    public virtual bool Filter(TableViewColumn column, object? item)
    {
        // Operator filter (Equals, Larger than, Contains, Between, …) takes precedence when one was applied.
        if (_descriptors.TryGetValue(column, out var descriptor))
        {
            return descriptor.MatchesItem(item);
        }

        var value = column.GetCellContent(item);
        value = IsBlank(value) ? null : value!;
        return SelectedValues.TryGetValue(column, out var selected) && selected.Contains(value);
    }

    /// <inheritdoc/>
    public IDictionary<TableViewColumn, ICollection<object?>> SelectedValues { get; } = new Dictionary<TableViewColumn, ICollection<object?>>();
}
