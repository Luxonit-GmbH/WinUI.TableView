using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace WinUI.TableView.Tests;

/// <summary>
/// The selection contract every items-source mode must honour identically. It exists because the modes do NOT
/// behave the same underneath: an <see cref="Microsoft.UI.Xaml.Data.ISelectionInfo"/> source (what
/// <see cref="TreeTableViewSource"/> is) makes the platform hand selection bookkeeping over, after which its own
/// SelectedItems and its SelectionChanged collections come back NULL. Every "works on the Table, broken on the
/// Tree" bug so far has been that difference leaking out, so the same assertions run against all three modes.
/// </summary>
[TestClass]
public class TableViewSelectionContractTests
{
    private enum SourceMode
    {
        /// <summary>The default: the internal CollectionView owns the items.</summary>
        CollectionView,

        /// <summary>Bypass mode over a plain collection — the platform still owns selection.</summary>
        DirectPlain,

        /// <summary>Bypass mode over TreeTableViewSource — the SOURCE owns selection (ISelectionInfo).</summary>
        DirectSelectionInfo,
    }

    [UITestMethod]
    public async Task SelectionContract_CollectionView() => await RunContractAsync(SourceMode.CollectionView);

    [UITestMethod]
    public async Task SelectionContract_DirectPlain() => await RunContractAsync(SourceMode.DirectPlain);

    [UITestMethod]
    public async Task SelectionContract_DirectSelectionInfo() => await RunContractAsync(SourceMode.DirectSelectionInfo);

    [UITestMethod]
    public async Task SelectedItems_IsNeverNull_InAnyMode()
    {
        foreach (var mode in Enum.GetValues<SourceMode>())
        {
            var tableView = await CreateAsync(mode);

            Assert.IsNotNull(tableView.SelectedItems, $"{mode}: empty selection must still give a collection");
            Assert.AreEqual(0, tableView.SelectedItems.Count, $"{mode}: nothing is selected yet");

            await UnloadAsync(tableView);
        }
    }

    [UITestMethod]
    public async Task SelectionChanged_HandlerSurvives_InAnyMode()
    {
        foreach (var mode in Enum.GetValues<SourceMode>())
        {
            var tableView = await CreateAsync(mode);

            var raised = 0;
            tableView.SelectionChanged += (_, _) => raised++;

            // Reaching the assertions means the control's OWN SelectionChanged handler did not throw on whatever
            // the platform handed it — the failure that took the app down after the upstream merge.
            tableView.SelectAll();
            await Task.Yield();
            tableView.DeselectAll();
            await Task.Yield();

            Assert.IsTrue(raised > 0, $"{mode}: selecting must raise SelectionChanged");

            await UnloadAsync(tableView);
        }
    }

    /// <summary>
    /// Select, read back through every public accessor, extend, then clear — the accessors must agree with each
    /// other in every mode, not merely be individually non-null.
    /// </summary>
    private static async Task RunContractAsync(SourceMode mode)
    {
        var tableView = await CreateAsync(mode);

        Assert.AreEqual(0, tableView.SelectedItems.Count, $"{mode}: starts unselected");
        Assert.AreEqual(0, tableView.SelectedRanges.Count, $"{mode}: starts unselected");
        Assert.AreEqual(0, tableView.SelectedValues.Count(), $"{mode}: starts unselected");

        // --- single row -------------------------------------------------------------------------------------
        tableView.SelectRange(new ItemIndexRange(2, 1));
        await Task.Yield();

        Assert.AreEqual(1, RowCount(tableView), $"{mode}: one row selected");
        Assert.AreEqual(1, tableView.SelectedItems.Count, $"{mode}: SelectedItems agrees");
        Assert.AreEqual(1, tableView.SelectedValues.Count(), $"{mode}: SelectedValues agrees");
        Assert.AreSame(tableView.Items[2], tableView.SelectedItems[0], $"{mode}: it is the right item");
        Assert.AreSame(tableView.Items[2], tableView.SelectedValues.Single(), $"{mode}: SelectedValues matches");

        // --- extend to a contiguous block -------------------------------------------------------------------
        tableView.SelectRange(new ItemIndexRange(3, 2)); // rows 3..4
        await Task.Yield();

        Assert.AreEqual(3, RowCount(tableView), $"{mode}: rows 2..4 selected");
        Assert.AreEqual(3, tableView.SelectedItems.Count, $"{mode}: SelectedItems agrees after extending");
        CollectionAssert.AreEqual(
            new[] { tableView.Items[2], tableView.Items[3], tableView.Items[4] },
            tableView.SelectedValues.ToArray(),
            $"{mode}: SelectedValues is in row order");

        // --- deselect a row in the middle -------------------------------------------------------------------
        tableView.DeselectRange(new ItemIndexRange(3, 1));
        await Task.Yield();

        Assert.AreEqual(2, RowCount(tableView), $"{mode}: the middle row is gone");
        Assert.AreEqual(2, tableView.SelectedItems.Count, $"{mode}: SelectedItems agrees after deselecting");
        CollectionAssert.AreEqual(
            new[] { tableView.Items[2], tableView.Items[4] },
            tableView.SelectedValues.ToArray(),
            $"{mode}: the split range keeps row order");

        // --- clear ------------------------------------------------------------------------------------------
        tableView.DeselectAll();
        await Task.Yield();

        Assert.AreEqual(0, RowCount(tableView), $"{mode}: cleared");
        Assert.IsNotNull(tableView.SelectedItems, $"{mode}: still a collection, not null");
        Assert.AreEqual(0, tableView.SelectedItems.Count, $"{mode}: cleared");
        Assert.AreEqual(0, tableView.SelectedValues.Count(), $"{mode}: cleared");

        await UnloadAsync(tableView);
    }

    private static int RowCount(TableView tableView)
        => tableView.SelectedRanges.Sum(range => (int)range.Length);

    private static async Task<TableView> CreateAsync(SourceMode mode)
    {
        var tableView = new TableView
        {
            AutoGenerateColumns = false,
            SelectionMode = ListViewSelectionMode.Extended,
            SelectionUnit = TableViewSelectionUnit.Row,
            UseCollectionView = mode is SourceMode.CollectionView,
            Width = 600,
            Height = 400,
        };

        tableView.Columns.Add(new TableViewTextColumn
        {
            Header = "Name",
            Width = new GridLength(250, GridUnitType.Pixel),
            Binding = new Binding { Path = new PropertyPath(nameof(ContractNode.Name)) },
        });

        var items = Enumerable.Range(0, 6).Select(i => new ContractNode($"Item {i}")).ToList();

        tableView.ItemsSource = mode is SourceMode.DirectSelectionInfo
            ? new TreeTableViewSource(new ObservableCollection<ITableViewTreeItem>(items))
            : new ObservableCollection<ContractNode>(items);

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(tableView);
        tableView.UpdateLayout();

        return tableView;
    }

    private static Task UnloadAsync(TableView tableView)
        => UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);

    /// <summary>A leaf that can serve as a plain row AND as a (childless) tree item, so one model covers all modes.</summary>
    private sealed class ContractNode(string name) : ITableViewTreeItem
    {
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

        public string Name { get; } = name;
        public int Depth => 0;
        public IEnumerable? ChildrenSource => null;
        public bool HasChildren => false;
        public bool IsFinalItem => true;
        public bool IsExpanded { get => false; set { } }
        public bool IsLoading => false;

        public override string ToString() => Name;
    }
}
