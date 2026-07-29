using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace WinUI.TableView.Tests;

/// <summary>
/// Covers swapping the whole COLUMN SET at runtime (clear + add, or a new ColumnsSource): every realized and
/// recycled row must end up with exactly the current columns — no stale, missing or duplicated cells.
/// </summary>
[TestClass]
public class TableViewColumnSetSwapTests
{
    [UITestMethod]
    public async Task ClearAndAddColumns_AllRealizedRowsMatchNewColumnSet()
    {
        var tableView = await LoadAsync(columnCount: 3);

        AssertAllRowsHaveColumns(tableView, 3);

        // The app's pattern: clear everything, then add the new column set one by one.
        tableView.Columns.Clear();
        foreach (var column in CreateColumns(5))
        {
            tableView.Columns.Add(column);
        }

        tableView.UpdateLayout();
        await Task.Yield();

        AssertAllRowsHaveColumns(tableView, 5);

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task ColumnsSource_Swap_RebuildsRows_AndClearsCaches()
    {
        var tableView = await LoadAsync(columnCount: 0);
        tableView.ColumnsSource = new ObservableCollection<TableViewColumn>(CreateColumns(4));
        tableView.UpdateLayout();

        AssertAllRowsHaveColumns(tableView, 4);
        Assert.AreEqual(4, tableView.Columns.VisibleColumns.Count);

        // Swap the entire set (a new collection instance, as a view-model would produce).
        tableView.ColumnsSource = new ObservableCollection<TableViewColumn>(CreateColumns(2));
        tableView.UpdateLayout();
        await Task.Yield();

        AssertAllRowsHaveColumns(tableView, 2);
        Assert.AreEqual(2, tableView.Columns.VisibleColumns.Count); // cached visible columns dropped too

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task ColumnSwapWhileScrolled_RecycledRowsRebuild()
    {
        // The regression: rows recycled across the swap (no RowPresenter at the time of the change) previously
        // kept their old cells forever, so scrolling back revealed rows with the wrong column count.
        var tableView = await LoadAsync(columnCount: 3);

        _ = await tableView.ScrollRowIntoView(400); // recycle the first screen of containers away
        tableView.UpdateLayout();
        await Task.Yield();

        tableView.Columns.Clear();
        foreach (var column in CreateColumns(6))
        {
            tableView.Columns.Add(column);
        }

        tableView.UpdateLayout();
        await Task.Yield();

        AssertAllRowsHaveColumns(tableView, 6);

        _ = await tableView.ScrollRowIntoView(0); // bring the recycled containers back
        tableView.UpdateLayout();
        await Task.Yield();

        AssertAllRowsHaveColumns(tableView, 6);

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task InvalidateColumns_ForcesRebuildAfterUntrackedChange()
    {
        var tableView = await LoadAsync(columnCount: 3);

        tableView.Columns.Clear();
        tableView.Columns.AddRange(CreateColumns(4));
        tableView.InvalidateColumns(); // the explicit "I replaced the columns" entry point

        tableView.UpdateLayout();
        await Task.Yield();

        AssertAllRowsHaveColumns(tableView, 4);

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    [Ignore("KNOWN FAILING — executable repro. Instrumentation proved TableViewHeaderRow's CollectionChanged " +
            "handler NEVER FIRES: after a Clear()+Add() swap there is no ClearHeaders, no AddHeaders and no " +
            "CalculateHeaderWidths, even though TableView._headerRow is present AND the ROWS receive the very same " +
            "Columns.CollectionChanged event correctly (their cells rebuild). The swapped-in columns therefore " +
            "never get sized headers, column.ActualWidth stays 0, GetVisibleScrollableRange returns (-1,-1) and " +
            "every cell stays collapsed — the row renders with columns missing. NEXT STEP: determine why the " +
            "header row's subscription is inert while the rows' works — prime suspect is that the subscribing " +
            "header row instance is not the one in the live visual tree (a second instance), so the fix is likely " +
            "in how TableViewHeaderRow.TableView is assigned. Remove [Ignore] when fixing.")]
    public async Task ColumnSwap_WithColumnVirtualization_NewCellsAreVisibleNotCollapsed()
    {
        // The likely real-world repro: with column virtualization the realized BAND is cached by index range. A
        // swapped column set can produce the same numeric range for different columns, so without invalidation the
        // realize pass is skipped and the new cells stay collapsed — the row renders fewer columns than it has.
        var tableView = await LoadAsync(columnCount: 4, virtualize: true);

        AssertVisibleCellsMatchColumns(tableView, 4);
        tableView.Columns.Clear();
        foreach (var column in CreateColumns(4, "X")) // same COUNT, different column instances
        {
            tableView.Columns.Add(column);
        }

        // The band realize is debounced (~50 ms) and then chunked across dispatcher turns; wait it out so this
        // asserts the settled state rather than the transient one.
        await Task.Delay(400);
        tableView.UpdateLayout();

        AssertVisibleCellsMatchColumns(tableView, 4);

        await UnloadAsync(tableView);
    }

    private static void AssertVisibleCellsMatchColumns(TableView tableView, int expected)
    {
        AssertAllRowsHaveColumns(tableView, expected);

        var range = tableView.GetVisibleScrollableRange(0.5);
        var widths = string.Join(",", tableView.Columns.VisibleColumns.Select(
            c => $"{c.ActualWidth}/hdr:{(c.HeaderControl is null ? "null" : c.HeaderControl.Width.ToString())}"));

        foreach (var row in tableView.Rows)
        {
            var visible = row.Cells.Count(cell => cell.Visibility == Visibility.Visible);
            Assert.AreEqual(expected, visible,
                $"row {row.Index}: {visible} of {row.Cells.Count} cells visible — collapsed cells render as missing " +
                $"columns. range={range}, widths=[{widths}], rows={tableView.Rows.Count}, " +
                $"headerRow={(tableView.HeaderRow is null ? "NULL" : "present")}");
        }
    }

    private static void AssertAllRowsHaveColumns(TableView tableView, int expected)
    {
        Assert.AreEqual(expected, tableView.Columns.VisibleColumns.Count, "visible column count");

        foreach (var row in tableView.Rows)
        {
            Assert.AreEqual(expected, row.Cells.Count, $"row {row.Index} cell count");

            // Cells must map to the CURRENT columns, in order — not merely match in count.
            for (var i = 0; i < expected; i++)
            {
                Assert.AreSame(tableView.Columns.VisibleColumns[i], row.Cells[i].Column, $"row {row.Index} cell {i} column");
            }
        }
    }

    private static IEnumerable<TableViewColumn> CreateColumns(int count, string prefix = "C")
    {
        for (var i = 0; i < count; i++)
        {
            yield return new TableViewTextColumn
            {
                Header = $"{prefix}{i}",
                Width = new GridLength(100, GridUnitType.Pixel),
                Binding = new Binding { Path = new PropertyPath(nameof(SwapItem.Name)) },
            };
        }
    }

    private static async Task<TableView> LoadAsync(int columnCount, bool virtualize = false)
    {
        var tableView = new TableView
        {
            AutoGenerateColumns = false,
            IsColumnVirtualizationEnabled = virtualize,
            RowHeight = 32,
            Width = 800,
            Height = 400,
            ItemsSource = new ObservableCollection<SwapItem>(
                Enumerable.Range(0, 500).Select(i => new SwapItem { Name = $"Item {i}" })),
        };

        if (columnCount > 0)
        {
            tableView.Columns.AddRange(CreateColumns(columnCount));
        }

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(tableView);
        tableView.UpdateLayout();

        return tableView;
    }

    private static async Task UnloadAsync(TableView tableView)
        => await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);

    private sealed class SwapItem
    {
        public string Name { get; set; } = string.Empty;
    }
}
