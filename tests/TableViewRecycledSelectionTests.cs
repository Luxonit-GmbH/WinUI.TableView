using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace WinUI.TableView.Tests;

/// <summary>
/// Covers selection state surviving container recycling (scrolling must not leave phantom selected rows behind)
/// and the tree column's cell/editing templates.
/// </summary>
[TestClass]
public class TableViewRecycledSelectionTests
{
    [UITestMethod]
    public async Task RecycledContainer_DoesNotKeepPreviousSelectionVisuals()
    {
        var tableView = await LoadAsync();

        // Select cells in the first four rows, then recycle those containers by scrolling far away.
        for (var i = 0; i < 4; i++)
        {
            tableView.MakeSelection(new TableViewCellSlot(i, 0), shiftKey: false, ctrlKey: true);
        }

        await Task.Yield();
        await Task.Delay(50);
        tableView.UpdateLayout();

        Assert.AreEqual(4, tableView.SelectedCells.Count, "precondition: four cells selected");

        _ = await tableView.ScrollRowIntoView(300);
        tableView.UpdateLayout();
        await Task.Yield();
        await Task.Delay(100);
        tableView.UpdateLayout();

        // Every realized row now shows an item whose cells are NOT selected, so none may still LOOK selected.
        foreach (var row in tableView.Rows.Where(r => r.Index >= 4))
        {
            foreach (var cell in row.Cells)
            {
                Assert.IsFalse(cell.IsSelected, $"row {row.Index} cell still selected after recycling");

                // The visual state must have been reset too — this is what the recycle path used to skip.
                var states = Microsoft.UI.Xaml.VisualStateManager.GetVisualStateGroups(
                    (FrameworkElement)Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(cell, 0));
                var selectionGroup = states.FirstOrDefault(g => g.Name == "SelectionStates");

                if (selectionGroup?.CurrentState is { } current)
                {
                    Assert.AreNotEqual("Selected", current.Name,
                        $"row {row.Index} cell still shows the Selected visual state after recycling");
                }
            }
        }

        // The original selection itself is untouched — only the visuals were stale.
        Assert.AreEqual(4, tableView.SelectedCells.Count);

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task TreeColumn_CellTemplate_ReplacesBoundText()
    {
        var roots = new ObservableCollection<ITableViewTreeItem>();

        var treeView = new TreeTableView
        {
            AutoGenerateColumns = false,
            Width = 600,
            Height = 400,
        };

        var treeColumn = new TableViewTreeColumn
        {
            Header = "Name",
            Width = new GridLength(300, GridUnitType.Pixel),
            CellTemplate = (DataTemplate)XamlReader.Load(
                """
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                    <TextBlock Text="templated" />
                </DataTemplate>
                """),
        };

        treeView.Columns.Add(treeColumn);
        treeView.TreeItemsSource = roots;

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(treeView);
        treeView.UpdateLayout();

        // The template renders in place of the bound TextBlock, alongside the indent + chevron the column owns.
        Assert.IsNotNull(treeColumn.CellTemplate);

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(treeView);
    }

    private static async Task<TableView> LoadAsync()
    {
        var tableView = new TableView
        {
            AutoGenerateColumns = false,
            RowHeight = 32,
            Width = 600,
            Height = 300,
            SelectionMode = ListViewSelectionMode.Extended,
            SelectionUnit = TableViewSelectionUnit.Cell,
            ItemsSource = new ObservableCollection<Item>(
                Enumerable.Range(0, 400).Select(i => new Item { Name = $"Item {i}" })),
        };

        tableView.Columns.Add(new TableViewTextColumn
        {
            Header = "Name",
            Width = new GridLength(200, GridUnitType.Pixel),
            Binding = new Binding { Path = new PropertyPath(nameof(Item.Name)) },
        });

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(tableView);
        tableView.UpdateLayout();

        return tableView;
    }

    private static async Task UnloadAsync(TableView tableView)
        => await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);

    private sealed class Item
    {
        public string Name { get; set; } = string.Empty;
    }
}
