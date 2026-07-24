using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation.Collections;

namespace WinUI.TableView.Tests;

/// <summary>
/// Covers <see cref="TreeTableViewSource"/>: the library-side flattening adapter over nested
/// (collection-in-collection) hierarchies. The app mutates the NESTED collections only; the adapter owns all flat
/// index math and raises native VectorChanged for the grid.
/// </summary>
[TestClass]
public class TreeTableViewSourceTests
{
    [TestMethod]
    public void InitialFlatten_IncludesPreExpandedBranches()
    {
        var (source, _, _) = CreateTree();

        AssertFlat(source, "A", "A1", "A2", "A2a", "B");
    }

    [TestMethod]
    public void ExpandCollapse_SplicesSubtree_AndPreservesDescendantState()
    {
        var (source, a, _) = CreateTree();
        var a2 = (Node)a.Children![1];

        source.Collapse(a);

        AssertFlat(source, "A", "B");
        Assert.IsFalse(a.IsExpanded);
        Assert.IsTrue(a2.IsExpanded, "collapsing a parent must not reset descendants' expansion state");

        source.Expand(a);

        AssertFlat(source, "A", "A1", "A2", "A2a", "B"); // A2's subtree restored because it stayed expanded
        Assert.IsTrue(a.IsExpanded);
    }

    [TestMethod]
    public void ChildInsert_IntoExpandedParent_LandsAtCorrectFlatPosition()
    {
        var (source, a, _) = CreateTree();
        var events = Record(source);

        // The app's only job: insert into the PARENT's children at the app's ordering. Here between A1 and A2.
        a.Children!.Insert(1, new Node("AX", 1));

        AssertFlat(source, "A", "A1", "AX", "A2", "A2a", "B");
        CollectionAssert.AreEqual(new[] { (CollectionChange.ItemInserted, 2u) }, events);
    }

    [TestMethod]
    public void ChildInsert_IntoCollapsedParent_IsDeferredUntilExpand()
    {
        var (source, a, roots) = CreateTree();
        var b = (Node)roots[1];
        b.SetChildren(new ObservableCollection<ITableViewTreeItem>());

        b.Children!.Add(new Node("B1", 1));

        AssertFlat(source, "A", "A1", "A2", "A2a", "B"); // collapsed: nothing visible yet

        source.Expand(b);

        AssertFlat(source, "A", "A1", "A2", "A2a", "B", "B1");
    }

    [TestMethod]
    public void RootInsert_BetweenSubtrees_LandsAfterPrecedingSubtree()
    {
        var (source, _, roots) = CreateTree();
        var events = Record(source);

        roots.Insert(1, new Node("C", 0)); // between A (whose subtree occupies 4 flat rows) and B

        AssertFlat(source, "A", "A1", "A2", "A2a", "C", "B");
        CollectionAssert.AreEqual(new[] { (CollectionChange.ItemInserted, 4u) }, events);
    }

    [TestMethod]
    public void ChildRemove_RemovesWholeExpandedSubtree_AndStopsTracking()
    {
        var (source, a, _) = CreateTree();
        var a2 = (Node)a.Children![1];
        var events = Record(source);

        a.Children!.RemoveAt(1); // removes A2, which is expanded with child A2a

        AssertFlat(source, "A", "A1", "B");
        CollectionAssert.AreEqual(
            new[] { (CollectionChange.ItemRemoved, 2u), (CollectionChange.ItemRemoved, 2u) }, events);

        // The removed branch is no longer tracked: mutating it must not touch the flat view.
        events.Clear();
        a2.Children!.Add(new Node("ghost", 2));
        Assert.AreEqual(0, events.Count);
        AssertFlat(source, "A", "A1", "B");
    }

    [TestMethod]
    public void VectorChildren_InsertAndRemove_FlowIntoFlatView()
    {
        // Children exposed as a native IObservableVector<object> (the app's preferred shape).
        var vectorChildren = new TestObservableVector { new Node("R1", 1), new Node("R2", 1) };
        var root = new Node("R", 0);
        root.SetChildren(vectorChildren);
        var source = new TreeTableViewSource(new ObservableCollection<ITableViewTreeItem> { root });

        source.Expand(root);
        AssertFlat(source, "R", "R1", "R2");

        vectorChildren.Insert(1, new Node("RX", 1));
        AssertFlat(source, "R", "R1", "RX", "R2");

        vectorChildren.RemoveAt(0);
        AssertFlat(source, "R", "RX", "R2");
    }

