using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace WinUI.TableView.Tests;

/// <summary>
/// Covers the multi-column sort chain (ordered, capped by MaxSortColumns, surfaced through the Sorting event) and
/// the filter operator model + Filtering/ClearFilter events.
/// </summary>
[TestClass]
public class TableViewSortFilterTests
{
    // ---------------------------------------------------------------------------------------------------------
    // Multi-column sorting
    // ---------------------------------------------------------------------------------------------------------

    [UITestMethod]
    public async Task ApplySort_BuildsOrderedChain_AndStampsColumns()
    {
        var tableView = await LoadAsync();
        var columns = tableView.Columns.ToList();

        tableView.ApplySort(
        [
            Descriptor(columns[1], SortDirection.Descending, 0),
            Descriptor(columns[3], SortDirection.Ascending, 1),
            Descriptor(columns[0], SortDirection.Descending, 2),
        ]);

        CollectionAssert.AreEqual(
            new[] { columns[1], columns[3], columns[0] },
            tableView.SortChain.Select(d => d.Column).ToArray());

        Assert.AreEqual(0, columns[1].SortPriority);
        Assert.AreEqual(1, columns[3].SortPriority);
        Assert.AreEqual(2, columns[0].SortPriority);
        Assert.AreEqual(SortDirection.Descending, columns[1].SortDirection);
        Assert.AreEqual(SortDirection.Ascending, columns[3].SortDirection);

        // Columns outside the chain are cleared.
        Assert.IsNull(columns[2].SortDirection);
        Assert.AreEqual(-1, columns[2].SortPriority);

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task ApplySort_TrimsChainToMaxSortColumns()
    {
        var tableView = await LoadAsync(columnCount: 8);
        tableView.MaxSortColumns = 5;
        var columns = tableView.Columns.ToList();

        tableView.ApplySort(columns.Take(7)
            .Select((column, i) => Descriptor(column, SortDirection.Ascending, i)));

        Assert.AreEqual(5, tableView.SortChain.Count);
        Assert.IsNull(columns[5].SortDirection, "the 6th requested column must be dropped");

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task ApplySort_ClearsSortingWhenEmpty()
    {
        var tableView = await LoadAsync();
        var columns = tableView.Columns.ToList();

        tableView.ApplySort([Descriptor(columns[0], SortDirection.Ascending, 0)]);
        Assert.AreEqual(1, tableView.SortChain.Count);

        tableView.ApplySort([]);

        Assert.AreEqual(0, tableView.SortChain.Count);
        Assert.IsNull(columns[0].SortDirection);
        Assert.AreEqual(-1, columns[0].SortPriority);

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task SortingEvent_CarriesFullOrderedChain_AndCanBeHandled()
    {
        var tableView = await LoadAsync(columnCount: 8);
        var columns = tableView.Columns.ToList();

        // Seed a two-column chain, then let the app take over the third click.
        tableView.ApplySort(
        [
            Descriptor(columns[2], SortDirection.Descending, 0),
            Descriptor(columns[4], SortDirection.Ascending, 1),
        ]);

        TableViewSortingEventArgs? captured = null;
        tableView.Sorting += (_, e) =>
        {
            captured = e;
            e.Handled = true; // app sorts the data itself
        };

        // Simulate a Ctrl+click on a third column through the same path the header uses.
        columns[6].HeaderControl!.InvokeSortCycle(multiSort: true);

        Assert.IsNotNull(captured);
        Assert.AreSame(columns[6], captured!.Column);
        Assert.IsTrue(captured.IsMultiSort);
        CollectionAssert.AreEqual(
            new[] { columns[2], columns[4], columns[6] },
            captured.SortDescriptions.Select(d => d.Column).ToArray());
        CollectionAssert.AreEqual(
            new[] { 0, 1, 2 },
            captured.SortDescriptions.Select(d => d.Priority).ToArray());

        // Handled: the grid must not have applied it.
        Assert.AreEqual(2, tableView.SortChain.Count);

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task HeaderClick_MultiSort_AppendsAndCapsChain()
    {
        var tableView = await LoadAsync(columnCount: 8);
        tableView.MaxSortColumns = 3;
        var columns = tableView.Columns.ToList();

        for (var i = 0; i < 5; i++)
        {
            columns[i].HeaderControl!.InvokeSortCycle(multiSort: true);
        }

        // Capped at 3, keeping the most recently clicked columns in click order.
        CollectionAssert.AreEqual(
            new[] { columns[2], columns[3], columns[4] },
            tableView.SortChain.Select(d => d.Column).ToArray());

        await UnloadAsync(tableView);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Filter operators + events
    // ---------------------------------------------------------------------------------------------------------

    [UITestMethod] // constructing a column creates a Binding, which requires the UI thread
    public void FilterDescriptor_EvaluatesEveryOperator()
    {
        var column = new TableViewTextColumn();

        Assert.IsTrue(Match(column, TableViewFilterOperator.Equals, "abc", "abc"));
        Assert.IsFalse(Match(column, TableViewFilterOperator.NotEquals, "abc", "abc"));
        Assert.IsTrue(Match(column, TableViewFilterOperator.Contains, "abcdef", "cde"));
        Assert.IsTrue(Match(column, TableViewFilterOperator.NotContains, "abcdef", "xyz"));
        Assert.IsTrue(Match(column, TableViewFilterOperator.StartsWith, "abcdef", "abc"));
        Assert.IsTrue(Match(column, TableViewFilterOperator.EndsWith, "abcdef", "def"));
        Assert.IsTrue(Match(column, TableViewFilterOperator.GreaterThan, 10, 5));
        Assert.IsTrue(Match(column, TableViewFilterOperator.GreaterThanOrEqual, 10, 10));
        Assert.IsTrue(Match(column, TableViewFilterOperator.LessThan, 5, 10));
        Assert.IsTrue(Match(column, TableViewFilterOperator.LessThanOrEqual, 10, 10));
        Assert.IsTrue(Match(column, TableViewFilterOperator.IsEmpty, "   "));
        Assert.IsTrue(Match(column, TableViewFilterOperator.IsNotEmpty, "x"));

        // Between uses both bounds, inclusive.
        var between = new TableViewFilterDescriptor(column, TableViewFilterOperator.Between, 5, 10);
        Assert.IsTrue(between.Matches(5));
        Assert.IsTrue(between.Matches(10));
        Assert.IsFalse(between.Matches(11));

        // Numeric comparison still works when the filter value arrives as text (typed into a filter box).
        Assert.IsTrue(Match(column, TableViewFilterOperator.GreaterThan, 10, "5"));
        Assert.IsFalse(Match(column, TableViewFilterOperator.GreaterThan, 10, "50"));

        // Checkbox mode.
        var selected = new TableViewFilterDescriptor(
            column, TableViewFilterOperator.SelectedValues, selectedValues: new List<object?> { "a", null });
        Assert.IsTrue(selected.Matches("a"));
        Assert.IsTrue(selected.Matches(null), "blank cells map to the null entry");
        Assert.IsFalse(selected.Matches("b"));
    }

    [UITestMethod]
    public async Task FilteringAndClearFilterEvents_FireAndCanBeHandled()
    {
        var tableView = await LoadAsync();
        var column = tableView.Columns[0];

        TableViewFilteringEventArgs? filtering = null;
        TableViewClearFilterEventArgs? clearing = null;
        tableView.Filtering += (_, e) => { filtering = e; e.Handled = true; };
        tableView.ClearFilter += (_, e) => { clearing = e; e.Handled = true; };

        tableView.FilterHandler.SelectedValues[column] = new List<object?> { "Item 1" };
        tableView.FilterHandler.ApplyFilter(column);

        Assert.IsNotNull(filtering);
        Assert.AreSame(column, filtering!.Column);
        Assert.AreEqual(TableViewFilterOperator.SelectedValues, filtering.Descriptor.Operator);
        CollectionAssert.AreEqual(new object?[] { "Item 1" }, filtering.Descriptor.SelectedValues!.ToArray());
        Assert.IsTrue(column.IsFiltered, "handled filtering still marks the column so the funnel icon shows");
        Assert.AreEqual(0, tableView.FilterDescriptions.Count, "handled: the built-in filter must not be applied");

        tableView.FilterHandler.ClearFilter(column);

        Assert.IsNotNull(clearing);
        Assert.AreSame(column, clearing!.Column);
        Assert.IsFalse(column.IsFiltered);

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task OperatorFilter_AppliedThroughHandler_FiltersRows()
    {
        var tableView = await LoadAsync();
        var column = tableView.Columns[0];

        // What the flyout produces when the user picks "Ends with" and types 20. (These test columns bind Name,
        // so a text operator is the meaningful one here; numeric comparisons are covered by the descriptor tests.)
        tableView.FilterHandler.ApplyFilter(
            new TableViewFilterDescriptor(column, TableViewFilterOperator.EndsWith, "20"));

        Assert.IsTrue(column.IsFiltered);

        // The filter predicate must evaluate the OPERATOR, not the checkbox selection.
        Assert.IsTrue(tableView.FilterHandler.Filter(column, new SortItem { Name = "Item 20", Value = 20 }));
        Assert.IsFalse(tableView.FilterHandler.Filter(column, new SortItem { Name = "Item 3", Value = 3 }));

        tableView.FilterHandler.ClearFilter(column);
        Assert.IsFalse(column.IsFiltered);

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public void OperatorOptions_AreTypeAware()
    {
        var forText = Controls.TableViewFilterItemsControl.OperatorOption.ForValueType(typeof(string))
            .Select(o => o.Operator).ToList();
        var forNumber = Controls.TableViewFilterItemsControl.OperatorOption.ForValueType(typeof(double))
            .Select(o => o.Operator).ToList();

        // Text columns get the text operators, not the comparisons.
        CollectionAssert.Contains(forText, TableViewFilterOperator.Contains);
        CollectionAssert.Contains(forText, TableViewFilterOperator.StartsWith);
        CollectionAssert.DoesNotContain(forText, TableViewFilterOperator.Between);

        // Numeric columns get the comparisons the user asked for, not the text operators.
        CollectionAssert.Contains(forNumber, TableViewFilterOperator.GreaterThan);
        CollectionAssert.Contains(forNumber, TableViewFilterOperator.LessThan);
        CollectionAssert.Contains(forNumber, TableViewFilterOperator.Between);
        CollectionAssert.DoesNotContain(forNumber, TableViewFilterOperator.Contains);

        // Both keep the classic checkbox mode first, plus the empty checks.
        Assert.AreEqual(TableViewFilterOperator.SelectedValues, forText[0]);
        Assert.AreEqual(TableViewFilterOperator.SelectedValues, forNumber[0]);
        CollectionAssert.Contains(forNumber, TableViewFilterOperator.IsEmpty);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------

    private static bool Match(TableViewColumn column, TableViewFilterOperator op, object? cellValue, object? filterValue = null)
        => new TableViewFilterDescriptor(column, op, filterValue).Matches(cellValue);

    private static TableViewSortDescription Descriptor(TableViewColumn column, SortDirection direction, int priority)
        => new(column, nameof(SortItem.Name), direction, priority);

    private static async Task<TableView> LoadAsync(int columnCount = 5)
    {
        var tableView = new TableView
        {
            AutoGenerateColumns = false,
            RowHeight = 32,
            Width = 900,
            Height = 400,
            ItemsSource = new ObservableCollection<SortItem>(
                Enumerable.Range(0, 20).Select(i => new SortItem { Name = $"Item {i}", Value = i })),
        };

        for (var i = 0; i < columnCount; i++)
        {
            tableView.Columns.Add(new TableViewTextColumn
            {
                Header = $"C{i}",
                Width = new GridLength(100, GridUnitType.Pixel),
                Binding = new Binding { Path = new PropertyPath(nameof(SortItem.Name)) },
            });
        }

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(tableView);
        tableView.UpdateLayout();

        return tableView;
    }

    private static async Task UnloadAsync(TableView tableView)
        => await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);

    private sealed class SortItem
    {
        public string Name { get; set; } = string.Empty;
        public int Value { get; set; }
    }
}
