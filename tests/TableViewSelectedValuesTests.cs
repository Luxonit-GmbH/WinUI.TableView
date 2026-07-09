using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace WinUI.TableView.Tests;

/// <summary>
/// Covers <see cref="TableView.SelectedValues"/>, <see cref="TableView.SelectedCellValues"/> and
/// <see cref="TableView.SelectedCellSlots"/> — in particular NON-CONTIGUOUS selection (skipped rows), which
/// produces many disjoint ranges rather than one contiguous span.
/// </summary>
[TestClass]
public class TableViewSelectedValuesTests
{
    [UITestMethod]
    public async Task SelectedValues_OddRowsOnly_ReturnsExactlyTheSkippedSelection()
    {
        var (tableView, items) = await LoadAsync();

        // Select rows 1, 3, 5, ..., 19 — ten disjoint length-1 ranges with gaps between them.
        for (var i = 1; i < 20; i += 2)
        {
            tableView.SelectRange(new ItemIndexRange(i, 1));
        }

        var values = tableView.SelectedValues.Cast<ValueItem>().ToList();

        Assert.AreEqual(10, values.Count);
        CollectionAssert.AreEqual(
            Enumerable.Range(0, 10).Select(i => items[i * 2 + 1]).ToList(),
            values);

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task SelectedValues_MixedRangesAndGaps_KeepsRowOrder()
    {
        var (tableView, items) = await LoadAsync();

        // A contiguous block, a gap, a single row, a gap, another block — selected out of order.
        tableView.SelectRange(new ItemIndexRange(14, 3)); // 14,15,16
        tableView.SelectRange(new ItemIndexRange(2, 3));  // 2,3,4
        tableView.SelectRange(new ItemIndexRange(8, 1));  // 8

        var values = tableView.SelectedValues.Cast<ValueItem>().ToList();

        CollectionAssert.AreEqual(
            new[] { items[2], items[3], items[4], items[8], items[14], items[15], items[16] },
            values);

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task SelectedValues_CellsAndRowsMixed_DeduplicatesRows()
    {
        var (tableView, items) = await LoadAsync();

        tableView.SelectRange(new ItemIndexRange(3, 2)); // rows 3,4 selected as rows

        // Cells in rows 4 (overlaps the row selection) and 9 (does not).
        tableView.SelectedCells = [new TableViewCellSlot(4, 0), new TableViewCellSlot(9, 1)];

        var values = tableView.SelectedValues.Cast<ValueItem>().ToList();

        CollectionAssert.AreEqual(new[] { items[3], items[4], items[9] }, values);

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task SelectedCellValues_ReturnsBoundValuesInRowColumnOrder()
    {
        var (tableView, items) = await LoadAsync();

        tableView.SelectedCells = [new TableViewCellSlot(5, 1), new TableViewCellSlot(2, 0), new TableViewCellSlot(2, 1)];

        var values = tableView.SelectedCellValues.ToList();

        // Row 2 (Name, Value), then row 5 (Value).
        CollectionAssert.AreEqual(new object?[] { items[2].Name, items[2].Value, items[5].Value }, values);

        await UnloadAsync(tableView);
    }

    private static async Task<(TableView TableView, ObservableCollection<ValueItem> Items)> LoadAsync()
    {
        var items = new ObservableCollection<ValueItem>(
            Enumerable.Range(0, 20).Select(i => new ValueItem { Name = $"Item {i}", Value = i }));

        var tableView = new TableView
        {
            AutoGenerateColumns = false,
            SelectionMode = ListViewSelectionMode.Extended,
            Width = 600,
            Height = 400,
        };

        tableView.Columns.Add(new TableViewTextColumn
        {
            Header = "Name",
            Binding = new Binding { Path = new PropertyPath(nameof(ValueItem.Name)) },
        });
        tableView.Columns.Add(new TableViewNumberColumn
        {
            Header = "Value",
            Binding = new Binding { Path = new PropertyPath(nameof(ValueItem.Value)) },
        });

        tableView.ItemsSource = items;

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(tableView);

        return (tableView, items);
    }

    private static async Task UnloadAsync(TableView tableView)
        => await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);

    private sealed class ValueItem
    {
        public string Name { get; set; } = string.Empty;
        public double Value { get; set; }
    }
}
