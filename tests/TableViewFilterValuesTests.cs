using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using WinUI.TableView.Controls;
using WinUI.TableView.Extensions;

namespace WinUI.TableView.Tests;

/// <summary>
/// The values a column's filter flyout lists must come out for any value type, in a sensible order, and a
/// handler that throws must leave the flyout empty rather than end the process.
/// </summary>
/// <remarks>
/// The flyout used to collect values into a <c>SortedSet</c> with the default comparer, which throws "At least one
/// object must implement IComparable" for a record or a custom struct and "Object must be of type X" for a column
/// that mixes types; the flyout's initialisation is <c>async void</c>, so the throw was an unhandled exception
/// and the application died with it.
/// </remarks>
[TestClass]
public class TableViewFilterValuesTests
{
    [UITestMethod]
    public async Task FilterItems_ListAValueTypeWithoutIComparable_InTextOrder()
    {
        var tableView = await LoadAsync(
            new Row { Price = new Money(2) },
            new Row { Price = new Money(10) },
            new Row { Price = new Money(1) },
            new Row { Price = new Money(2) },
            new Row { Price = null });
        var column = tableView.Columns[0];

        var items = tableView.FilterHandler.GetFilterItems(column, null);

        // One entry per distinct value plus the blank entry, ordered by the text the flyout shows: 1, 10, 2.
        Assert.IsNull(items[0].Value, "the blank entry comes first");
        CollectionAssert.AreEqual(
            new object?[] { new Money(1), new Money(10), new Money(2) },
            items.Skip(1).Select(x => x.Value).ToArray());

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task FilterItems_CountAValueTypeWithoutIComparable()
    {
        var tableView = await LoadAsync(
            new Row { Price = new Money(2) },
            new Row { Price = new Money(10) },
            new Row { Price = new Money(2) },
            new Row { Price = new Money(2) },
            new Row { Price = null });
        tableView.ShowFilterItemsCount = true;
        var column = tableView.Columns[0];

        var items = tableView.FilterHandler.GetFilterItems(column, null);

        Assert.AreEqual(3, items.Count);
        Assert.AreEqual(3, items.Single(x => Equals(x.Value, new Money(2))).Count, "equal values are one entry, counted");
        Assert.AreEqual(1, items.Single(x => Equals(x.Value, new Money(10))).Count);
        Assert.AreEqual(1, items.Single(x => x.Value is null).Count);

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task FilterItems_ListAColumnThatMixesTypes()
    {
        var tableView = await LoadAsync(
            new Row { Tag = 3 },
            new Row { Tag = "b" },
            new Row { Tag = 1 },
            new Row { Tag = "a" },
            new Row { Tag = 3 });
        var column = tableView.Columns[1];

        var items = tableView.FilterHandler.GetFilterItems(column, null);

        // The default comparer throws "Object must be of type Int32" here. Values of one type keep their own
        // order; across types, text order; nothing is lost.
        CollectionAssert.AreEquivalent(new object[] { 1, 3, "a", "b" }, items.Select(x => x.Value).ToArray());
        Assert.AreEqual(4, items.Count);
        Assert.IsTrue(items.Select(x => x.Value).ToList().IndexOf(1) < items.Select(x => x.Value).ToList().IndexOf(3));
        Assert.IsTrue(items.Select(x => x.Value).ToList().IndexOf("a") < items.Select(x => x.Value).ToList().IndexOf("b"));

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task FilterItems_StillOrderComparableValues()
    {
        var tableView = await LoadAsync(
            new Row { Amount = 10 },
            new Row { Amount = 9 },
            new Row { Amount = 100 });
        var column = tableView.Columns[2];

        var items = tableView.FilterHandler.GetFilterItems(column, null);

        // Numbers order as numbers (9, 10, 100), not as text (10, 100, 9).
        CollectionAssert.AreEqual(new object?[] { 9m, 10m, 100m }, items.Select(x => x.Value).ToArray());

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task FilterFlyout_OpensEmpty_WhenTheHandlerThrows()
    {
        var tableView = await LoadAsync(new Row { Price = new Money(1) });
        tableView.FilterHandler = new ThrowingFilterHandler(tableView);
        var header = tableView.FindDescendant<TableViewColumnHeader>()!;

        var control = new TableViewFilterItemsControl { TableView = tableView, ColumnHeader = header };

        // Initialize is async void; the values are fetched before its first await, so they can be checked at once.
        control.Initialize();

        Assert.IsNotNull(control.FilterItems);
        Assert.AreEqual(0, control.FilterItems!.Count, "a throwing handler leaves the flyout empty rather than unhandled");

        await UnloadAsync(tableView);
    }

    private static async Task<TableView> LoadAsync(params Row[] rows)
    {
        var tableView = new TableView
        {
            AutoGenerateColumns = false,
            RowHeight = 32,
            Width = 900,
            Height = 400,
            ItemsSource = new ObservableCollection<Row>(rows),
        };

        foreach (var path in new[] { nameof(Row.Price), nameof(Row.Tag), nameof(Row.Amount) })
        {
            tableView.Columns.Add(new TableViewTextColumn
            {
                Header = path,
                Width = new GridLength(150, GridUnitType.Pixel),
                Binding = new Binding { Path = new PropertyPath(path) },
            });
        }

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(tableView);
        tableView.UpdateLayout();

        return tableView;
    }

    private static async Task UnloadAsync(TableView tableView)
        => await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);

    /// <summary>A value with equality but no ordering, the shape of a typical domain record.</summary>
    private sealed record Money(decimal Amount);

    private sealed class Row
    {
        public Money? Price { get; set; }
        public object? Tag { get; set; }
        public decimal Amount { get; set; }
    }

    private sealed class ThrowingFilterHandler(TableView tableView) : ColumnFilterHandler(tableView)
    {
        public override IList<TableViewFilterItem> GetFilterItems(TableViewColumn column, string? searchText = default)
            => throw new InvalidOperationException("the app's handler failed");
    }
}