    [UITestMethod]
    public async Task TreeItemsSource_OneLineBinding_WrapsRootsAndDisplaysTree()
    {
        var (source, _, roots) = CreateTree();
        source.Dispose(); // roots reused; the control creates its own adapter

        var treeTableView = new TreeTableView { AutoGenerateColumns = false, Width = 600, Height = 400 };
        treeTableView.Columns.Add(new TableViewTreeColumn
        {
            Header = "Name",
            Width = new GridLength(250, GridUnitType.Pixel),
            Binding = new Binding { Path = new PropertyPath(nameof(Node.Name)) },
        });

        treeTableView.TreeItemsSource = roots; // the entire wiring

        Assert.IsInstanceOfType<TreeTableViewSource>(treeTableView.ItemsSource);
        Assert.IsFalse(treeTableView.UseCollectionView);
        Assert.IsNotNull(treeTableView.TreeSource);

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(treeTableView);
        treeTableView.UpdateLayout();

        Assert.AreEqual(5, treeTableView.Items.Count); // pre-expanded fixture fully flattened

        treeTableView.TreeItemsSource = null;
        Assert.IsNull(treeTableView.ItemsSource);

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(treeTableView);
    }

    [UITestMethod]
    public async Task AutoExpandCollapse_NoHandlersNeeded()
    {
        var (source, a, roots) = CreateTree();
        source.Collapse(a);
        source.Dispose();

        var treeTableView = new TreeTableView { AutoGenerateColumns = false, Width = 600, Height = 400 };
        treeTableView.Columns.Add(new TableViewTreeColumn
        {
            Header = "Name",
            Width = new GridLength(250, GridUnitType.Pixel),
            Binding = new Binding { Path = new PropertyPath(nameof(Node.Name)) },
        });
        treeTableView.TreeItemsSource = roots;

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(treeTableView);
        treeTableView.UpdateLayout();

        Assert.AreEqual(2, treeTableView.Items.Count);

        treeTableView.RequestExpandCollapse(a, 0, expand: true); // no handlers wired anywhere

        Assert.IsTrue(a.IsExpanded);
        Assert.AreEqual(5, treeTableView.Items.Count);

        treeTableView.RequestExpandCollapse(a, 0, expand: false);

        Assert.IsFalse(a.IsExpanded);
        Assert.AreEqual(2, treeTableView.Items.Count);

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(treeTableView);
    }

    [UITestMethod]
    public async Task AutoExpand_EmptyLiveChildren_ThenStreamedRowsAppear()
    {
        // The streaming-backend shape: children collection exists (empty) before any data arrives; expanding
        // subscribes the empty branch and rows stream in afterwards with no further calls.
        var streamingChildren = new ObservableCollection<ITableViewTreeItem>();
        var root = new Node("R", 0) { ForceHasChildren = true };
        root.SetChildren(streamingChildren);
        var roots = new ObservableCollection<ITableViewTreeItem> { root };

        var treeTableView = new TreeTableView { AutoGenerateColumns = false, Width = 600, Height = 400 };
        treeTableView.Columns.Add(new TableViewTreeColumn
        {
            Header = "Name",
            Width = new GridLength(250, GridUnitType.Pixel),
            Binding = new Binding { Path = new PropertyPath(nameof(Node.Name)) },
        });
        treeTableView.TreeItemsSource = roots;

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(treeTableView);
        treeTableView.UpdateLayout();

        treeTableView.RequestExpandCollapse(root, 0, expand: true); // auto: subscribes the empty branch

        Assert.IsTrue(root.IsExpanded);
        Assert.AreEqual(1, treeTableView.Items.Count);

        streamingChildren.Add(new Node("R1", 1)); // "backend" data arrives later
        streamingChildren.Add(new Node("R2", 1));

        Assert.AreEqual(3, treeTableView.Items.Count);
        Assert.AreSame(streamingChildren[0], treeTableView.Items[1]);

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(treeTableView);
    }

