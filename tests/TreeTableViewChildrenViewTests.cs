using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Windows.Foundation.Collections;

namespace WinUI.TableView.Tests;

/// <summary>
/// Covers <see cref="TreeTableViewChildrenView"/>: the client-side, per-branch sorted + filtered children view
/// (snapshot sorting, master list retained across filters, binary-searched sorted inserts).
/// </summary>
[TestClass]
public class TreeTableViewChildrenViewTests
{
    [TestMethod]
    public void Add_WithComparer_InsertsAtSortedPosition_StableForEqualKeys()
    {
        var view = new TreeTableViewChildrenView();
        view.Apply(ByValue, filter: null);

        var b1 = new TestNode("B1", 2);
        var b2 = new TestNode("B2", 2); // equal key, added later -> must land after B1

        view.Add(new TestNode("C", 3));
        view.Add(new TestNode("A", 1));
        view.Add(b1);
        view.Add(b2);

        CollectionAssert.AreEqual(new[] { "A", "B1", "B2", "C" }, Names(view));
        Assert.AreEqual(1, view.IndexOf(b1));
        Assert.AreEqual(2, view.IndexOf(b2));
    }

    [TestMethod]
    public void Apply_SortsAndFilters_WithASingleReset()
    {
        var view = new TreeTableViewChildrenView();
        view.Add(new TestNode("C", 3));
        view.Add(new TestNode("A", 1));
        view.Add(new TestNode("B", 2));
        view.Add(new TestNode("X", 99));

        var resets = 0;
        view.VectorChanged += (_, e) => { if (e.CollectionChange == CollectionChange.Reset) resets++; };

        view.Apply(ByValue, filter: item => ((TestNode)item).Value < 10);

        Assert.AreEqual(1, resets);
        CollectionAssert.AreEqual(new[] { "A", "B", "C" }, Names(view));

        view.Apply(comparer: null, filter: null); // clear: master list retained, arrival order restored

        CollectionAssert.AreEqual(new[] { "C", "A", "B", "X" }, Names(view));
        Assert.AreEqual(4, view.AllItems.Count);
    }

    [TestMethod]
    public void SnapshotSorting_MutationDoesNotMove_RefreshDoes()
    {
        var view = new TreeTableViewChildrenView();
        view.Apply(ByValue, filter: null);

        var node = new TestNode("N", 1);
        view.Add(node);
        view.Add(new TestNode("M", 5));

        node.Value = 10; // mutate the sort key: snapshot sorting -> no movement by itself

        CollectionAssert.AreEqual(new[] { "N", "M" }, Names(view));

        view.Refresh(node); // deliberate re-place

        CollectionAssert.AreEqual(new[] { "M", "N" }, Names(view));
    }

    [TestMethod]
    public void Remove_AfterKeyMutation_FindsItemViaLinearFallback()
    {
        var view = new TreeTableViewChildrenView();
        view.Apply(ByValue, filter: null);

        var node = new TestNode("N", 1);
        view.Add(node);
        view.Add(new TestNode("M", 5));

        node.Value = 42; // binary search by current key would look in the wrong place

        Assert.IsTrue(view.Remove(node));
        CollectionAssert.AreEqual(new[] { "M" }, Names(view));
        Assert.AreEqual(1, view.AllItems.Count);
    }

    [TestMethod]
    public void FilteredOutItems_KeepLoadedState_AndReturnOnClear()
    {
        // Bound through the adapter: filtering a branch removes rows; clearing restores them WITH expansion state.
        var grandChildren = new TreeTableViewChildrenView();
        grandChildren.Add(new TestNode("G1", 1));

        var c1 = new TestNode("C1", 1) { IsExpanded = true, Children = grandChildren };
        var c2 = new TestNode("C2", 2);
        var children = new TreeTableViewChildrenView();
        children.Add(c1);
        children.Add(c2);

        var root = new TestNode("R", 0) { IsExpanded = true, Children = children };
        using var source = new TreeTableViewSource(new ObservableCollection<ITableViewTreeItem> { root });

        CollectionAssert.AreEqual(new[] { "R", "C1", "G1", "C2" }, FlatNames(source));

        children.Apply(comparer: null, filter: item => ((TestNode)item).Value >= 2); // C1 (and subtree) filtered out

        CollectionAssert.AreEqual(new[] { "R", "C2" }, FlatNames(source));
        Assert.IsTrue(c1.IsExpanded, "filtered-out node keeps its expansion state");

        children.Apply(comparer: null, filter: null);

        CollectionAssert.AreEqual(new[] { "R", "C1", "G1", "C2" }, FlatNames(source)); // subtree restored expanded
    }

    [TestMethod]
    public void Resort_ThroughAdapter_ReordersBranchRows()
    {
        var children = new TreeTableViewChildrenView();
        children.Add(new TestNode("C", 3));
        children.Add(new TestNode("A", 1));
        children.Add(new TestNode("B", 2));

        var root = new TestNode("R", 0) { IsExpanded = true, Children = children };
        using var source = new TreeTableViewSource(new ObservableCollection<ITableViewTreeItem> { root });

        CollectionAssert.AreEqual(new[] { "R", "C", "A", "B" }, FlatNames(source));

        children.Apply(ByValue, filter: null); // client-side sort, one Reset, adapter diffs

        CollectionAssert.AreEqual(new[] { "R", "A", "B", "C" }, FlatNames(source));

        children.Add(new TestNode("AB", 1)); // streamed insert lands sorted (stable: after equal-key A)

        CollectionAssert.AreEqual(new[] { "R", "A", "AB", "B", "C" }, FlatNames(source));
    }

    private static IComparer<ITableViewTreeItem> ByValue { get; }
        = Comparer<ITableViewTreeItem>.Create(static (a, b) => ((TestNode)a).Value.CompareTo(((TestNode)b).Value));

    private static string[] Names(TreeTableViewChildrenView view)
        => [.. view.Cast<TestNode>().Select(n => n.Name)];

    private static string[] FlatNames(TreeTableViewSource source)
        => [.. source.Cast<TestNode>().Select(n => n.Name)];

    private sealed class TestNode(string name, int value) : ITableViewTreeItem
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name { get; } = name;
        public int Value { get; set; } = value;
        public int Depth { get; init; }
        public TreeTableViewChildrenView? Children { get; init; }
        public System.Collections.IEnumerable? ChildrenSource => Children;
        public bool HasChildren => Children is { Count: > 0 };
        public bool IsFinalItem => false;
        public bool IsExpanded { get; set; }
        public bool IsLoading => false;

        private void OnPropertyChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
