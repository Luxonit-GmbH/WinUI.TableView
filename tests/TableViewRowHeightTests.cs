using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace WinUI.TableView.Tests;

/// <summary>
/// RowHeight, RowMinHeight and RowMaxHeight map onto Height, MinHeight and MaxHeight, and in WinUI the minimum
/// wins over the height. With the default minimum of 40, a grid that asked for 28px rows got 40px ones, while the
/// code that reasons about the row height (the visible row range, the cells' content constraint, the page size)
/// took the 28 at its word. An explicit RowHeight is the more specific setting, so it caps the minimum.
/// </summary>
[TestClass]
public class TableViewRowHeightTests
{
    [UITestMethod]
    public async Task RowHeight_BelowTheDefaultMinimum_IsTheHeightTheCellsGet()
    {
        var tableView = await LoadAsync(rowHeight: 28);
        var row = tableView.Rows.First();
        var cell = row.Cells.First();

        Assert.AreEqual(40d, tableView.RowMinHeight, "the default minimum is the premise of this test");
        Assert.AreEqual(28d, cell.ActualHeight, 0.5, "an explicit RowHeight below the default minimum must be the height the cells get");
        Assert.IsTrue(row.ActualHeight < 40d, $"the row itself must not be held at the default minimum (it is {row.ActualHeight})");

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);
    }

    [UITestMethod]
    public async Task NoRowHeight_KeepsTheMinimum()
    {
        var tableView = await LoadAsync(rowHeight: double.NaN);
        var cell = tableView.Rows.First().Cells.First();

        Assert.AreEqual(40d, cell.ActualHeight, 0.5, "without a RowHeight the default minimum still sets the row height");

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);
    }

    [UITestMethod]
    public async Task ChangingRowHeight_ReappliesTheMinimumToExistingCells()
    {
        var tableView = await LoadAsync(rowHeight: double.NaN);
        var cell = tableView.Rows.First().Cells.First();
        Assert.AreEqual(40d, cell.ActualHeight, 0.5);

        tableView.RowHeight = 24;
        tableView.UpdateLayout();
        await Task.Delay(100);
        tableView.UpdateLayout();

        Assert.AreEqual(24d, cell.ActualHeight, 0.5, "a RowHeight set after the cells exist must cap their minimum too");

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);
    }

    private static async Task<TableView> LoadAsync(double rowHeight)
    {
        var tableView = new TableView
        {
            AutoGenerateColumns = false,
            RowHeight = rowHeight,
            Width = 600,
            Height = 400,
        };

        for (var i = 0; i < 4; i++)
        {
            tableView.Columns.Add(new TableViewTextColumn
            {
                Header = $"C{i}",
                Width = new GridLength(100),
                Binding = new Binding { Path = new PropertyPath(nameof(Item.Name)) },
            });
        }

        tableView.ItemsSource = new ObservableCollection<Item>(
            Enumerable.Range(0, 100).Select(i => new Item { Name = $"Item {i}" }));

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(tableView);
        tableView.UpdateLayout();
        await Task.Delay(300);
        tableView.UpdateLayout();

        return tableView;
    }

    private sealed class Item
    {
        public string Name { get; set; } = string.Empty;
    }
}
