using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace WinUI.TableView.Tests;

/// <summary>
/// Covers narrowing a column. A drag clamps to <see cref="TableViewColumn.MinWidth"/>, but the header-width pass
/// clamps again with its own effective minimum — if the two disagree the column springs back and the user can only
/// ever widen it.
/// </summary>
[TestClass]
public class TableViewColumnResizeTests
{
    private const double Narrow = 60d;

    [UITestMethod]
    public async Task PlainColumn_CanBeNarrowed()
    {
        var tableView = await CreateAsync(autoSizeMinWidth: false);
        var column = tableView.Columns[0];

        var original = column.ActualWidth;
        await ResizeAsync(tableView, column, Narrow);

        Assert.IsTrue(column.ActualWidth < original,
            $"expected the column to shrink from {original} but it is {column.ActualWidth}");
        Assert.AreEqual(Narrow, column.ActualWidth, 1d);

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task AutoSizeMinWidthColumn_CanAlsoBeNarrowed()
    {
        var tableView = await CreateAsync(autoSizeMinWidth: true);
        var column = tableView.Columns[0];

        // The captured first-render minimum has to be big enough for this to be a real test.
        var original = column.ActualWidth;
        Assert.IsTrue(original > Narrow * 2,
            $"fixture problem: the content-derived width {original} is not comfortably above {Narrow}");

        await ResizeAsync(tableView, column, Narrow);

        // AutoSizeMinWidth exists to pick a sensible width on FIRST render. It must not become a permanent floor
        // that stops the user narrowing the column afterwards.
        Assert.AreEqual(Narrow, column.ActualWidth, 1d,
            "an explicit resize must win over the auto-captured minimum");

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task ExplicitMinWidth_IsStillRespectedWhenNarrowing()
    {
        var tableView = await CreateAsync(autoSizeMinWidth: true);
        var column = tableView.Columns[0];
        column.MinWidth = 120d;

        await ResizeAsync(tableView, column, Narrow);

        Assert.AreEqual(120d, column.ActualWidth, 1d,
            "MinWidth set by the app is a real floor and must survive a resize");

        await UnloadAsync(tableView);
    }

    /// <summary>
    /// What a drag ultimately does: clamp against the column's own min/max and assign a pixel width. The
    /// header-width recalculation it triggers is coalesced onto the dispatcher, so settle before reading back.
    /// </summary>
    private static async Task ResizeAsync(TableView tableView, TableViewColumn column, double width)
    {
        var min = column.MinWidth ?? tableView.MinColumnWidth;
        var max = column.MaxWidth ?? tableView.MaxColumnWidth;

        column.NotifyUserResized();
        column.Width = new GridLength(System.Math.Clamp(width, min, max), GridUnitType.Pixel);

        await Task.Yield();
        tableView.UpdateLayout();
        await Task.Yield();
        tableView.UpdateLayout();
    }

    private static async Task<TableView> CreateAsync(bool autoSizeMinWidth)
    {
        var tableView = new TableView
        {
            AutoGenerateColumns = false,
            RowHeight = 32,
            Width = 900,
            Height = 400,
        };

        tableView.Columns.Add(new TableViewTextColumn
        {
            Header = "A column header that is fairly wide",
            AutoSizeMinWidth = autoSizeMinWidth,
            Width = new GridLength(300, GridUnitType.Pixel),
            Binding = new Binding { Path = new PropertyPath(nameof(ResizeItem.Name)) },
        });
        tableView.Columns.Add(new TableViewTextColumn
        {
            Header = "Second",
            Width = new GridLength(150, GridUnitType.Pixel),
            Binding = new Binding { Path = new PropertyPath(nameof(ResizeItem.Other)) },
        });

        tableView.ItemsSource = new ObservableCollection<ResizeItem>(
            Enumerable.Range(0, 5).Select(i => new ResizeItem
            {
                Name = $"A fairly long cell value number {i} that wants space",
                Other = $"{i}",
            }));

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(tableView);
        tableView.UpdateLayout();
        await Task.Yield();
        tableView.UpdateLayout();

        return tableView;
    }

    private static Task UnloadAsync(TableView tableView)
        => UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);

    private sealed class ResizeItem
    {
        public string Name { get; set; } = string.Empty;
        public string Other { get; set; } = string.Empty;
    }
}
