using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace WinUI.TableView.Tests;

/// <summary>
/// Covers row grouping: the consumer binds a FLAT collection and names a property, and the grid projects it into
/// collapsible groups with full-width header rows.
/// </summary>
[TestClass]
public class TableViewRowGroupingTests
{
    [UITestMethod]
    public async Task GroupByPath_ProjectsGroupsInKeyOrder()
    {
        var tableView = await CreateAsync(groupBy: "Department");

        CollectionAssert.AreEqual(
            new[] { "Engineering", "Finance", "HR" },
            tableView.Groups.Select(group => group.Title).ToArray());

        CollectionAssert.AreEqual(new[] { 2, 1, 2 }, tableView.Groups.Select(group => group.Count).ToArray());

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task GroupRows_AppearAboveTheirMembers()
    {
        var tableView = await CreateAsync(groupBy: "Department");

        // A header row per group plus every member: 3 + 5.
        Assert.AreEqual(8, tableView.Items.Count);
        Assert.IsInstanceOfType(tableView.Items[0], typeof(TableViewGroup));
        Assert.IsInstanceOfType(tableView.Items[1], typeof(Person));
        Assert.AreEqual("Engineering", ((Person)tableView.Items[1]).Department);

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task GroupSortDirection_Descending_ReversesTheGroups()
    {
        var tableView = await CreateAsync(groupBy: "Department");
        tableView.GroupSortDirection = SortDirection.Descending;
        await SettleAsync(tableView);

        CollectionAssert.AreEqual(
            new[] { "HR", "Finance", "Engineering" },
            tableView.Groups.Select(group => group.Title).ToArray());

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task CollapsingAGroup_RemovesOnlyItsMembers()
    {
        var tableView = await CreateAsync(groupBy: "Department");
        var engineering = tableView.Groups[0];

        tableView.SetGroupExpanded(engineering, false);
        await SettleAsync(tableView);

        Assert.AreEqual(6, tableView.Items.Count, "the two Engineering rows are gone, the headers remain");
        Assert.IsFalse(engineering.IsExpanded);

        tableView.SetGroupExpanded(engineering, true);
        await SettleAsync(tableView);

        Assert.AreEqual(8, tableView.Items.Count);

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task ShowGroupHeaders_Off_PresentsTheRowsFlat()
    {
        var tableView = await CreateAsync(groupBy: "Department");
        Assert.AreEqual(8, tableView.Items.Count);

        tableView.ShowGroupHeaders = false;
        await SettleAsync(tableView);

        Assert.AreEqual(5, tableView.Items.Count, "just the data rows");
        Assert.AreEqual(0, tableView.Groups.Count);

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task ClearingGroupByPath_ReturnsToTheOriginalSource()
    {
        var tableView = await CreateAsync(groupBy: "Department");

        tableView.GroupByPath = null;
        await SettleAsync(tableView);

        Assert.AreEqual(5, tableView.Items.Count);
        Assert.IsFalse(tableView.Items.OfType<TableViewGroup>().Any());

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task GroupHeaderRows_AreNotSelectable()
    {
        var tableView = await CreateAsync(groupBy: "Department");

        tableView.SelectAll();
        await Task.Yield();

        // SelectAll goes through the platform, so the header rows are still inside the selected ranges; what
        // matters is that an explicit selection of one refuses.
        tableView.DeselectAll();
        await Task.Yield();

        tableView.MakeSelection(new TableViewCellSlot(0, 0), shiftKey: false); // a group header
        await Task.Yield();
        Assert.AreEqual(0, SelectedRows(tableView).Length);

        tableView.MakeSelection(new TableViewCellSlot(1, 0), shiftKey: false); // a member
        await Task.Yield();
        CollectionAssert.AreEqual(new[] { 1 }, SelectedRows(tableView));

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task SelectedValues_ExcludeGroupHeaders_EvenAfterSelectAll()
    {
        var tableView = await CreateAsync(groupBy: "Department");

        tableView.SelectAll(); // the platform sweeps the header rows in too
        await Task.Yield();

        // What copy and export walk must be data only.
        Assert.AreEqual(5, tableView.SelectedValues.Count());
        Assert.IsFalse(tableView.SelectedValues.OfType<TableViewGroup>().Any());
        Assert.AreEqual(5, tableView.SelectedItems.Count);
        Assert.IsFalse(tableView.SelectedItems.OfType<TableViewGroup>().Any());

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task SelectAllCells_SkipsGroupHeaderRows()
    {
        var tableView = await CreateAsync(groupBy: "Department");
        tableView.SelectionUnit = TableViewSelectionUnit.Cell;
        await SettleAsync(tableView);

        tableView.SelectAll(); // in Cell unit this routes to the cell select-all
        await Task.Yield();

        var groupRows = Enumerable.Range(0, tableView.Items.Count)
            .Where(index => tableView.Items[index] is TableViewGroup)
            .ToHashSet();

        Assert.IsTrue(groupRows.Count > 0, "the fixture has group rows");
        Assert.IsFalse(tableView.SelectedCells.Any(slot => groupRows.Contains(slot.Row)),
            "a group header has no cells to select");

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task KeyboardNavigation_StepsOverGroupHeaders()
    {
        var tableView = await CreateAsync(groupBy: "Department");

        // Rows: 0 header, 1-2 Engineering, 3 header, 4 Finance, 5 header, 6-7 HR.
        Assert.AreEqual(1, tableView.SkipUnselectableRows(0, 1), "downwards past the first header");
        Assert.AreEqual(4, tableView.SkipUnselectableRows(3, 1), "downwards past a middle header");
        Assert.AreEqual(2, tableView.SkipUnselectableRows(3, -1), "upwards past the same header");

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task NullKeys_GroupTogether()
    {
        var people = new ObservableCollection<Person>
        {
            new() { Name = "A", Department = "HR" },
            new() { Name = "B", Department = null },
            new() { Name = "C", Department = null },
        };

        var tableView = await CreateAsync(groupBy: "Department", people: people);

        Assert.AreEqual(2, tableView.Groups.Count);
        Assert.IsTrue(tableView.Groups.Any(group => group.Key is null && group.Count == 2));

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task Grouping_SurvivesAChangeOfItemsSource()
    {
        var tableView = await CreateAsync(groupBy: "Department");

        tableView.ItemsSource = new ObservableCollection<Person>
        {
            new() { Name = "Z", Department = "Legal" },
        };
        await SettleAsync(tableView);

        CollectionAssert.AreEqual(new[] { "Legal" }, tableView.Groups.Select(group => group.Title).ToArray());
        Assert.AreEqual(2, tableView.Items.Count, "one header plus one row");

        await UnloadAsync(tableView);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Custom grouping
    // ---------------------------------------------------------------------------------------------------------

    [UITestMethod]
    public async Task GroupingEvent_LetsTheAppOwnTheProjection()
    {
        var tableView = await CreateAsync(groupBy: null);

        tableView.Grouping += (_, e) =>
        {
            // Grouped by something no property path could express.
            var vowels = e.Items.Cast<object>().Where(item => ((Person)item).Name[0] is 'A' or 'E').ToList();
            var rest = e.Items.Cast<object>().Except(vowels).ToList();

            e.Groups = [new TableViewGroup("Vowel", vowels), new TableViewGroup("Other", rest)];
            e.Handled = true;
        };

        tableView.RefreshGrouping();
        await SettleAsync(tableView);

        CollectionAssert.AreEqual(new[] { "Vowel", "Other" }, tableView.Groups.Select(g => g.Title).ToArray());
        CollectionAssert.AreEqual(new[] { 2, 3 }, tableView.Groups.Select(g => g.Count).ToArray());
        Assert.AreEqual(7, tableView.Items.Count, "two headers plus five rows");

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task GroupingEvent_IsRaisedForTheBuiltInProjectionToo()
    {
        var seen = 0;
        var tableView = await CreateAsync(groupBy: "Department");

        tableView.Grouping += (_, e) =>
        {
            seen++;
            Assert.AreEqual("Department", e.GroupByPath);
            Assert.AreEqual(5, e.Items.Count);
            // Not handled: the grid falls back to its own projection.
        };

        tableView.RefreshGrouping();
        await SettleAsync(tableView);

        Assert.AreEqual(1, seen);
        Assert.AreEqual(3, tableView.Groups.Count, "the built-in grouping still ran");

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task RefreshGrouping_KeepsCollapsedGroupsCollapsed()
    {
        var tableView = await CreateAsync(groupBy: "Department");

        tableView.SetGroupExpanded(tableView.Groups[0], false); // collapse Engineering
        await SettleAsync(tableView);
        Assert.AreEqual(6, tableView.Items.Count);

        tableView.RefreshGrouping();
        await SettleAsync(tableView);

        // Re-projecting must not silently re-open what the user shut.
        Assert.IsFalse(tableView.Groups[0].IsExpanded, "Engineering is still collapsed");
        Assert.AreEqual(6, tableView.Items.Count);
        Assert.IsTrue(tableView.Groups[1].IsExpanded, "the others are untouched");

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task RefreshGrouping_PicksUpDataChanges()
    {
        var people = new ObservableCollection<Person>
        {
            new() { Name = "A", Department = "HR" },
            new() { Name = "B", Department = "HR" },
        };

        var tableView = await CreateAsync(groupBy: "Department", people: people);
        Assert.AreEqual(1, tableView.Groups.Count);

        // A live mutation the grid deliberately does not watch for.
        people[1].Department = "Legal";
        tableView.RefreshGrouping();
        await SettleAsync(tableView);

        CollectionAssert.AreEqual(new[] { "HR", "Legal" }, tableView.Groups.Select(g => g.Title).ToArray());

        await UnloadAsync(tableView);
    }

    private static int[] SelectedRows(TableView tableView)
        => [.. tableView.SelectedRanges
            .SelectMany(range => Enumerable.Range(range.FirstIndex, (int)range.Length))
            .Distinct()
            .Order()];

    private static async Task SettleAsync(TableView tableView)
    {
        tableView.UpdateLayout();
        await Task.Yield();
        tableView.UpdateLayout();
    }

    private static async Task<TableView> CreateAsync(string? groupBy, ObservableCollection<Person>? people = null)
    {
        var tableView = new TableView
        {
            AutoGenerateColumns = false,
            SelectionMode = ListViewSelectionMode.Extended,
            SelectionUnit = TableViewSelectionUnit.Row,
            RowHeight = 32,
            Width = 800,
            Height = 500,
        };

        tableView.Columns.Add(new TableViewTextColumn
        {
            Header = "Name",
            Width = new GridLength(200, GridUnitType.Pixel),
            Binding = new Binding { Path = new PropertyPath(nameof(Person.Name)) },
        });
        tableView.Columns.Add(new TableViewTextColumn
        {
            Header = "Department",
            Width = new GridLength(200, GridUnitType.Pixel),
            Binding = new Binding { Path = new PropertyPath(nameof(Person.Department)) },
        });

        tableView.GroupByPath = groupBy;
        tableView.ItemsSource = people ?? new ObservableCollection<Person>
        {
            new() { Name = "Alice", Department = "Engineering" },
            new() { Name = "Bob", Department = "HR" },
            new() { Name = "Cara", Department = "Finance" },
            new() { Name = "Dan", Department = "Engineering" },
            new() { Name = "Eve", Department = "HR" },
        };

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(tableView);
        await SettleAsync(tableView);

        return tableView;
    }

    private static Task UnloadAsync(TableView tableView)
        => UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);

    private sealed class Person
    {
        public string Name { get; set; } = string.Empty;
        public string? Department { get; set; }
    }
}
