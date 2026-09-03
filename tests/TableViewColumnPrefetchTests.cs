using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace WinUI.TableView.Tests;

/// <summary>
/// Idle prefetch: columns just outside the realized band get their content created while the thread is idle, so
/// the first scroll into them reveals content rather than generating it. The invariant that matters as much as
/// the creation is what it must NOT do — show anything. Prefetched cells stay collapsed.
/// </summary>
[TestClass]
public class TableViewColumnPrefetchTests
{
    // 1200px viewport over 100px columns: 12 visible, the 0.5-viewport cache on each side makes the band ~[0, 18],
    // and a 1-viewport prefetch margin reaches to ~30. Column 24 is squarely in the margin; 70 is far beyond it.
    private const int PrefetchedColumn = 24;
    private const int FarColumn = 70;

    [UITestMethod]
    public async Task IdlePrefetch_CreatesContentJustOutsideTheBand_AndLeavesItCollapsed()
    {
        var tableView = await LoadAsync(prefetchLength: 1);
        var row = tableView.Rows.First();

        var prefetched = CellFor(row, tableView.Columns[PrefetchedColumn]);
        var far = CellFor(row, tableView.Columns[FarColumn]);

        Assert.IsNotNull(prefetched.Content, "a column just outside the band should have had its content created at idle");
        Assert.AreEqual(Visibility.Collapsed, prefetched.Visibility, "prefetched content must stay collapsed until it scrolls in");
        Assert.IsNull(far.Content, "a column beyond the prefetch margin must not be created");

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);
    }

    [UITestMethod]
    public async Task IdlePrefetch_Off_CreatesNothingOutsideTheBand()
    {
        var tableView = await LoadAsync(prefetchLength: 0);
        var row = tableView.Rows.First();

        Assert.IsNull(CellFor(row, tableView.Columns[PrefetchedColumn]).Content,
            "with ColumnPrefetchLength 0 nothing outside the band may be created");

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);
    }

    [UITestMethod]
    public async Task IdlePrefetch_ScrollingIntoThePrefetchedMargin_ShowsIt_WithoutCreatingIt()
    {
        var tableView = await LoadAsync(prefetchLength: 1);
        var row = tableView.Rows.First();
        var cell = CellFor(row, tableView.Columns[PrefetchedColumn]);
        var created = cell.Content;
        Assert.IsNotNull(created);

        // Scroll so that column 24 is on screen, then let the band realize settle.
        tableView.SetValue(TableView.HorizontalOffsetProperty, PrefetchedColumn * 100d - 200d);
        tableView.UpdateLayout();
        await Task.Delay(300);
        tableView.UpdateLayout();

        Assert.AreEqual(Visibility.Visible, cell.Visibility, "the cell should now be in the band and visible");
        Assert.AreSame(created, cell.Content, "the element created at idle is the one shown; nothing was regenerated");

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);
    }

    private static TableViewCell CellFor(TableViewRow row, TableViewColumn column)
        => row.Cells.First(cell => cell.Column == column);

    private static async Task<TableView> LoadAsync(double prefetchLength)
    {
        var tableView = new TableView
        {
            AutoGenerateColumns = false,
            IsColumnVirtualizationEnabled = true,
            ColumnPrefetchLength = prefetchLength,
            RowHeight = 32,
            Width = 1200,
            Height = 400,
        };

        for (var i = 0; i < 80; i++)
        {
            tableView.Columns.Add(new TableViewTextColumn
            {
                Header = $"C{i}",
                Width = new GridLength(100),
                Binding = new Binding { Path = new PropertyPath(nameof(Item.Name)) },
            });
        }

        tableView.ItemsSource = new ObservableCollection<Item>(Enumerable.Range(0, 100).Select(i => new Item { Name = $"Item {i}" }));

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(tableView);
        tableView.UpdateLayout();

        await Task.Delay(300); // the debounced band realize
        tableView.UpdateLayout();
        await Task.Delay(700); // idle time for the prefetch pump
        tableView.UpdateLayout();

        return tableView;
    }

    private sealed class Item
    {
        public string Name { get; set; } = string.Empty;
    }
}
