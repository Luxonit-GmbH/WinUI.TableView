using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using System.Linq;
using System.Threading.Tasks;

namespace WinUI.TableView.Tests;

/// <summary>
/// Covers the selection a right-click makes before its context flyout opens: the clicked row/cell is selected
/// according to <see cref="TableViewSelectionUnit"/>, Ctrl/Shift are honoured exactly as on a left click, and an
/// existing multi-selection survives a plain right-click inside it.
/// </summary>
[TestClass]
public class TableViewContextSelectionTests
{
    [UITestMethod]
    public async Task RightClick_OnUnselectedRow_SelectsIt()
    {
        var tableView = await CreateAsync(TableViewSelectionUnit.Row);

        RightClickRow(tableView, 2);

        CollectionAssert.AreEqual(new[] { 2 }, SelectedRowIndexes(tableView));
    }

    [UITestMethod]
    public async Task RightClick_InsideExistingSelection_KeepsIt()
    {
        var tableView = await CreateAsync(TableViewSelectionUnit.Row);
        tableView.SelectRange(new ItemIndexRange(1, 3)); // rows 1..3
        await Task.Yield();

        // The flyout has to be able to act on the whole selection, so a plain right-click inside it must not
        // collapse the selection down to the clicked row.
        RightClickRow(tableView, 2, isAlreadySelected: true);

        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, SelectedRowIndexes(tableView));
    }

    [UITestMethod]
    public async Task RightClick_OutsideExistingSelection_ReplacesIt()
    {
        var tableView = await CreateAsync(TableViewSelectionUnit.Row);
        tableView.SelectRange(new ItemIndexRange(0, 2)); // rows 0..1
        await Task.Yield();

        RightClickRow(tableView, 4);

        CollectionAssert.AreEqual(new[] { 4 }, SelectedRowIndexes(tableView));
    }

    [UITestMethod]
    public async Task CtrlRightClick_AddsToSelection()
    {
        var tableView = await CreateAsync(TableViewSelectionUnit.Row);
        tableView.SelectRange(new ItemIndexRange(0, 1)); // row 0
        await Task.Yield();

        RightClickRow(tableView, 3, ctrlKey: true);

        CollectionAssert.AreEqual(new[] { 0, 3 }, SelectedRowIndexes(tableView));
    }

    [UITestMethod]
    public async Task CtrlRightClick_OnSelectedRow_TogglesItOff()
    {
        var tableView = await CreateAsync(TableViewSelectionUnit.Row);
        tableView.SelectRange(new ItemIndexRange(0, 3)); // rows 0..2
        await Task.Yield();

        RightClickRow(tableView, 1, isAlreadySelected: true, ctrlKey: true);

        CollectionAssert.AreEqual(new[] { 0, 2 }, SelectedRowIndexes(tableView));
    }

    [UITestMethod]
    public async Task ShiftRightClick_ExtendsFromAnchor()
    {
        var tableView = await CreateAsync(TableViewSelectionUnit.Row);
        tableView.MakeSelection(new TableViewCellSlot(1, -1), shiftKey: false); // anchor on row 1
        await Task.Yield();

        RightClickRow(tableView, 4, shiftKey: true);

        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, SelectedRowIndexes(tableView));
    }

    [UITestMethod]
    public async Task RightClick_InCellUnit_SelectsTheClickedCell()
    {
        var tableView = await CreateAsync(TableViewSelectionUnit.Cell);

        tableView.ApplyContextRequestSelection(new TableViewCellSlot(2, 1), isAlreadySelected: false, false, false);
        await Task.Yield();

        Assert.AreEqual(1, tableView.SelectedCells.Count);
        Assert.IsTrue(tableView.SelectedCells.Contains(new TableViewCellSlot(2, 1)));
    }

    [UITestMethod]
    public async Task RightClick_IsClaimedOnce_WhenTheEventBubblesCellToRow()
    {
        var tableView = await CreateAsync(TableViewSelectionUnit.Row);
        tableView.SelectRange(new ItemIndexRange(0, 1)); // row 0
        await Task.Yield();

        // One right-click on a cell with no cell flyout reaches BOTH handlers: the cell's, then its row's. Applying
        // Ctrl twice would toggle row 3 straight back off.
        tableView.ApplyContextRequestSelection(new TableViewCellSlot(3, 1), isAlreadySelected: false, true, false);
        tableView.ApplyContextRequestSelection(new TableViewCellSlot(3, -1), isAlreadySelected: false, true, false);

        CollectionAssert.AreEqual(new[] { 0, 3 }, SelectedRowIndexes(tableView));
    }

    [UITestMethod]
    public async Task RightClick_WhenDisabled_LeavesSelectionAlone()
    {
        var tableView = await CreateAsync(TableViewSelectionUnit.Row);
        tableView.ForceRowOrCellSelectionOnContextRequested = false;
        tableView.SelectRange(new ItemIndexRange(0, 1)); // row 0
        await Task.Yield();

        RightClickRow(tableView, 3);

        CollectionAssert.AreEqual(new[] { 0 }, SelectedRowIndexes(tableView));
    }

    [UITestMethod]
    public async Task RightClick_WhenSelectionModeIsNone_LeavesSelectionAlone()
    {
        var tableView = await CreateAsync(TableViewSelectionUnit.Row, ListViewSelectionMode.None);

        RightClickRow(tableView, 3);

        Assert.AreEqual(0, SelectedRowIndexes(tableView).Length);
    }

    private static void RightClickRow(
        TableView tableView,
        int row,
        bool isAlreadySelected = false,
        bool ctrlKey = false,
        bool shiftKey = false)
        => tableView.ApplyContextRequestSelection(
            new TableViewCellSlot(row, -1), isAlreadySelected, ctrlKey, shiftKey);

    private static int[] SelectedRowIndexes(TableView tableView)
        => [.. tableView.SelectedRanges
            .SelectMany(range => Enumerable.Range(range.FirstIndex, (int)range.Length))
            .Distinct()
            .Order()];

    private static async Task<TableView> CreateAsync(
        TableViewSelectionUnit selectionUnit,
        ListViewSelectionMode selectionMode = ListViewSelectionMode.Extended)
    {
        var tableView = new TableView
        {
            AutoGenerateColumns = false,
            SelectionMode = selectionMode,
            SelectionUnit = selectionUnit,
            Width = 600,
            Height = 400,
        };

        tableView.Columns.Add(new TableViewTextColumn
        {
            Header = "Name",
            Binding = new Binding { Path = new PropertyPath(nameof(ContextItem.Name)) },
        });
        tableView.Columns.Add(new TableViewTextColumn
        {
            Header = "Value",
            Binding = new Binding { Path = new PropertyPath(nameof(ContextItem.Value)) },
        });

        tableView.ItemsSource = Enumerable.Range(0, 6)
            .Select(i => new ContextItem { Name = $"Item {i}", Value = i })
            .ToList();

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(tableView);
        tableView.UpdateLayout();

        return tableView;
    }

    private sealed class ContextItem
    {
        public string Name { get; set; } = string.Empty;
        public int Value { get; set; }
    }
}
