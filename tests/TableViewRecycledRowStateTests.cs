using Microsoft.UI;
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
/// What a container has to get right when it is recycled onto a different item.
/// </summary>
/// <remarks>
/// These guard an ordering bug rather than a calculation. <c>ClearContainerForItemOverride</c> nulls the row's
/// back-reference to the grid, and for a long time <c>PrepareContainerForItemOverride</c> restored it only after the
/// base call — which is what raises <c>OnContentChanged</c>. So on every recycle that handler ran against a null
/// grid and quietly skipped the cell realize, the cell-set self-heal and the alternate-row colouring.
/// </remarks>
[TestClass]
public class TableViewRecycledRowStateTests
{
    [UITestMethod]
    public async Task RecycledRow_HasItsTableViewBackReference_WhenContentChanges()
    {
        var tableView = await LoadAsync();

        await ScrollToAsync(tableView, 400);

        foreach (var row in tableView.Rows)
        {
            Assert.AreSame(tableView, row.TableView, "a realized row must know its grid");
        }

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);
    }

    [UITestMethod]
    public async Task RecycledRow_ReappliesAlternateRowColors()
    {
        var alternate = new SolidColorBrush(Colors.LightGray);
        var tableView = await LoadAsync();
        tableView.AlternateRowBackground = alternate;
        tableView.UpdateLayout();
        await Task.Delay(150);

        await ScrollToAsync(tableView, 400);

        // Odd-indexed rows carry the alternate brush, even-indexed ones carry the row's own background instead.
        // Before the ordering fix a recycled row kept whatever the previous item had left on it, so a row that
        // changed parity while off-screen came back with the wrong colour.
        var checkedAny = false;

        foreach (var row in tableView.Rows.Where(r => r.RowPresenter is not null))
        {
            checkedAny = true;

            if (row.Index % 2 == 1)
            {
                Assert.AreSame(alternate, row.RowPresenter!.Background,
                    $"odd row {row.Index} lost its alternate background after recycling");
            }
            else
            {
                Assert.AreNotSame(alternate, row.RowPresenter!.Background,
                    $"even row {row.Index} kept an alternate background after recycling");
            }
        }

        Assert.IsTrue(checkedAny, "no realized rows to check");

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);
    }

    /// <summary>
    /// A vertical jump of more than a viewport is a throw: the rows it recycles are held blank — their cell panels
    /// at zero opacity, their cells still bound to the item they showed — and bound to their new item once the
    /// scroll settles. A jump of less than a viewport is an ordinary scroll and binds at once.
    /// </summary>
    [UITestMethod]
    public async Task ThrowingTheScrollbar_HoldsRecycledRowsBlank_ThenBindsThemWhenItSettles()
    {
        var tableView = await LoadAsync();
        var scrollViewer = tableView.FindDescendant<ScrollViewer>(x => x.Name == "ScrollViewer")!;
        var deferredBefore = tableView.RowsDeferred;

        // Ten viewports down. The new offset lands on the next frame, and the recycles with it, so wait for them.
        scrollViewer.ChangeView(null, 4000d, null, disableAnimation: true);

        for (var waited = 0; tableView.RowsDeferred == deferredBefore && waited < 300; waited += 5)
        {
            await Task.Delay(5);
            tableView.UpdateLayout();
        }

        Assert.IsTrue(tableView.RowsDeferred > deferredBefore, "a jump of ten viewports must defer the rows it recycles");

        // While held: the first visible column stays live and shows the row's new item, so the user can tell
        // where the throw has taken them; the other cells are hidden. Bindings settle on the next tick, and the
        // platform's phased rendering releases held rows once the gesture has been quiet for 50 ms, so the held
        // state is checked a tick later, on whatever rows are still held then.
        await Task.Delay(20);
        tableView.UpdateLayout();

        foreach (var row in tableView.Rows.Where(r => r.RowPresenter?.AreCellsDeferred is true))
        {
            var live = row.Cells[0];
            var hidden = row.Cells[1];
            Assert.AreEqual(1d, live.Opacity, $"row {row.Index}: the live column must stay visible while held");
            Assert.AreEqual(((Item)row.Content).Name, TextOf(live), $"row {row.Index}: the live column must show the new item while held");
            Assert.AreEqual(0d, hidden.Opacity, $"row {row.Index}: a held cell must be hidden");
        }

        await Task.Delay(600);
        tableView.UpdateLayout();

        foreach (var row in tableView.Rows)
        {
            Assert.IsFalse(row.RowPresenter?.AreCellsDeferred is true, $"row {row.Index} is still held after the scroll settled");

            foreach (var cell in row.Cells.Where(c => c.Visibility == Visibility.Visible))
            {
                Assert.AreEqual(1d, cell.Opacity, $"row {row.Index} settled but a cell is still hidden");
                Assert.AreEqual(((Item)row.Content).Name, TextOf(cell), $"row {row.Index} shows another item's value after the throw settled");
            }
        }

        // Less than a viewport: an ordinary scroll, bound at once.
        deferredBefore = tableView.RowsDeferred;
        scrollViewer.ChangeView(null, 4200d, null, disableAnimation: true);
        await Task.Delay(100);
        tableView.UpdateLayout();

        Assert.AreEqual(deferredBefore, tableView.RowsDeferred, "a scroll of less than a viewport must not defer anything");

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);
    }

    /// <summary>
    /// A column flagged <see cref="TableViewColumn.KeepLiveDuringFastScroll"/> stays bound and visible on held
    /// rows wherever it sits, so an identifier column can be kept in view through a throw.
    /// </summary>
    [UITestMethod]
    public async Task ThrowingTheScrollbar_KeepsAFlaggedColumnLive()
    {
        var tableView = await LoadAsync();
        tableView.FastScrollLiveColumnCount = 0;
        tableView.Columns[3].KeepLiveDuringFastScroll = true;
        var scrollViewer = tableView.FindDescendant<ScrollViewer>(x => x.Name == "ScrollViewer")!;
        var deferredBefore = tableView.RowsDeferred;

        scrollViewer.ChangeView(null, 4000d, null, disableAnimation: true);

        for (var waited = 0; tableView.RowsDeferred == deferredBefore && waited < 300; waited += 5)
        {
            await Task.Delay(5);
            tableView.UpdateLayout();
        }

        Assert.IsTrue(tableView.RowsDeferred > deferredBefore, "the jump must defer the rows it recycles");

        await Task.Delay(20);
        tableView.UpdateLayout();

        foreach (var row in tableView.Rows.Where(r => r.RowPresenter?.AreCellsDeferred is true))
        {
            Assert.AreEqual(1d, row.Cells[3].Opacity, $"row {row.Index}: the flagged column must stay visible while held");
            Assert.AreEqual(((Item)row.Content).Name, TextOf(row.Cells[3]), $"row {row.Index}: the flagged column must show the new item while held");
            Assert.AreEqual(0d, row.Cells[0].Opacity, $"row {row.Index}: with no count, the first column is held like the rest");
        }

        await Task.Delay(600);
        tableView.UpdateLayout();

        foreach (var row in tableView.Rows)
        {
            Assert.IsFalse(row.RowPresenter?.AreCellsDeferred is true, $"row {row.Index} is still held after the scroll settled");
            Assert.AreEqual(((Item)row.Content).Name, TextOf(row.Cells[3]));
        }

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);
    }

    private static string? TextOf(TableViewCell cell)
    {
        var content = cell.Content as FrameworkElement;
        return (content as TextBlock ?? content?.FindDescendant<TextBlock>())?.Text;
    }

    private static async Task ScrollToAsync(TableView tableView, int index)
    {
        tableView.ScrollIntoView(tableView.Items[index]);
        tableView.UpdateLayout();
        await Task.Delay(300);
        tableView.UpdateLayout();
    }

    private static async Task<TableView> LoadAsync()
    {
        var tableView = new TableView
        {
            AutoGenerateColumns = false,
            IsColumnVirtualizationEnabled = true,
            RowHeight = 32,
            Width = 600,
            Height = 400,
        };

        for (var i = 0; i < 12; i++)
        {
            tableView.Columns.Add(new TableViewTextColumn
            {
                Header = $"C{i}",
                Width = new GridLength(100),
                Binding = new Binding { Path = new PropertyPath(nameof(Item.Name)) },
            });
        }

        tableView.ItemsSource = new ObservableCollection<Item>(
            Enumerable.Range(0, 1000).Select(i => new Item { Name = $"Item {i}" }));

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