    [UITestMethod]
    public async Task CancelOnArgs_SuppressesAutoExpansion()
    {
        var (source, a, roots) = CreateTree();
        source.Collapse(a);
        source.Dispose();

        var treeTableView = new TreeTableView { AutoGenerateColumns = false, Width = 600, Height = 400 };
        treeTableView.Columns.Add(new TableViewTreeColumn
        {
            Header = "Name",
            Width = new GridLength(250, GridUnitType.Pixel),
            Binding = new Binding { Path = new PropertyPath(nameof(Node.Name)) },
        });
        treeTableView.TreeItemsSource = roots;
        treeTableView.ExpandRequested += (_, e) => e.Cancel = true; // strict fetch-then-expand apps own the timing

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(treeTableView);
        treeTableView.UpdateLayout();

        treeTableView.RequestExpandCollapse(a, 0, expand: true);

        Assert.IsFalse(a.IsExpanded);
        Assert.AreEqual(2, treeTableView.Items.Count);

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(treeTableView);
    }

    [UITestMethod]
    public async Task HandlerCallingExpandItself_PlusAuto_YieldsNoDoubleRows()
    {
        var (source, a, roots) = CreateTree();
        source.Collapse(a);
        source.Dispose();

        var treeTableView = new TreeTableView { AutoGenerateColumns = false, Width = 600, Height = 400 };
        treeTableView.Columns.Add(new TableViewTreeColumn
        {
            Header = "Name",
            Width = new GridLength(250, GridUnitType.Pixel),
            Binding = new Binding { Path = new PropertyPath(nameof(Node.Name)) },
        });
        treeTableView.TreeItemsSource = roots;

        // Migrating apps may still expand inside the handler; the follow-up auto call must be a no-op.
        treeTableView.ExpandRequested += (_, e) => treeTableView.TreeSource!.Expand(e.Item);

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(treeTableView);
        treeTableView.UpdateLayout();

        treeTableView.RequestExpandCollapse(a, 0, expand: true);

        Assert.AreEqual(5, treeTableView.Items.Count); // exactly once

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(treeTableView);
    }

    [UITestMethod]
    public async Task BoundToTreeTableView_DirectMode_ChevronFlowExpands()
    {
        var (source, a, _) = CreateTree();
        source.Collapse(a);

        var treeTableView = new TreeTableView
        {
            AutoGenerateColumns = false,
            UseCollectionView = false,
            Width = 600,
            Height = 400,
        };
        treeTableView.Columns.Add(new TableViewTreeColumn
        {
            Header = "Name",
            Width = new GridLength(250, GridUnitType.Pixel),
            Binding = new Binding { Path = new PropertyPath(nameof(Node.Name)) },
        });

        // The entire app-side wiring for already-loaded children: two one-liners.
        treeTableView.ExpandRequested += (_, e) => source.Expand((ITableViewTreeItem)e.Item);
        treeTableView.CollapseRequested += (_, e) => source.Collapse((ITableViewTreeItem)e.Item);

        treeTableView.ItemsSource = source;
        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(treeTableView);
        treeTableView.UpdateLayout();

        Assert.AreEqual(2, treeTableView.Items.Count);

        treeTableView.RequestExpandCollapse(a, 0, expand: true);
        await Task.Yield();
        treeTableView.UpdateLayout();

        Assert.AreEqual(5, treeTableView.Items.Count);
        Assert.AreSame(a.Children![0], ((TableViewRow)treeTableView.ContainerFromIndex(1)!).Content);

        treeTableView.RequestExpandCollapse(a, 0, expand: false);
        await Task.Yield();

        Assert.AreEqual(2, treeTableView.Items.Count);

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(treeTableView);
    }

    [TestMethod]
    public void VectorChildren_ItemChangedWithSameReference_IsIgnored()
    {
        // ItemChanged raised as a "refresh" hint with the SAME instance must not tear the row down — property
        // mutations flow through bindings.
        var children = new TestObservableVector { new Node("R1", 1), new Node("R2", 1) };
        var root = new Node("R", 0) { IsExpanded = true };
        root.SetChildren(children);
        using var source = new TreeTableViewSource(new ObservableCollection<ITableViewTreeItem> { root });
        var events = Record(source);

        children[0] = children[0]; // same reference

        Assert.AreEqual(0, events.Count);
        AssertFlat(source, "R", "R1", "R2");

        children[0] = new Node("R1'", 1); // actual replacement still works

        AssertFlat(source, "R", "R1'", "R2");
    }

