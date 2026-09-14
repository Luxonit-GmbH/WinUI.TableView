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
    // 1200px viewport over 100px columns: 12 visible, i.e. indices [0, 11].
    //
    // Both knobs are viewport multiples capped in columns, and the cap is what bites here: the band reaches 4
    // columns past the visible window and the prefetch margin 8, so the band is ~[0, 15] and the margin ~[0, 19].
    // (Before the cap existed these were pixel measures alone — a 1-viewport margin over 100px columns reached to
    // column 30, which on a grid of many narrow columns meant prefetching most of the grid and calling it
    // virtualization.) Column 18 is squarely in the margin and outside the band; 70 is far beyond both.
    private const int PrefetchedColumn = 18;
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

        // Scroll so that the prefetched column is on screen, then let the band realize settle.
        tableView.SetValue(TableView.HorizontalOffsetProperty, PrefetchedColumn * 100d - 200d);
        tableView.UpdateLayout();
        await Task.Delay(300);
        tableView.UpdateLayout();

        Assert.AreEqual(Visibility.Visible, cell.Visibility, "the cell should now be in the band and visible");
        Assert.AreSame(created, cell.Content, "the element created at idle is the one shown; nothing was regenerated");

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);
    }

    /// <summary>
    /// Realization has to be two-way. Without this, content is generated once and kept for the life of the cell, so
    /// one sweep across the columns leaves every realized row holding a built, bound element for every column —
    /// only the measure stays virtualized, and a live feed goes on ticking columns nobody can see.
    /// </summary>
    [UITestMethod]
    public async Task ScrollingWellPastAColumn_ReleasesItsContent()
    {
        var tableView = await LoadAsync(prefetchLength: 1);
        var row = tableView.Rows.First();
        var cell = CellFor(row, tableView.Columns[0]);

        Assert.IsNotNull(cell.Content, "column 0 starts in the band, so it must have content");

        // Far enough that column 0 is outside the band, outside the prefetch margin and outside the release
        // hysteresis beyond it.
        tableView.SetValue(TableView.HorizontalOffsetProperty, 4_000d);
        tableView.UpdateLayout();
        await Task.Delay(300);
        tableView.UpdateLayout();

        Assert.IsNull(cell.Content, "a column left far behind must have its content released, not merely collapsed");

        // ...and it comes back when scrolled to, which is what makes releasing it safe.
        tableView.SetValue(TableView.HorizontalOffsetProperty, 0d);
        tableView.UpdateLayout();
        await Task.Delay(300);
        tableView.UpdateLayout();

        Assert.IsNotNull(cell.Content, "scrolling back must rebuild the released content");
        Assert.AreEqual(Visibility.Visible, cell.Visibility);

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);
    }

    /// <summary>
    /// A band that moves by one column must touch the columns that crossed an edge, not every column of every row.
    /// The old walk visited all of them on every settle, which at eighty columns and thirty rows was a few thousand
    /// lookups to change a handful of flags.
    /// </summary>
    [UITestMethod]
    public async Task ShiftingTheBandByOneColumn_TouchesOnlyTheColumnsThatCrossedAnEdge()
    {
        var tableView = await LoadAsync(prefetchLength: 0);
        var rowCount = tableView.Rows.Count;
        Assert.IsTrue(rowCount > 0, "the grid must have realized some rows for this to mean anything");

        var before = tableView.ColumnBandCellVisits;

        // One column's width, so the band shifts by one at each edge.
        tableView.SetValue(TableView.HorizontalOffsetProperty, 100d);
        tableView.UpdateLayout();
        await Task.Delay(300);
        tableView.UpdateLayout();

        var visits = tableView.ColumnBandCellVisits - before;

        Assert.IsTrue(visits > 0, "the band really did move, so some cells must have been re-flagged");
        Assert.IsTrue(
            visits <= rowCount * 8,
            $"a one-column shift visited {visits} cells across {rowCount} rows; a delta walk should be a few per row, not all 80");

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
