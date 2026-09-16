using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using WinUI.TableView.Extensions;

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
    /// Prefetch has to build the cell, not only its content. A collapsed cell is never measured, so layout never
    /// applies its template; without it there is no content presenter to constrain the content against, and the
    /// scroll that reveals the cell instantiates the template and measures the content again under the real width
    /// — on the scroll path, which is the cost prefetch exists to move off it.
    /// </summary>
    [UITestMethod]
    public async Task IdlePrefetch_AppliesTheCellsOwnTemplate_WhileItStaysCollapsed()
    {
        var tableView = await LoadAsync(prefetchLength: 1);
        var row = tableView.Rows.First();

        var prefetched = CellFor(row, tableView.Columns[PrefetchedColumn]);
        var far = CellFor(row, tableView.Columns[FarColumn]);

        Assert.IsNotNull(prefetched.Content);
        Assert.AreEqual(Visibility.Collapsed, prefetched.Visibility);
        Assert.IsTrue(VisualTreeHelper.GetChildrenCount(prefetched) > 0, "a prefetched cell must have its own template applied, so the reveal has nothing left to build");
        Assert.AreEqual(0, VisualTreeHelper.GetChildrenCount(far), "a cell beyond the prefetch margin must not have been built");
        Assert.IsTrue(tableView.CellTemplatesPrefetched > 0, "the pump must report the templates it applied");

        var content = prefetched.Content;

        tableView.SetValue(TableView.HorizontalOffsetProperty, PrefetchedColumn * 100d - 200d);
        tableView.UpdateLayout();
        await Task.Delay(300);
        tableView.UpdateLayout();

        Assert.AreEqual(Visibility.Visible, prefetched.Visibility);
        Assert.AreSame(content, prefetched.Content, "the reveal shows what was built at idle; nothing is regenerated");
        Assert.AreEqual(0, VisualTreeHelper.GetChildrenCount(far), "the far column is still beyond the margin, so it is still not built");

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

    /// <summary>
    /// The band is applied with hysteresis: it moves only once the viewport has used it up. A scroll that leaves the
    /// visible columns inside the band with a column to spare must touch no row at all — no cell flags, no cells
    /// panel measures — and the scroll that reaches the band's edge moves it for every visible row. Dirtying every
    /// visible row on every column crossing is what made a fullscreen drag lag.
    /// </summary>
    [UITestMethod]
    public async Task ScrollingInsideTheRevealedBand_TouchesNoRow_AndReachingItsEdgeMovesIt()
    {
        var tableView = await LoadAsync(prefetchLength: 0);

        // Settled at offset 0: visible [0, 11], band [0, 15].
        var visitsBefore = tableView.ColumnBandCellVisits;
        var panelMeasuresBefore = tableView.CellsPanelMeasures;

        // One column in: visible [1, 13], two columns short of the band's edge — inside the guard.
        tableView.SetValue(TableView.HorizontalOffsetProperty, 100d);
        tableView.UpdateLayout();

        Assert.AreEqual(0, tableView.ColumnBandCellVisits - visitsBefore, "a scroll that stays inside the band must not flag a cell");
        Assert.AreEqual(0, tableView.CellsPanelMeasures - panelMeasuresBefore, "a scroll that stays inside the band must not dirty a row");

        // Three columns in: the visible range comes within the guard of the band's edge, so the band moves. The
        // move is spread over ticks — a first slice of rows now, the rest as the drag goes on or when it settles —
        // and no row is left showing a collapsed column meanwhile, which the one-pixel test below pins.
        tableView.SetValue(TableView.HorizontalOffsetProperty, 300d);
        tableView.UpdateLayout();

        Assert.IsTrue(tableView.ColumnBandCellVisits - visitsBefore > 0, "reaching the band's edge must move it");
        Assert.IsTrue(tableView.CellsPanelMeasures - panelMeasuresBefore > 0, "the band move must dirty the rows it revealed");

        // Settled: every realized row ends up on the same band, and it reaches past the columns now in view. The
        // settle pass is chunked across dispatcher turns and starts 100 ms after the last tick, so wait for it
        // rather than assuming a fixed delay covers it on a loaded host.
        var settled = (-2, -2);

        for (var waited = 0; waited < 2000; waited += 50)
        {
            await Task.Delay(50);
            tableView.UpdateLayout();
            settled = tableView.Rows.First().AppliedBand;

            if (settled.Item1 >= 0 && tableView.Rows.All(r => r.AppliedBand == settled))
            {
                break;
            }
        }

        Assert.IsTrue(settled.Item2 >= 16, $"the settled band {settled} should reach past the columns in view");
        Assert.IsTrue(tableView.Rows.All(r => r.AppliedBand == settled), "every realized row should settle on the same band");

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);
    }

    /// <summary>
    /// The other half of the contract: hysteresis must never let a column arrive in the viewport collapsed. One
    /// pixel at a time across two band moves, every column that is even partly visible has a visible cell.
    /// </summary>
    [UITestMethod]
    public async Task ScrollingOnePixelAtATime_NeverShowsACollapsedColumn()
    {
        var tableView = await LoadAsync(prefetchLength: 0);
        var row = tableView.Rows.First();

        for (var offset = 1d; offset <= 900d; offset += 1d)
        {
            tableView.SetValue(TableView.HorizontalOffsetProperty, offset);
            tableView.UpdateLayout();

            var (first, last) = tableView.GetVisibleScrollableRange(0);

            for (var column = first; column <= last; column++)
            {
                var cell = CellFor(row, tableView.Columns.VisibleScrollableColumns[column]);
                Assert.AreEqual(Visibility.Visible, cell.Visibility, $"column {column} is in the viewport at offset {offset} but its cell is collapsed");
            }
        }

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);
    }

    /// <summary>
    /// A prefetched cell has its content's DataContext pinned to the item it was built under, and revealing it
    /// under the same item leaves the pin in place. The row must then be able to recycle onto a different item
    /// without that cell going on showing the old one: whatever pins for the collapsed state has to be undone by
    /// the time the row shows another item.
    /// </summary>
    [UITestMethod]
    public async Task PrefetchedThenRevealedCell_FollowsTheRowOntoANewItem()
    {
        var tableView = await LoadAsync(prefetchLength: 1);

        // Reveal the prefetched column, so it is in band while still pinned to the item it was built under.
        tableView.SetValue(TableView.HorizontalOffsetProperty, PrefetchedColumn * 100d - 200d);
        tableView.UpdateLayout();
        await Task.Delay(300);
        tableView.UpdateLayout();

        var row = tableView.Rows.First();
        var cell = CellFor(row, tableView.Columns[PrefetchedColumn]);
        var before = (Item)row.Content;
        Assert.AreEqual(Visibility.Visible, cell.Visibility);
        Assert.AreEqual(before.Name, TextOf(cell), "the revealed cell shows the item it was prefetched under");

        // Recycle every container onto distant items.
        tableView.ScrollIntoView(tableView.Items[60]);
        tableView.UpdateLayout();
        await Task.Delay(300);
        tableView.UpdateLayout();

        var checkedAny = false;

        foreach (var realized in tableView.Rows)
        {
            if (realized.Content is not Item item || ReferenceEquals(item, before))
            {
                continue;
            }

            var recycled = CellFor(realized, tableView.Columns[PrefetchedColumn]);

            if (recycled.Visibility is not Visibility.Visible)
            {
                continue;
            }

            checkedAny = true;
            Assert.AreEqual(item.Name, TextOf(recycled), $"row {realized.Index} shows another item's value in a column that was prefetched, then revealed, then recycled");
        }

        Assert.IsTrue(checkedAny, "no recycled row with the column visible was found to check");

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);
    }

    private static string? TextOf(TableViewCell cell)
    {
        var content = cell.Content as FrameworkElement;
        var textBlock = content as TextBlock ?? content?.FindDescendant<TextBlock>();
        return textBlock?.Text;
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

        // Idle time for the prefetch pump. It waits out the debounce after the last recycle and then works in
        // small low-priority increments, so how long it needs depends on how loaded the host is; wait for the
        // column the tests look at rather than a fixed time.
        if (prefetchLength > 0)
        {
            var row = tableView.Rows.First();
            var column = tableView.Columns[PrefetchedColumn];

            for (var waited = 0; CellFor(row, column).Content is null && waited < 4000; waited += 50)
            {
                await Task.Delay(50);
            }
        }
        else
        {
            await Task.Delay(700);
        }

        tableView.UpdateLayout();

        return tableView;
    }

    private sealed class Item
    {
        public string Name { get; set; } = string.Empty;
    }
}
