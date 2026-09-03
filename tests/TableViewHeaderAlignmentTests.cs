using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Foundation;

namespace WinUI.TableView.Tests;

/// <summary>
/// The header row and the cell columns are panned by different elements bound to the same offset, so nothing
/// structural keeps them aligned — only the arithmetic does. This checks the arithmetic on screen: every visible
/// header's left edge must sit over its column's cells, before any scroll and after several, using
/// TransformToVisual, which reports composition translation (verified) and so measures what the user sees.
/// </summary>
[TestClass]
public class TableViewHeaderAlignmentTests
{
    private const int ColumnCount = 80;
    private const double ColumnWidth = 100;

    [UITestMethod]
    public async Task Headers_SitOverTheirColumns_BeforeAndAfterHorizontalScroll()
    {
        var tableView = await LoadAsync();

        foreach (var offset in new[] { 0d, 250d, 1000d, 2450d })
        {
            tableView.SetValue(TableView.HorizontalOffsetProperty, offset);
            tableView.UpdateLayout();
            await Task.Delay(150); // let the band realize and the compositor apply the pan
            tableView.UpdateLayout();

            AssertAligned(tableView, offset);
        }

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);
    }

    /// <summary>
    /// The same check over template columns whose cell is a Button — the configuration the heavy pan benchmark
    /// draws, and the one a header/cell misalignment was reported in. A Control cell has its own template,
    /// padding and minimum size, none of which the header knows about, so this is where the two can drift apart.
    /// </summary>
    [UITestMethod]
    public async Task Headers_SitOverTheirColumns_WithTemplateColumns_BeforeAndAfterHorizontalScroll()
    {
        var tableView = await LoadAsync(heavy: true);

        foreach (var offset in new[] { 0d, 250d, 1000d, 2450d })
        {
            tableView.SetValue(TableView.HorizontalOffsetProperty, offset);
            tableView.UpdateLayout();
            await Task.Delay(150);
            tableView.UpdateLayout();

            AssertAligned(tableView, offset);
        }

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);
    }

    private static void AssertAligned(TableView tableView, double offset)
    {
        var row = tableView.Rows.First();
        var report = new StringBuilder();
        var misaligned = 0;

        foreach (var column in tableView.Columns)
        {
            if (column.HeaderControl is not { } header || header.ActualWidth <= 0)
            {
                continue;
            }

            var cell = row.Cells.FirstOrDefault(c => c.Column == column);

            if (cell is null || cell.Visibility != Visibility.Visible)
            {
                continue; // outside the realized band: collapsed, nothing on screen to align
            }

            var headerX = header.TransformToVisual(tableView).TransformPoint(new Point(0, 0)).X;
            var cellX = cell.TransformToVisual(tableView).TransformPoint(new Point(0, 0)).X;

            // Only what is actually on screen matters.
            if (headerX + header.ActualWidth < 0 || headerX > tableView.ActualWidth)
            {
                continue;
            }

            if (System.Math.Abs(headerX - cellX) > 1)
            {
                misaligned++;
                report.AppendLine($"  {column.Header}: header at {headerX:F1}, cells at {cellX:F1} (delta {headerX - cellX:F1}), header width {header.ActualWidth:F0}, column width {column.ActualWidth:F0}");
            }
        }

        Assert.AreEqual(0, misaligned,
            $"at HorizontalOffset {offset}, {misaligned} visible header(s) do not sit over their cells:\n{report}");
    }

    private static DataTemplate? _buttonCellTemplate;

    private static async Task<TableView> LoadAsync(bool heavy = false)
    {
        var tableView = new TableView
        {
            AutoGenerateColumns = false,
            IsColumnVirtualizationEnabled = true,
            RowHeight = 32,
            Width = 1200,
            Height = 400,
        };

        _buttonCellTemplate ??= (DataTemplate)XamlReader.Load(
            """
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <Button MinWidth="80" Padding="4,0" HorizontalAlignment="Stretch">
                    <TextBlock Text="{Binding Name}" />
                </Button>
            </DataTemplate>
            """);

        for (var i = 0; i < ColumnCount; i++)
        {
            tableView.Columns.Add(heavy
                ? new TableViewTemplateColumn
                {
                    Header = $"Col {i}",
                    Width = new GridLength(ColumnWidth),
                    CellTemplate = _buttonCellTemplate,
                }
                : new TableViewTextColumn
                {
                    Header = $"Col {i}",
                    Width = new GridLength(ColumnWidth),
                    Binding = new Binding { Path = new PropertyPath(nameof(Item.Name)) },
                });
        }

        tableView.ItemsSource = new ObservableCollection<Item>(Enumerable.Range(0, 100).Select(i => new Item { Name = $"Item {i}" }));

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
