using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.System;

namespace WinUI.TableView.Tests;

/// <summary>
/// Covers <see cref="TableView.UseListViewHotkeys"/>: ListView-style row keyboarding, where Up/Down travel
/// without selecting, Enter toggles, and Shift extends and shrinks from an anchor. Everything the flag does NOT
/// claim is asserted too, because the value of this feature is that it stays narrow.
/// </summary>
[TestClass]
public class TableViewListViewHotkeysTests
{
    [UITestMethod]
    public async Task Disabled_ByDefault_ClaimsNothing()
    {
        var tableView = await CreateAsync(enable: false);

        Assert.IsFalse(Press(tableView, VirtualKey.Down));
        Assert.IsFalse(Press(tableView, VirtualKey.Enter));

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task ArrowDown_MovesTheCurrentRow_WithoutChangingSelection()
    {
        var tableView = await CreateAsync();
        tableView.SelectRange(new ItemIndexRange(0, 1)); // row 0 selected
        tableView.CurrentRowIndex = 0;
        await Task.Yield();

        Assert.IsTrue(Press(tableView, VirtualKey.Down));
        Assert.IsTrue(Press(tableView, VirtualKey.Down));

        Assert.AreEqual(2, tableView.CurrentRowIndex, "the current row travelled");
        CollectionAssert.AreEqual(new[] { 0 }, Selected(tableView), "but the selection did not follow it");

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task ArrowUp_StopsAtTheTop_AndDown_AtTheBottom()
    {
        var tableView = await CreateAsync();
        tableView.CurrentRowIndex = 0;

        Assert.IsTrue(Press(tableView, VirtualKey.Up));
        Assert.AreEqual(0, tableView.CurrentRowIndex);

        tableView.CurrentRowIndex = tableView.Items.Count - 1;
        Assert.IsTrue(Press(tableView, VirtualKey.Down));
        Assert.AreEqual(tableView.Items.Count - 1, tableView.CurrentRowIndex);

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task Enter_TogglesTheCurrentRow()
    {
        var tableView = await CreateAsync();
        tableView.CurrentRowIndex = 3;

        Assert.IsTrue(Press(tableView, VirtualKey.Enter));
        await Task.Yield();
        CollectionAssert.AreEqual(new[] { 3 }, Selected(tableView), "on");

        Assert.IsTrue(Press(tableView, VirtualKey.Enter));
        await Task.Yield();
        Assert.AreEqual(0, Selected(tableView).Length, "off again");

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task TravelThenEnter_SelectsWhereYouLanded_LeavingEarlierSelectionAlone()
    {
        var tableView = await CreateAsync();
        tableView.SelectRange(new ItemIndexRange(1, 1));
        tableView.CurrentRowIndex = 1;
        await Task.Yield();

        Press(tableView, VirtualKey.Down);
        Press(tableView, VirtualKey.Down);
        Press(tableView, VirtualKey.Enter);
        await Task.Yield();

        CollectionAssert.AreEqual(new[] { 1, 3 }, Selected(tableView));

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task ShiftDown_ExtendsFromTheAnchor()
    {
        var tableView = await CreateAsync();
        tableView.CurrentRowIndex = 1;
        tableView.SelectionStartRowIndex = 1;

        Press(tableView, VirtualKey.Down, shift: true);
        Press(tableView, VirtualKey.Down, shift: true);
        await Task.Yield();

        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, Selected(tableView));

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task ShiftReversing_ShrinksTheRange_RatherThanLeavingATrail()
    {
        var tableView = await CreateAsync();
        tableView.CurrentRowIndex = 1;
        tableView.SelectionStartRowIndex = 1;

        Press(tableView, VirtualKey.Down, shift: true);
        Press(tableView, VirtualKey.Down, shift: true);
        Press(tableView, VirtualKey.Down, shift: true); // 1..4
        await Task.Yield();
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, Selected(tableView));

        Press(tableView, VirtualKey.Up, shift: true);
        Press(tableView, VirtualKey.Up, shift: true);   // back to 1..2
        await Task.Yield();

        CollectionAssert.AreEqual(new[] { 1, 2 }, Selected(tableView));

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task ShiftExtension_LeavesSelectionsMadeElsewhereAlone()
    {
        var tableView = await CreateAsync();
        tableView.SelectRange(new ItemIndexRange(7, 1)); // an unrelated selection far away
        tableView.CurrentRowIndex = 1;
        tableView.SelectionStartRowIndex = 1;
        await Task.Yield();

        Press(tableView, VirtualKey.Down, shift: true);
        Press(tableView, VirtualKey.Down, shift: true);
        Press(tableView, VirtualKey.Up, shift: true);   // extend then shrink
        await Task.Yield();

        CollectionAssert.AreEqual(new[] { 1, 2, 7 }, Selected(tableView),
            "shrinking must give back only what the extension took");

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task DoesNotClaim_TheKeysTheGridOwns()
    {
        var tableView = await CreateAsync();
        tableView.CurrentRowIndex = 2;

        // Home/End stay COLUMN navigation — the reason we did not take the upstream patch.
        Assert.IsFalse(Press(tableView, VirtualKey.Home), "Home");
        Assert.IsFalse(Press(tableView, VirtualKey.End), "End");

        // Ctrl+Up/Down keep jumping to the first/last row.
        Assert.IsFalse(Press(tableView, VirtualKey.Up, ctrl: true), "Ctrl+Up");
        Assert.IsFalse(Press(tableView, VirtualKey.Down, ctrl: true), "Ctrl+Down");

        // Left/Right are untouched, so TreeTableView keeps expand/collapse.
        Assert.IsFalse(Press(tableView, VirtualKey.Left), "Left");
        Assert.IsFalse(Press(tableView, VirtualKey.Right), "Right");

        Assert.IsFalse(Press(tableView, VirtualKey.PageDown), "PageDown");
        Assert.IsFalse(Press(tableView, VirtualKey.Tab), "Tab");

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task DoesNotClaim_InSingleSelectionMode()
    {
        var tableView = await CreateAsync(selectionMode: ListViewSelectionMode.Single);
        tableView.CurrentRowIndex = 2;

        Assert.IsFalse(Press(tableView, VirtualKey.Down));
        Assert.IsFalse(Press(tableView, VirtualKey.Enter));

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task DoesNotClaim_InCellInteraction()
    {
        var tableView = await CreateAsync(selectionUnit: TableViewSelectionUnit.Cell);
        tableView.CurrentRowIndex = 2;

        Assert.IsFalse(Press(tableView, VirtualKey.Down), "cell keyboarding is unchanged");

        await UnloadAsync(tableView);
    }

    private static bool Press(TableView tableView, VirtualKey key, bool shift = false, bool ctrl = false)
        => tableView.TryHandleListViewHotkey(key, shift, ctrl);

    private static int[] Selected(TableView tableView)
        => [.. tableView.SelectedRanges
            .SelectMany(range => Enumerable.Range(range.FirstIndex, (int)range.Length))
            .Distinct()
            .Order()];

    private static async Task<TableView> CreateAsync(
        bool enable = true,
        ListViewSelectionMode selectionMode = ListViewSelectionMode.Extended,
        TableViewSelectionUnit selectionUnit = TableViewSelectionUnit.Row)
    {
        var tableView = new TableView
        {
            AutoGenerateColumns = false,
            UseListViewHotkeys = enable,
            SelectionMode = selectionMode,
            SelectionUnit = selectionUnit,
            Width = 600,
            Height = 400,
        };

        tableView.Columns.Add(new TableViewTextColumn
        {
            Header = "Name",
            Width = new GridLength(200, GridUnitType.Pixel),
            Binding = new Binding { Path = new PropertyPath(nameof(HotkeyItem.Name)) },
        });

        tableView.ItemsSource = new ObservableCollection<HotkeyItem>(
            Enumerable.Range(0, 10).Select(i => new HotkeyItem { Name = $"Item {i}" }));

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(tableView);
        tableView.UpdateLayout();

        return tableView;
    }

    private static Task UnloadAsync(TableView tableView)
        => UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);

    private sealed class HotkeyItem
    {
        public string Name { get; set; } = string.Empty;
    }
}
