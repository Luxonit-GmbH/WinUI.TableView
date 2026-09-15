using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

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