    [TestMethod]
    public void VectorChildren_ResetWithAppendedTail_TouchesOnlyTheTail()
    {
        // Reset is diffed by reference: the unchanged prefix keeps its rows, subtrees AND subscriptions — only the
        // appended children produce events. No Reset ever reaches the flat view.
        var grandChildren = new TestObservableVector { new Node("G1", 2) };
        var c0 = new Node("C0", 1) { IsExpanded = true };
        c0.SetChildren(grandChildren);
        var c1 = new Node("C1", 1);
        var children = new TestObservableVector { c0, c1 };
        var root = new Node("R", 0) { IsExpanded = true };
        root.SetChildren(children);
        using var source = new TreeTableViewSource(new ObservableCollection<ITableViewTreeItem> { root });

        AssertFlat(source, "R", "C0", "G1", "C1");
        var events = Record(source);

        children.ReplaceAll([c0, c1, new Node("C2", 1), new Node("C3", 1)]);

        AssertFlat(source, "R", "C0", "G1", "C1", "C2", "C3");
        CollectionAssert.AreEqual(
            new[] { (CollectionChange.ItemInserted, 4u), (CollectionChange.ItemInserted, 5u) }, events);

        // The untouched prefix child's branch is still tracked: its children keep flowing into the flat view.
        grandChildren.Add(new Node("G2", 2));
        AssertFlat(source, "R", "C0", "G1", "G2", "C1", "C2", "C3");
    }

    [TestMethod]
    public void VectorChildren_ResetWithReorder_MatchesNewOrder()
    {
        var a = new Node("A", 1);
        var b = new Node("B", 1);
        var c = new Node("C", 1);
        var children = new TestObservableVector { a, b, c };
        var root = new Node("R", 0) { IsExpanded = true };
        root.SetChildren(children);
        using var source = new TreeTableViewSource(new ObservableCollection<ITableViewTreeItem> { root });

        children.ReplaceAll([c, a, b]); // resort: no common prefix/suffix, window rebuild

        AssertFlat(source, "R", "C", "A", "B");
    }

    [TestMethod]
    public void Selection_FollowsItemsAcrossStreamedInserts_NewRowNotSelected()
    {
        var (source, a, _) = CreateTree(); // flat: A, A1, A2, A2a, B

        source.SelectRange(new ItemIndexRange(1, 3)); // A1, A2, A2a

        a.Children!.Insert(1, new Node("AX", 1)); // lands at flat index 2, INSIDE the selected range

        // flat: A, A1, AX, A2, A2a, B — the previously selected ITEMS stay selected, AX does not.
        Assert.IsFalse(source.IsSelected(0));
        Assert.IsTrue(source.IsSelected(1));  // A1
        Assert.IsFalse(source.IsSelected(2)); // AX (inserted into the middle of the range -> split)
        Assert.IsTrue(source.IsSelected(3));  // A2
        Assert.IsTrue(source.IsSelected(4));  // A2a
        Assert.IsFalse(source.IsSelected(5)); // B
        Assert.AreEqual(3, source.GetSelectedRanges().Sum(r => (long)r.Length));
    }

    [TestMethod]
    public void Selection_ShrinksOnRemoval_AndFollowsShiftedItems()
    {
        var (source, a, _) = CreateTree(); // flat: A, A1, A2, A2a, B

        source.SelectRange(new ItemIndexRange(2, 3)); // A2, A2a, B

        a.Children!.RemoveAt(0); // removes A1 -> flat: A, A2, A2a, B

        Assert.IsTrue(source.IsSelected(1));  // A2 followed its item down
        Assert.IsTrue(source.IsSelected(2));  // A2a
        Assert.IsTrue(source.IsSelected(3));  // B
        Assert.IsFalse(source.IsSelected(0));

        a.Children!.RemoveAt(0); // removes A2 (selected, expanded with A2a) -> flat: A, B

        Assert.AreEqual(1, source.GetSelectedRanges().Sum(r => (long)r.Length)); // only B remains selected
        Assert.IsTrue(source.IsSelected(1));
    }

