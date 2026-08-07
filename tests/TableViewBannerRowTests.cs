using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using WinUI.TableView.Extensions;

namespace WinUI.TableView.Tests;

/// <summary>
/// Covers banner rows: items that occupy an index but render as one full-width piece of content instead of
/// cells, and are not data — so selection must step around them.
/// </summary>
[TestClass]
public class TableViewBannerRowTests
{
    [UITestMethod]
    public async Task BannerRow_RendersFullWidthContent_InsteadOfCells()
    {
        var tableView = await CreateAsync();

        var bannerRow = RowAt(tableView, 0);
        var dataRow = RowAt(tableView, 1);

        Assert.IsTrue(IsShowingBanner(bannerRow), "the group header renders as a banner");
        Assert.IsFalse(IsShowingBanner(dataRow), "an ordinary row still renders cells");

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task BannerRow_HidesTheCellLayout()
    {
        var tableView = await CreateAsync();

        var rootPanel = RowAt(tableView, 0)!
            .FindDescendants()
            .OfType<FrameworkElement>()
            .First(element => element.Name == "RootPanel");

        // Replaced, not overlaid — the cells panel is hand-arranged, so leaving it visible draws it over.
        Assert.AreEqual(Visibility.Collapsed, rootPanel.Visibility);

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task BannerRow_IsNotSelectable()
    {
        var tableView = await CreateAsync();

        tableView.MakeSelection(new TableViewCellSlot(0, 0), shiftKey: false); // the banner
        await Task.Yield();

        Assert.AreEqual(0, SelectedRows(tableView).Length, "a banner row is not data and cannot be selected");

        tableView.MakeSelection(new TableViewCellSlot(1, 0), shiftKey: false); // a real row
        await Task.Yield();

        CollectionAssert.AreEqual(new[] { 1 }, SelectedRows(tableView));

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task BannerRow_IsNotSelectedByRightClickEither()
    {
        var tableView = await CreateAsync();

        tableView.ApplyContextRequestSelection(new TableViewCellSlot(0, -1), isAlreadySelected: false, false, false);
        await Task.Yield();

        Assert.AreEqual(0, SelectedRows(tableView).Length);

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task MixedItemTypes_DoNotThrow_WhenReadingCellContent()
    {
        var tableView = await CreateAsync();
        var column = tableView.Columns[0];

        // A compiled value getter is built for ONE runtime type. Reading a banner item first and a data item
        // second used to reuse that getter and throw InvalidCastException — which is unavoidable for grouping,
        // where header rows and data rows share a source by definition.
        var fromBanner = column.GetCellContent(tableView.Items[0]);
        var fromData = column.GetCellContent(tableView.Items[1]);
        var backToBanner = column.GetCellContent(tableView.Items[0]);

        Assert.AreEqual("EUR trades", fromBanner, "the banner type resolves its own Name");
        Assert.AreEqual("EURUSD", fromData, "and the data row resolves ITS Name, not through the other type's getter");
        Assert.AreEqual("EUR trades", backToBanner, "alternating types keeps working");

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task MixedItemTypes_DoNotThrow_WhenCopying()
    {
        var tableView = await CreateAsync();
        var column = tableView.Columns[0];

        _ = column.GetClipboardContent(tableView.Items[1]);
        _ = column.GetClipboardContent(tableView.Items[0]);
        _ = column.GetClipboardContent(tableView.Items[1]);

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public void IsSelectableItem_ReportsBannerRows()
    {
        var tableView = new TableView { AutoGenerateColumns = false };
        tableView.ItemsSource = new ObservableCollection<object>
        {
            new SectionItem("Section"),
            new DataItem { Name = "A" },
        };

        Assert.IsFalse(tableView.IsSelectableItem(0));
        Assert.IsTrue(tableView.IsSelectableItem(1));
        Assert.IsTrue(tableView.IsSelectableItem(-1), "out of range is not a banner");
        Assert.IsTrue(tableView.IsSelectableItem(99));
    }

    private static bool IsShowingBanner(TableViewRow? row)
        => row?.FindDescendants()
              .OfType<ContentPresenter>()
              .Any(presenter => presenter.Name == "BannerPresenter" && presenter.Visibility == Visibility.Visible)
           ?? false;

    private static TableViewRow? RowAt(TableView tableView, int index)
        => tableView.ContainerFromIndex(index) as TableViewRow;

    private static int[] SelectedRows(TableView tableView)
        => [.. tableView.SelectedRanges
            .SelectMany(range => Enumerable.Range(range.FirstIndex, (int)range.Length))
            .Distinct()
            .Order()];

    private static async Task<TableView> CreateAsync()
    {
        var tableView = new TableView
        {
            AutoGenerateColumns = false,
            SelectionMode = ListViewSelectionMode.Extended,
            SelectionUnit = TableViewSelectionUnit.Row,
            RowHeight = 32,
            Width = 600,
            Height = 400,
        };

        tableView.Columns.Add(new TableViewTextColumn
        {
            Header = "Name",
            Width = new GridLength(200, GridUnitType.Pixel),
            Binding = new Binding { Path = new PropertyPath(nameof(DataItem.Name)) },
        });

        tableView.ItemsSource = new ObservableCollection<object>
        {
            new SectionItem("EUR trades"),
            new DataItem { Name = "EURUSD" },
            new DataItem { Name = "EURGBP" },
        };

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(tableView);
        tableView.UpdateLayout();
        await Task.Yield();
        tableView.UpdateLayout();

        return tableView;
    }

    private static Task UnloadAsync(TableView tableView)
        => UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);

    /// <summary>
    /// Deliberately carries a Name of its own, like a real group header showing its key. That is what makes the
    /// mixed-type case dangerous: a getter compiled for THIS type would happily be reused on a data row.
    /// </summary>
    private sealed class SectionItem(string title) : ITableViewBannerItem
    {
        public string Name { get; } = title;
        public object? BannerContent { get; } = title;
    }

    private sealed class DataItem
    {
        public string Name { get; set; } = string.Empty;
    }
}
