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
using Windows.Foundation.Collections;

namespace WinUI.TableView.Tests;

/// <summary>
/// Covers <see cref="TreeTableViewSource"/>: the library-side flattening adapter over nested
/// (collection-in-collection) hierarchies. The app mutates the NESTED collections only; the adapter owns all flat
/// index math and raises native VectorChanged for the grid.
/// </summary>
[TestClass]
public partial class TreeTableViewSourceTests
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
    public void TreeItemsSource_AcceptsAnyEnumerable_RejectsOnlyNonEnumerables()
    {
        var treeTableView = new TreeTableView();

        // Items are deliberately NOT type-checked: rows without ITableViewTreeItem are plain leaves.
        treeTableView.TreeItemsSource = new List<int> { 1, 2, 3 };
        Assert.AreEqual(3, treeTableView.TreeSource!.Count);

        // Only a value that cannot be enumerated at all fails fast.
        Assert.ThrowsExactly<ArgumentException>(() => treeTableView.TreeItemsSource = new object());
    }

    [UITestMethod]
    public async Task MixedCollection_PlainRowsAndTreeItems_ExpandWorks_PlainRowsShowNoChevron()
    {
        // The app's model: only expandable items implement ITableViewTreeItem; plain rows implement nothing.
        var group = new Node("Group", 0) { ForceHasChildren = true };
        group.SetChildren(new ObservableCollection<ITableViewTreeItem> { new Node("Child", 1) });
        var roots = new TestObservableVector { "plain leaf", group }; // IObservableVector<object>, mixed items

        var treeTableView = new TreeTableView { AutoGenerateColumns = false, Width = 600, Height = 400 };
        treeTableView.Columns.Add(new TableViewTreeColumn
        {
            Header = "Name",
            Width = new GridLength(250, GridUnitType.Pixel),
        });
        treeTableView.TreeItemsSource = roots;

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(treeTableView);
        treeTableView.UpdateLayout();

        Assert.AreEqual(2, treeTableView.Items.Count);

        // Expanding the interface-implementing item works (auto-expansion, no handlers).
        treeTableView.RequestExpandCollapse(group, 1, expand: true);
        treeTableView.UpdateLayout();
        Assert.AreEqual(3, treeTableView.Items.Count);

        // Let the dispatcher deliver the (asynchronously raised) DataContextChanged/Loaded notifications the
        // virtualization churn produced before asserting the visuals they drive.
        await Task.Yield();
        await Task.Yield();
        treeTableView.UpdateLayout();

        // The plain row's tree cell fails CLOSED: chevron glyph collapsed via binding FallbackValue.
        var plainRow = (TableViewRow)treeTableView.ContainerFromIndex(0)!;
        var plainChevronGlyph = ((Grid)((Button)((Grid)plainRow.Cells[0].Content).Children[1]).Content).Children[0];
        Assert.AreEqual(Visibility.Collapsed, plainChevronGlyph.Visibility);

        var groupRow = (TableViewRow)treeTableView.ContainerFromIndex(1)!;
        var groupCellContent = (Grid)groupRow.Cells[0].Content;
        var groupChevronGlyph = ((Grid)((Button)groupCellContent.Children[1]).Content).Children[0];
        Assert.AreEqual(Visibility.Visible, groupChevronGlyph.Visibility,
            $"diag: rowContent={groupRow.Content?.GetType().Name}, sameAsGroup={ReferenceEquals(groupRow.Content, group)}, " +
            $"cellDataContext={groupCellContent.DataContext?.GetType().Name ?? "null"}, " +
            $"dcSameAsGroup={ReferenceEquals(groupCellContent.DataContext, group)}, hasChildren={group.HasChildren}");

        // And the double-click path does not consume the gesture on a plain row.
        Assert.IsFalse(treeTableView.ToggleExpandCollapseFromCell(plainRow.Cells[0]));

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
                foreach (var descendant in children.Cast<ITableViewTreeItem>().SelectMany(ReferenceFlatten))
                {
                    yield return descendant;
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Bulk coalescing
    // ---------------------------------------------------------------------------------------------------------

    [TestMethod]
    public void BulkUpdateScope_CoalescesAppDrivenRemovals_IntoOneReset()
    {
        var (source, a, _) = CreateTree();
        var children = (ObservableCollection<ITableViewTreeItem>)a.ChildrenSource!;

        for (var i = 0; i < 40; i++)
        {
            children.Add(new Node($"AX{i}", 1));
        }

        var events = Record(source);

        using (source.BeginBulkUpdate())
        {
            while (children.Count > 0)
            {
                children.RemoveAt(children.Count - 1);
            }
        }

        CollectionAssert.AreEqual(
            new[] { (CollectionChange.Reset, 0u) },
            events,
            "removing children one call at a time inside a bulk scope must reach the grid as a single change");

        AssertFlat(source, "A", "B");
    }

    [TestMethod]
    public void BulkUpdateScope_IsReentrant_AndFlushesOnlyOnce()
    {
        var (source, a, _) = CreateTree();
        var children = (ObservableCollection<ITableViewTreeItem>)a.ChildrenSource!;
        var events = Record(source);

        using (source.BeginBulkUpdate())
        {
            children.Add(new Node("AX", 1));

            using (source.BeginBulkUpdate())
            {
                children.Add(new Node("AY", 1));
            }

            children.Add(new Node("AZ", 1));
        }

        Assert.AreEqual(1, events.Count, "the inner scope must not flush; only the outermost one does");
        Assert.AreEqual(CollectionChange.Reset, events[0].Item1);
        AssertFlat(source, "A", "A1", "A2", "A2a", "AX", "AY", "AZ", "B");
    }

    [TestMethod]
    public void BulkUpdateScope_WithNoChanges_RaisesNothing()
    {
        var (source, _, _) = CreateTree();
        var events = Record(source);

        using (source.BeginBulkUpdate())
        {
        }

        Assert.AreEqual(0, events.Count);
    }

    [TestMethod]
    public void BranchReset_AboveThreshold_CoalescesIntoOneReset()
    {
        var (source, a, _) = CreateTree();
        var children = (ObservableCollection<ITableViewTreeItem>)a.ChildrenSource!;

        for (var i = 0; i < 40; i++)
        {
            children.Add(new Node($"AX{i}", 1));
        }

        var events = Record(source);

        children.Clear(); // ObservableCollection raises a single Reset

        Assert.AreEqual(1, events.Count, "a large branch reset must not be replayed as one event per removed row");
        Assert.AreEqual(CollectionChange.Reset, events[0].Item1);
        AssertFlat(source, "A", "B");
    }

    [TestMethod]
    public void BranchReset_BelowThreshold_StaysGranular()
    {
        var (source, a, _) = CreateTree();
        var children = (ObservableCollection<ITableViewTreeItem>)a.ChildrenSource!;
        var events = Record(source);

        children.Clear(); // only 2 children — below BulkChangeThreshold

        // Small resets stay granular on purpose: a Reset makes the host drop every realized container and
        // reset the scroll position, which is far more expensive than a handful of removals.
        Assert.IsFalse(
            events.Any(e => e.Item1 == CollectionChange.Reset),
            "small branch resets must stay granular so the host keeps its containers and scroll position");
        AssertFlat(source, "A", "B");
    }

    [UITestMethod]
    public async Task TreeSelection_SelectedItems_IsPopulated_NotNull()
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

        treeTableView.ItemsSource = source;
        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(treeTableView);
        treeTableView.UpdateLayout();

        treeTableView.SelectAll();
        await Task.Yield();

        // The platform leaves ITS collection null when the source implements ISelectionInfo, so consumer code that
        // works on a flat grid used to throw a NullReferenceException here.
        Assert.IsNotNull(treeTableView.SelectedItems);
        CollectionAssert.AreEqual(
            new[] { "A", "B" },
            treeTableView.SelectedItems.Cast<Node>().Select(n => n.Name).ToArray());

        // Expanding inserts unselected children; the snapshot must reflect the selection, not the row count.
        treeTableView.RequestExpandCollapse(a, 0, expand: true);
        await Task.Yield();

        Assert.AreEqual(5, treeTableView.Items.Count);
        CollectionAssert.AreEqual(
            new[] { "A", "B" },
            treeTableView.SelectedItems.Cast<Node>().Select(n => n.Name).ToArray());

        // Read-only by design: the selection lives in the source, so mutating a copy could not select anything.
        Assert.ThrowsExactly<NotSupportedException>(() => treeTableView.SelectedItems.Add(new Node("Z", 0)));

        treeTableView.DeselectAll();
        await Task.Yield();
        Assert.AreEqual(0, treeTableView.SelectedItems.Count);

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(treeTableView);
    }

    [UITestMethod]
    public async Task LoadingBranch_DoesNotBlockExpandingOtherBranches()
    {
        var slow = new LoadingNode("Slow");
        var other = new LoadingNode("Other");
        other.SetChildren(new ObservableCollection<ITableViewTreeItem> { new Node("OtherChild", 1) });

        var source = new TreeTableViewSource(new ObservableCollection<ITableViewTreeItem> { slow, other });

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
            Binding = new Binding { Path = new PropertyPath(nameof(LoadingNode.Name)) },
        });

        // The async pattern: the handler starts a fetch, flips IsLoading, and cancels so the rows are spliced only
        // once the children arrive.
        var expandRequests = new List<string>();
        treeTableView.ExpandRequested += (_, e) =>
        {
            var node = (LoadingNode)e.Item;
            expandRequests.Add(node.Name);

            if (node.LoadsSlowly)
            {
                node.IsLoading = true;
                e.Cancel = true;
            }
        };

        treeTableView.ItemsSource = source;
        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(treeTableView);
        treeTableView.UpdateLayout();

        treeTableView.RequestExpandCollapse(slow, 0, expand: true);
        await Task.Yield();

        Assert.IsTrue(slow.IsLoading, "the fetch for the first branch is still in flight");
        Assert.AreEqual(2, treeTableView.Items.Count, "a pending fetch must not splice rows yet");

        // A SECOND branch, expanded while the first one is still loading.
        treeTableView.RequestExpandCollapse(other, 1, expand: true);
        await Task.Yield();
        treeTableView.UpdateLayout();

        CollectionAssert.AreEqual(new[] { "Slow", "Other" }, expandRequests,
            "a branch loading elsewhere must not suppress the request for another branch");
        Assert.AreEqual(3, treeTableView.Items.Count, "the second branch must expand while the first is loading");
        Assert.IsTrue(other.IsExpanded);

        // ...and the gate that DOES apply is the loading item's own: clicking it again is still a no-op.
        expandRequests.Clear();
        treeTableView.RequestExpandCollapse(slow, 0, expand: true);
        await Task.Yield();

        Assert.AreEqual(0, expandRequests.Count, "the loading item itself stays gated");

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(treeTableView);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Item uniqueness — rows are identified by reference, so a repeated instance is reported where it is
    // introduced instead of crashing later on an unrelated removal.
    // ---------------------------------------------------------------------------------------------------------

    [TestMethod]
    public void DuplicateChild_InTheSameCollection_ThrowsNamingTheItem()
    {
        var (source, a, _) = CreateTree();
        var children = (ObservableCollection<ITableViewTreeItem>)a.ChildrenSource!;
        var a1 = (Node)children[0];

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => children.Add(a1));

        StringAssert.Contains(error.Message, "A1", "the message must name the offending item");
        StringAssert.Contains(error.Message, nameof(TreeTableViewSource));
    }

    [TestMethod]
    public void DuplicateChild_UnderADifferentParent_Throws()
    {
        var (source, a, roots) = CreateTree();
        var a1 = (Node)((ObservableCollection<ITableViewTreeItem>)a.ChildrenSource!)[0];

        var b = (Node)roots[1];
        var bChildren = new ObservableCollection<ITableViewTreeItem>();
        b.SetChildren(bChildren);
        source.Expand(b);

        Assert.ThrowsExactly<InvalidOperationException>(() => bChildren.Add(a1));
    }

    [TestMethod]
    public void DuplicateRoot_InTheConstructorInput_Throws()
    {
        var shared = new Node("Shared", 0);
        var roots = new ObservableCollection<ITableViewTreeItem> { shared, new Node("B", 0), shared };

        Assert.ThrowsExactly<InvalidOperationException>(() => new TreeTableViewSource(roots));
    }

    [TestMethod]
    public void DuplicateNestedInAPreExpandedSubtree_ThrowsOnExpand()
    {
        var (source, a, roots) = CreateTree();
        var a2a = (Node)((ObservableCollection<ITableViewTreeItem>)
            ((Node)((ObservableCollection<ITableViewTreeItem>)a.ChildrenSource!)[1]).ChildrenSource!)[0];

        // B's child is itself pre-expanded and holds an item that is already visible under A.
        var bChild = new Node("BChild", 1) { IsExpanded = true };
        bChild.SetChildren(new ObservableCollection<ITableViewTreeItem> { a2a });

        var b = (Node)roots[1];
        b.SetChildren(new ObservableCollection<ITableViewTreeItem> { bChild });

        Assert.ThrowsExactly<InvalidOperationException>(() => source.Expand(b));
    }

    [TestMethod]
    public void RejectedDuplicate_AddsNoRow_AndKeepsTheFirstOccurrence()
    {
        var (source, a, _) = CreateTree();
        var children = (ObservableCollection<ITableViewTreeItem>)a.ChildrenSource!;
        var a1 = (Node)children[0];

        Assert.ThrowsExactly<InvalidOperationException>(() => children.Add(a1));

        // No phantom row, and the instance still resolves to its original position.
        AssertFlat(source, "A", "A1", "A2", "A2a", "B");
        Assert.AreEqual(1, source.IndexOf(a1));
    }

    [TestMethod]
    public void RejectedDuplicate_ResyncsTheBranch_OnTheNextChange()
    {
        var (source, a, _) = CreateTree();
        var children = (ObservableCollection<ITableViewTreeItem>)a.ChildrenSource!;
        var a1 = (Node)children[0];

        // ObservableCollection commits the add before it notifies, so after the rejection the source holds an item
        // the adapter refused — every later positional event from it would be off by one.
        Assert.ThrowsExactly<InvalidOperationException>(() => children.Add(a1));

        children.RemoveAt(children.Count - 1); // the app drops the duplicate it should never have added

        AssertFlat(source, "A", "A1", "A2", "A2a", "B");

        // ...and the branch is fully live again.
        var ax = new Node("AX", 1);
        children.Add(ax);
        AssertFlat(source, "A", "A1", "A2", "A2a", "AX", "B");

        children.Remove(ax);
        AssertFlat(source, "A", "A1", "A2", "A2a", "B");

        source.Collapse(a);
        source.Expand(a);
        AssertFlat(source, "A", "A1", "A2", "A2a", "B");
    }

    [TestMethod]
    public void RejectedDuplicate_StillPresent_ThrowsAgainOnResync_NeverRemovesTheWrongRow()
    {
        var (source, a, _) = CreateTree();
        var children = (ObservableCollection<ITableViewTreeItem>)a.ChildrenSource!;
        var a1 = (Node)children[0];

        Assert.ThrowsExactly<InvalidOperationException>(() => children.Add(a1));

        // The app ignores the error and keeps streaming. The resync re-derives from the source, which still holds
        // the duplicate, so it fails the same way instead of silently removing an unrelated row.
        Assert.ThrowsExactly<InvalidOperationException>(() => children.Add(new Node("AX", 1)));

        Assert.AreEqual(1, source.IndexOf(a1), "the original occupant must keep its row");
    }

    [TestMethod]
    public void DuplicateTwiceInOnePreExpandedCollection_ReportsTheDuplicate_NotANullReference()
    {
        // Both occupants are registered by the SAME insert walk, so the first one has no row yet when the second is
        // rejected — asking for its row index would throw and mask the diagnostic.
        var repeated = new Node("Twice", 1);
        var parent = new Node("P", 0) { IsExpanded = true };
        parent.SetChildren(new ObservableCollection<ITableViewTreeItem> { repeated, repeated });

        var error = Assert.ThrowsExactly<InvalidOperationException>(
            () => new TreeTableViewSource(new ObservableCollection<ITableViewTreeItem> { parent }));

        StringAssert.Contains(error.Message, "Twice");
    }

    [TestMethod]
    public void FailedExpand_LeavesNoHalfSplicedBranch()
    {
        var (source, a, roots) = CreateTree();
        var a1 = (Node)((ObservableCollection<ITableViewTreeItem>)a.ChildrenSource!)[0];

        var b = (Node)roots[1];
        // Good children first, the offender last: a per-child check would splice the good ones in and then fail.
        b.SetChildren(new ObservableCollection<ITableViewTreeItem> { new Node("B1", 1), new Node("B2", 1), a1 });

        Assert.ThrowsExactly<InvalidOperationException>(() => source.Expand(b));

        AssertFlat(source, "A", "A1", "A2", "A2a", "B");
        Assert.IsFalse(b.IsExpanded, "a rejected expand must not leave the item claiming to be expanded");

        // The real regression this guards: collapsing after a failed expand used to hit the missing bookkeeping.
        source.Collapse(b);
        AssertFlat(source, "A", "A1", "A2", "A2a", "B");
    }

    [TestMethod]
    public void FailedResync_KeepsTheTail_RatherThanAmputatingIt()
    {
        var shared = new Node("Shared", 0);
        var parent = new Node("P", 0) { IsExpanded = true };
        var children = new ObservableCollection<ITableViewTreeItem>
        {
            new Node("C0", 1), new Node("C1", 1), new Node("C2", 1), new Node("C3", 1),
        };
        parent.SetChildren(children);

        var source = new TreeTableViewSource(
            new ObservableCollection<ITableViewTreeItem> { parent, shared });

        // A duplicate lands MID-list, so the resync's changed window spans the rest of the branch.
        Assert.ThrowsExactly<InvalidOperationException>(() => children.Insert(2, shared));

        // The app keeps streaming; the resync re-derives, finds the duplicate still there, and must refuse WITHOUT
        // having already deleted the window it was going to rebuild.
        Assert.ThrowsExactly<InvalidOperationException>(() => children.Add(new Node("C4", 1)));

        AssertFlat(source, "P", "C0", "C1", "C2", "C3", "Shared");
    }

    [TestMethod]
    public void DistinctButEqualItems_AreNotDuplicates()
    {
        // Value-equality view models (records are the common case) must still get one row each: the tree is keyed
        // by reference, not by Equals.
        var first = new ValueEqualNode("same");
        var second = new ValueEqualNode("same");
        Assert.AreEqual(first, second, "the fixture must actually compare equal for this test to mean anything");

        var roots = new ObservableCollection<ITableViewTreeItem> { first, second };
        var source = new TreeTableViewSource(roots);

        Assert.AreEqual(2, source.Count);
        Assert.AreEqual(0, source.IndexOf(first));
        Assert.AreEqual(1, source.IndexOf(second));

        roots.Add(new ValueEqualNode("same"));
        Assert.AreEqual(3, source.Count);
    }

    [TestMethod]
    public void SameInstance_RemovedThenReinserted_IsNotADuplicate()
    {
        var (source, a, _) = CreateTree();
        var children = (ObservableCollection<ITableViewTreeItem>)a.ChildrenSource!;
        var a1 = (Node)children[0];

        // The app's resort/refilter shape: remove + insert the SAME instance. The removal drops its entry first,
        // so this must never trip the uniqueness guard.
        children.RemoveAt(0);
        children.Add(a1);

        AssertFlat(source, "A", "A2", "A2a", "A1", "B");
    }

    [TestMethod]
    public void ResetThatReordersTheSameInstances_IsNotADuplicate()
    {
        var (source, a, _) = CreateTree();
        var children = (ObservableCollection<ITableViewTreeItem>)a.ChildrenSource!;
        var a1 = children[0];
        var a2 = children[1];

        // Clear + refill raises Reset events; RebuildBranch must remove the changed window before re-inserting it.
        children.Clear();
        children.Add(a2);
        children.Add(a1);

        AssertFlat(source, "A", "A2", "A2a", "A1", "B");
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

    // Source-generated binding metadata: the tree column's {Binding} paths (HasChildren/IsExpanded/...) must
    // resolve on this model inside the test host, where plain reflection binding is unreliable.
    [WinRT.GeneratedBindableCustomProperty]
    public sealed partial class Node(string name, int depth) : ITableViewTreeItem
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name { get; } = name;
        public int Depth { get; } = depth;
        public IList? Children { get; private set; }
        public IEnumerable? ChildrenSource { get; private set; }

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

        public void SetChildren(IEnumerable children)
        {
            ChildrenSource = children;
            Children = children as IList;
        }

        // Diagnostics quote the item; a view model that says something useful here gets a useful message.
        public override string ToString() => Name;
    }

    /// <summary>
    /// A node with a settable IsLoading, for the async-expansion flow. "Slow" nodes model a fetch still in flight.
    /// </summary>
    [WinRT.GeneratedBindableCustomProperty]
    public sealed partial class LoadingNode(string name) : ITableViewTreeItem
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name { get; } = name;
        public int Depth => 0;
        public IEnumerable? ChildrenSource { get; private set; }
        public bool HasChildren => true; // a backend child COUNT is known before the children themselves
        public bool IsFinalItem => false;
        public bool LoadsSlowly => ChildrenSource is null;

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

        public bool IsLoading
        {
            get;
            set
            {
                if (field != value)
                {
                    field = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoading)));
                }
            }
        }

        public void SetChildren(IEnumerable children) => ChildrenSource = children;

        public override string ToString() => Name;
    }

    /// <summary>
    /// A leaf whose Equals/GetHashCode are value-based, like a record. Distinct instances of it must still get
    /// distinct rows.
    /// </summary>
    private sealed class ValueEqualNode(string name) : ITableViewTreeItem
    {
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

        public string Name { get; } = name;
        public int Depth => 0;
        public IEnumerable? ChildrenSource => null;
        public bool HasChildren => false;
        public bool IsFinalItem => true;
        public bool IsExpanded { get => false; set { } }
        public bool IsLoading => false;

        public override bool Equals(object? obj) => obj is ValueEqualNode other && other.Name == Name;
        public override int GetHashCode() => Name.GetHashCode();
        public override string ToString() => Name;
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