    [TestMethod]
    public void Selection_CollapseDropsDescendantSelection_KeepsTheRest()
    {
        var (source, a, _) = CreateTree(); // flat: A, A1, A2, A2a, B

        source.SelectRange(new ItemIndexRange(0, 5)); // everything

        source.Collapse(a); // flat: A, B

        Assert.IsTrue(source.IsSelected(0));  // A kept
        Assert.IsTrue(source.IsSelected(1));  // B kept (followed its item)
        Assert.AreEqual(2, source.GetSelectedRanges().Sum(r => (long)r.Length)); // descendants' selection gone
    }

    [UITestMethod]
    public async Task BoundToTreeTableView_SelectionInfo_SelectAllThenExpand()
    {
        var (source, a, _) = CreateTree();
        source.Collapse(a); // start with: A, B

        var treeTableView = new TreeTableView
        {
            AutoGenerateColumns = false,
            UseCollectionView = false,
            SelectionMode = ListViewSelectionMode.Extended,
            Width = 600,
            Height = 400,
        };
        treeTableView.Columns.Add(new TableViewTreeColumn
        {
            Header = "Name",
            Width = new GridLength(250, GridUnitType.Pixel),
            Binding = new Binding { Path = new PropertyPath(nameof(Node.Name)) },
        });
        treeTableView.ExpandRequested += (_, e) => source.Expand((ITableViewTreeItem)e.Item);

        treeTableView.ItemsSource = source;
        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(treeTableView);
        treeTableView.UpdateLayout();

        treeTableView.SelectAll(); // delegates to source.SelectRange — O(ranges), no per-item work
        await Task.Yield();

        Assert.AreEqual(2, treeTableView.SelectedRanges.Sum(r => (long)r.Length));
        Assert.AreEqual(2, treeTableView.SelectedValues.Count());

        treeTableView.RequestExpandCollapse(a, 0, expand: true);
        await Task.Yield();

        // The two originally selected roots are still selected; the inserted children are not.
        Assert.AreEqual(5, treeTableView.Items.Count);
        Assert.AreEqual(2, treeTableView.SelectedRanges.Sum(r => (long)r.Length));
        CollectionAssert.AreEqual(
            new[] { "A", "B" },
            treeTableView.SelectedValues.Cast<Node>().Select(n => n.Name).ToArray());

        treeTableView.DeselectAll();
        await Task.Yield();
        Assert.AreEqual(0, treeTableView.SelectedRanges.Count);

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(treeTableView);
    }

    [UITestMethod]
    public async Task AutomationPeer_TreeRow_ReportsSiblingPosition()
    {
        var (source, _, _) = CreateTree(); // flat: A, A1, A2, A2a, B

        var treeTableView = new TreeTableView
        {
            AutoGenerateColumns = false,
            UseCollectionView = false,
            Width = 600,
            Height = 400,
        };
        treeTableView.Columns.Add(new TableViewTreeColumn
        {
            Header = "Name",
            Width = new GridLength(250, GridUnitType.Pixel),
            Binding = new Binding { Path = new PropertyPath(nameof(Node.Name)) },
        });
        treeTableView.ItemsSource = source;

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(treeTableView);
        treeTableView.UpdateLayout();

        // A is root 1 of 2; A1 is child 1 of 2 within A; B is root 2 of 2.
        var peerA = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(
            (TableViewRow)treeTableView.ContainerFromIndex(0)!);
        var peerA1 = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(
            (TableViewRow)treeTableView.ContainerFromIndex(1)!);
        var peerB = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(
            (TableViewRow)treeTableView.ContainerFromIndex(4)!);

        Assert.AreEqual((1, 2), (peerA.GetPositionInSet(), peerA.GetSizeOfSet()));
        Assert.AreEqual((1, 2), (peerA1.GetPositionInSet(), peerA1.GetSizeOfSet()));
        Assert.AreEqual((2, 2), (peerB.GetPositionInSet(), peerB.GetSizeOfSet()));

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(treeTableView);
    }

    [TestMethod]
    public void ResortViaRemoveAndInsert_MovedNodeKeepsExpansionAndSubtree()
    {
        // A data-layer resort arrives as remove+insert on the children collection. The moved node's expansion
        // state lives on the NODE, so its whole visible subtree re-materializes at the new position.
        var (source, a, _) = CreateTree(); // flat: A, A1, A2(expanded), A2a, B

        var a2 = (Node)a.Children![1];
        a.Children!.RemoveAt(1);
        a.Children!.Insert(0, a2); // A2 sorted before A1 now

        Assert.IsTrue(a2.IsExpanded);
        AssertFlat(source, "A", "A2", "A2a", "A1", "B");

        // And its branch is live again: streaming into the moved subtree still flows.
        ((ObservableCollection<ITableViewTreeItem>)a2.ChildrenSource!).Add(new Node("A2b", 2));
        AssertFlat(source, "A", "A2", "A2a", "A2b", "A1", "B");
    }

    [TestMethod]
    public void Fuzz_RandomMutations_AlwaysMatchReferenceFlatten()
    {
        // The order-statistic backing store is only trustworthy under adversarial interleavings — drive random
        // structural mutations and compare against a naive recursive flatten after every operation.
        var random = new System.Random(20260710);
        var roots = new ObservableCollection<ITableViewTreeItem>();
        using var source = new TreeTableViewSource(roots);
        var allNodes = new List<Node>();
        var counter = 0;

        Node NewNode(int depth)
        {
            var node = new Node($"N{counter++}", depth);
            node.SetChildren(new ObservableCollection<ITableViewTreeItem>());
            allNodes.Add(node);
            return node;
        }

        for (var op = 0; op < 1_000; op++)
        {
            switch (random.Next(5))
            {
                case 0: // insert a root
                    roots.Insert(random.Next(roots.Count + 1), NewNode(0));
                    break;

                case 1 when allNodes.Count > 0: // insert a child under a random node
                    var parent = allNodes[random.Next(allNodes.Count)];
                    var children = (ObservableCollection<ITableViewTreeItem>)parent.ChildrenSource!;
                    children.Insert(random.Next(children.Count + 1), NewNode(parent.Depth + 1));
                    break;

                case 2 when allNodes.Count > 0: // expand a random node
                    source.Expand(allNodes[random.Next(allNodes.Count)]);
                    break;

                case 3 when allNodes.Count > 0: // collapse a random node
                    source.Collapse(allNodes[random.Next(allNodes.Count)]);
                    break;

                case 4 when roots.Count > 0 && random.Next(3) == 0: // occasionally remove a root subtree
                    var index = random.Next(roots.Count);
                    Prune((Node)roots[index]);
                    roots.RemoveAt(index);
                    break;
            }

            var expected = roots.SelectMany(ReferenceFlatten).Cast<Node>().Select(n => n.Name).ToArray();
            var actual = source.Cast<Node>().Select(n => n.Name).ToArray();
            CollectionAssert.AreEqual(expected, actual, $"flat view diverged from reference at operation {op}");

            // Spot-check the indexed accessors against the enumeration order.
            if (source.Count > 0)
            {
                var probe = random.Next(source.Count);
                Assert.AreSame(actual[probe], ((Node)source[probe]).Name);
                Assert.AreEqual(probe, source.IndexOf(source[probe]));
            }
        }

        void Prune(Node node)
        {
            allNodes.Remove(node);
            foreach (var child in ((ObservableCollection<ITableViewTreeItem>)node.ChildrenSource!).Cast<Node>().ToList())
            {
                Prune(child);
            }
        }

        static IEnumerable<ITableViewTreeItem> ReferenceFlatten(ITableViewTreeItem item)
        {
            yield return item;

            if (item is ITableViewTreeItem { IsExpanded: true, ChildrenSource: { } children })
            {
                foreach (var descendant in children.SelectMany(ReferenceFlatten))
                {
                    yield return descendant;
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A(expanded): [A1, A2(expanded): [A2a]], B — the standard fixture.
    /// </summary>
    private static (TreeTableViewSource Source, Node A, ObservableCollection<ITableViewTreeItem> Roots) CreateTree()
    {
        // Typed collections assign directly thanks to IEnumerable<T> covariance — no object-typed casts needed.
        var a2 = new Node("A2", 1) { IsExpanded = true };
        a2.SetChildren(new ObservableCollection<ITableViewTreeItem> { new Node("A2a", 2) });

        var a = new Node("A", 0) { IsExpanded = true };
        a.SetChildren(new ObservableCollection<ITableViewTreeItem> { new Node("A1", 1), a2 });

        var roots = new ObservableCollection<ITableViewTreeItem> { a, new Node("B", 0) };

        return (new TreeTableViewSource(roots), a, roots);
    }

    private static void AssertFlat(TreeTableViewSource source, params string[] names)
        => CollectionAssert.AreEqual(names, source.Cast<Node>().Select(n => n.Name).ToArray());

    private static List<(CollectionChange, uint)> Record(TreeTableViewSource source)
    {
        var events = new List<(CollectionChange, uint)>();
        source.VectorChanged += (_, e) => events.Add((e.CollectionChange, e.Index));
        return events;
    }

    private sealed class Node(string name, int depth) : ITableViewTreeItem
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name { get; } = name;
        public int Depth { get; } = depth;
        public IList? Children { get; private set; }
        public IEnumerable<ITableViewTreeItem>? ChildrenSource { get; private set; }

        /// <summary>Backend-count style: children exist but are not loaded/derivable from the collection yet.</summary>
        public bool ForceHasChildren { get; init; }

        public bool HasChildren => ForceHasChildren || Children is { Count: > 0 };
        public bool IsFinalItem => false;

        public bool IsExpanded
        {
            get;
            set
            {
                if (field != value)
                {
                    field = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
                }
            }
        }

        public bool IsLoading => false;

        public void SetChildren(IEnumerable<ITableViewTreeItem> children)
        {
            ChildrenSource = children;
            Children = children as IList;
        }
    }

    /// <summary>
    /// Minimal IObservableVector-of-object collection for the native-notification children path. Note the extra
    /// <see cref="IEnumerable{T}"/> of <see cref="ITableViewTreeItem"/> implementation: an object-typed vector is
    /// not covariantly convertible to the typed enumerable, so a collection class meant to serve as ChildrenSource
    /// exposes the typed enumerator explicitly (one line) — typed .NET collections need nothing.
    /// </summary>
    private sealed class TestObservableVector : IObservableVector<object>, IEnumerable<ITableViewTreeItem>
    {
        private readonly List<object> _items = [];

        IEnumerator<ITableViewTreeItem> IEnumerable<ITableViewTreeItem>.GetEnumerator()
            => _items.Cast<ITableViewTreeItem>().GetEnumerator();

        public event VectorChangedEventHandler<object>? VectorChanged;

        public object this[int index]
        {
            get => _items[index];
            set { _items[index] = value; Raise(CollectionChange.ItemChanged, index); }
        }

        public int Count => _items.Count;
        public bool IsReadOnly => false;
        public void Add(object item) => Insert(_items.Count, item);
        public void Insert(int index, object item) { _items.Insert(index, item); Raise(CollectionChange.ItemInserted, index); }
        public void RemoveAt(int index) { _items.RemoveAt(index); Raise(CollectionChange.ItemRemoved, index); }
        public bool Remove(object item) { var i = _items.IndexOf(item); if (i < 0) return false; RemoveAt(i); return true; }
        public void Clear() { _items.Clear(); Raise(CollectionChange.Reset, 0); }
        public void ReplaceAll(IEnumerable<object> items) { _items.Clear(); _items.AddRange(items); Raise(CollectionChange.Reset, 0); }
        public bool Contains(object item) => _items.Contains(item);
        public void CopyTo(object[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
        public int IndexOf(object item) => _items.IndexOf(item);
        public IEnumerator<object> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

        private void Raise(CollectionChange change, int index)
            => VectorChanged?.Invoke(this, new Args(change, (uint)index));

        private sealed class Args(CollectionChange change, uint index) : IVectorChangedEventArgs
        {
            public CollectionChange CollectionChange => change;
            public uint Index => index;
        }
    }
}
