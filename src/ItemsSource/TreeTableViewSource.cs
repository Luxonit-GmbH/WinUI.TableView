using Microsoft.UI.Xaml.Data;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Windows.Foundation.Collections;

namespace WinUI.TableView;

/// <summary>
/// Flattens a nested (collection-in-collection) hierarchy of <see cref="ITableViewTreeNode"/> items into the flat,
/// read-only <see cref="IObservableVector{T}"/> view a <see cref="TreeTableView"/> binds to (use with
/// <see cref="TableView.UseCollectionView"/> = <see langword="false"/>).
/// </summary>
/// <remarks>
/// <para>The app keeps its natural shape: nodes own children collections in the app's ordering, and mutations go to
/// those collections — this adapter owns all flat-position math. While a node is expanded its children collection is
/// tracked live (<see cref="IObservableVector{T}"/> of object natively, or
/// <see cref="INotifyCollectionChanged"/>); collapsed branches cost nothing, so subscriptions scale with the number
/// of expanded nodes, not with data size.</para>
/// <para>Built for heavy streaming into expanded branches: rows live in an order-statistic structure and subtree
/// sizes are maintained incrementally, so a child insert/remove costs O(log visibleRows + depth) — independent of
/// how many rows are visible — instead of the linear scans a naive flat list would need.</para>
/// <para>Expansion flow: wire <see cref="TreeTableView.ExpandRequested"/> to (optionally fetch children, then) call
/// <see cref="Expand"/>, and <see cref="TreeTableView.CollapseRequested"/> to <see cref="Collapse"/>. Collapsing
/// keeps descendants' own expansion state, so re-expanding restores the previous shape.</para>
/// <para>Node instances must be unique within the visible tree (flat positions are resolved by reference).</para>
/// </remarks>
public partial class TreeTableViewSource : IObservableVector<object>, ISelectionInfo, IItemsRangeInfo, IDisposable
{
    private static readonly object RootsKey = new();

    private readonly IndexedRows _rows = new();
    private readonly Dictionary<object, IndexedRows.Row> _handles = [];
    private readonly Dictionary<object, int> _subtreeSizes = [];
    private readonly Dictionary<object, object> _parents = [];
    private readonly Dictionary<object, Branch> _branches = [];
    private readonly List<(int First, int Last)> _selection = []; // sorted, disjoint, inclusive flat-index intervals

    /// <inheritdoc/>
    public event VectorChangedEventHandler<object>? VectorChanged;

    /// <summary>
    /// Occurs when the platform reports new visible/tracked row ranges (see <see cref="VisibleRange"/> and
    /// <see cref="TrackedRanges"/>) — the authoritative feed for limiting updates to on-screen rows.
    /// </summary>
    public event EventHandler? RangesChanged;

    /// <summary>
    /// Initializes the source over the given root nodes. Pre-expanded nodes (with a populated
    /// <see cref="ITableViewTreeNode.ChildrenSource"/>) are flattened immediately.
    /// </summary>
    /// <param name="roots">The root nodes; tracked live when observable (see <see cref="ITableViewTreeNode.ChildrenSource"/>).</param>
    public TreeTableViewSource(IEnumerable<ITableViewTreeItem> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var branch = Subscribe(RootsKey, roots);
        var flatIndex = 0;

        foreach (var root in branch.Shadow)
        {
            flatIndex += InsertSubtree(flatIndex, root, RootsKey);
        }
    }

    /// <summary>
    /// Shows the node's children (sets <see cref="ITableViewTreeNode.IsExpanded"/> and splices its
    /// <see cref="ITableViewTreeNode.ChildrenSource"/> — including pre-expanded descendants — into the flat view).
    /// Call after the children collection is populated; typical wiring is from
    /// <see cref="TreeTableView.ExpandRequested"/>, after any asynchronous fetch completes.
    /// </summary>
    /// <param name="node">The node to expand.</param>
    public void Expand(ITableViewTreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (_branches.ContainsKey(node))
        {
            return;
        }

        node.IsExpanded = true;

        if (node.ChildrenSource is not { } children || !_handles.TryGetValue(node, out var handle))
        {
            return;
        }

        var branch = Subscribe(node, children);
        var flatIndex = _rows.IndexOf(handle) + 1;

        foreach (var child in branch.Shadow)
        {
            flatIndex += InsertSubtree(flatIndex, child, node);
        }
    }

    /// <summary>
    /// Hides the node's visible descendants and sets <see cref="ITableViewTreeNode.IsExpanded"/> to
    /// <see langword="false"/>. Descendants keep their own expansion state for a later re-expand.
    /// </summary>
    /// <param name="node">The node to collapse.</param>
    public void Collapse(ITableViewTreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (_branches.TryGetValue(node, out var branch))
        {
            foreach (var child in branch.Shadow.ToList())
            {
                RemoveSubtree(child);
            }

            Unsubscribe(branch);
        }

        node.IsExpanded = false;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Flattening core
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A tracked children collection: the roots, or an expanded node's ChildrenSource. The shadow list resolves
    /// removed items (vector change events carry only the index) and preserves child order.
    /// </summary>
    private sealed class Branch
    {
        public required object Key { get; init; }
        public required IEnumerable<ITableViewTreeItem> Source { get; init; }
        public List<object> Shadow { get; } = [];
        public VectorChangedEventHandler<object>? VectorHandler { get; set; }
        public NotifyCollectionChangedEventHandler? InccHandler { get; set; }
    }

    private Branch Subscribe(object key, IEnumerable<ITableViewTreeItem> source)
    {
        var branch = new Branch { Key = key, Source = source };
        branch.Shadow.AddRange(source);

        if (source is IObservableVector<object> vector)
        {
            branch.VectorHandler = (sender, args) => OnBranchVectorChanged(branch, sender, args);
            vector.VectorChanged += branch.VectorHandler;
        }
        else if (source is INotifyCollectionChanged incc)
        {
            branch.InccHandler = (_, e) => OnBranchCollectionChanged(branch, e);
            incc.CollectionChanged += branch.InccHandler;
        }

        _branches[key] = branch;
        return branch;
    }

    private void Unsubscribe(Branch branch)
    {
        if (branch.VectorHandler is not null && branch.Source is IObservableVector<object> vector)
        {
            vector.VectorChanged -= branch.VectorHandler;
        }
        else if (branch.InccHandler is not null && branch.Source is INotifyCollectionChanged incc)
        {
            incc.CollectionChanged -= branch.InccHandler;
        }

        _branches.Remove(branch.Key);
    }

    /// <summary>
    /// Collects the node and (recursively) its expanded, tracked descendants, recording parentage and storing each
    /// node's visible-subtree size. Returns the node's own visible size.
    /// </summary>
    private int BuildSubtree(object node, object parentKey, List<object> buffer)
    {
        buffer.Add(node);
        _parents[node] = parentKey;
        var size = 1;

        if (node is ITableViewTreeNode { IsExpanded: true, ChildrenSource: { } children } treeNode
            && !_branches.ContainsKey(treeNode))
        {
            var branch = Subscribe(treeNode, children);

            foreach (var child in branch.Shadow)
            {
                size += BuildSubtree(child, treeNode, buffer);
            }
        }

        _subtreeSizes[node] = size;
        return size;
    }

    /// <summary>
    /// Inserts the node's visible subtree at the flat index (one ItemInserted per row), grows the ancestors'
    /// stored sizes, and returns the number of rows inserted.
    /// </summary>
    private int InsertSubtree(int flatIndex, object node, object parentKey)
    {
        var buffer = new List<object>();
        var size = BuildSubtree(node, parentKey, buffer);

        for (var i = 0; i < buffer.Count; i++)
        {
            _handles[buffer[i]] = _rows.InsertAt(flatIndex + i, buffer[i]);
            ShiftSelectionForInsert(flatIndex + i);
            VectorChanged?.Invoke(this, new VectorChangedArgs(CollectionChange.ItemInserted, (uint)(flatIndex + i)));
        }

        GrowAncestors(parentKey, size);
        return size;
    }

    /// <summary>
    /// Removes the node's visible subtree from the flat view (one ItemRemoved per row), shrinks the ancestors'
    /// stored sizes, and drops the subscriptions of the node and its tracked descendants.
    /// </summary>
    private void RemoveSubtree(object node)
    {
        var size = _subtreeSizes[node];
        var parentKey = _parents[node];
        var flatIndex = _rows.IndexOf(_handles[node]);

        for (var i = 0; i < size; i++)
        {
            var handle = _rows.SelectAt(flatIndex);
            var value = handle.Value;

            _rows.Remove(handle);
            _handles.Remove(value);
            _subtreeSizes.Remove(value);
            _parents.Remove(value);
            ShiftSelectionForRemove(flatIndex);

            VectorChanged?.Invoke(this, new VectorChangedArgs(CollectionChange.ItemRemoved, (uint)flatIndex));
        }

        DropBranchesRecursive(node);
        GrowAncestors(parentKey, -size);
    }

    private void GrowAncestors(object key, int delta)
    {
        while (!ReferenceEquals(key, RootsKey))
        {
            _subtreeSizes[key] += delta;
            key = _parents[key];
        }
    }

    /// <summary>
    /// The item's 1-based position among its visible siblings and the sibling count, for UI Automation
    /// ("item 2 of 5"). Returns <see langword="null"/> when the item is not currently visible.
    /// </summary>
    internal (int Position, int Count)? GetSiblingInfo(object item)
    {
        if (!_parents.TryGetValue(item, out var parentKey) || !_branches.TryGetValue(parentKey, out var branch))
        {
            return null;
        }

        var index = branch.Shadow.IndexOf(item);
        return index < 0 ? null : (index + 1, branch.Shadow.Count);
    }

    private void DropBranchesRecursive(object node)
    {
        if (_branches.TryGetValue(node, out var branch))
        {
            foreach (var child in branch.Shadow)
            {
                DropBranchesRecursive(child);
            }

            Unsubscribe(branch);
        }
    }

    /// <summary>
    /// The flat index where the branch's child at the given position starts (or would start): the current occupant
    /// of that child position marks the spot; appends land right after the preceding sibling's subtree. All O(log n).
    /// </summary>
    private int ChildFlatIndex(Branch branch, int childIndex)
    {
        if (childIndex < branch.Shadow.Count)
        {
            return _rows.IndexOf(_handles[branch.Shadow[childIndex]]);
        }

        if (branch.Shadow.Count > 0)
        {
            var last = branch.Shadow[^1];
            return _rows.IndexOf(_handles[last]) + _subtreeSizes[last];
        }

        return ReferenceEquals(branch.Key, RootsKey) ? 0 : _rows.IndexOf(_handles[branch.Key]) + 1;
    }

    private void OnBranchVectorChanged(Branch branch, IObservableVector<object> sender, IVectorChangedEventArgs args)
    {
        var index = (int)args.Index;

        switch (args.CollectionChange)
        {
            case CollectionChange.ItemInserted:
                InsertChild(branch, index, sender[index]);
                break;
            case CollectionChange.ItemRemoved:
                RemoveChild(branch, index);
                break;
            case CollectionChange.ItemChanged:
                // ItemChanged = the slot was REPLACED (vector[i] = x), not moved. Some sources raise it with the
                // SAME reference as a "refresh" hint — property mutations flow through bindings, so replacing the
                // subtree would only cause needless row recycling; skip unless the instance actually changed.
                if (!ReferenceEquals(branch.Shadow[index], sender[index]))
                {
                    RemoveChild(branch, index);
                    InsertChild(branch, index, sender[index]);
                }
                break;
            case CollectionChange.Reset:
                RebuildBranch(branch);
                break;
        }
    }

    private void OnBranchCollectionChanged(Branch branch, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                for (var i = 0; i < e.NewItems.Count; i++)
                {
                    InsertChild(branch, e.NewStartingIndex + i, e.NewItems[i]!);
                }
                break;

            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                for (var i = 0; i < e.OldItems.Count; i++)
                {
                    RemoveChild(branch, e.OldStartingIndex);
                }
                break;

            case NotifyCollectionChangedAction.Replace when e.NewItems is not null:
                for (var i = 0; i < e.NewItems.Count; i++)
                {
                    RemoveChild(branch, e.NewStartingIndex + i);
                    InsertChild(branch, e.NewStartingIndex + i, e.NewItems[i]!);
                }
                break;

            case NotifyCollectionChangedAction.Move when e.OldItems is not null:
                for (var i = 0; i < e.OldItems.Count; i++)
                {
                    RemoveChild(branch, e.OldStartingIndex);
                }
                for (var i = 0; i < e.OldItems.Count; i++)
                {
                    InsertChild(branch, e.NewStartingIndex + i, e.OldItems[i]!);
                }
                break;

            case NotifyCollectionChangedAction.Reset:
                RebuildBranch(branch);
                break;
        }
    }

    private void InsertChild(Branch branch, int childIndex, object child)
    {
        var flatIndex = ChildFlatIndex(branch, childIndex);
        branch.Shadow.Insert(childIndex, child);
        InsertSubtree(flatIndex, child, branch.Key);
    }

    private void RemoveChild(Branch branch, int childIndex)
    {
        var child = branch.Shadow[childIndex];
        branch.Shadow.RemoveAt(childIndex);
        RemoveSubtree(child);
    }

    /// <summary>
    /// Handles a Reset ("everything may have changed") from a tracked collection. A Reset is deliberately NEVER
    /// forwarded to the flat view — that would make the ListView drop every realized container and reset the scroll
    /// position. Instead the new contents are diffed against the shadow by reference: the common prefix and suffix
    /// are left completely untouched (no events, subtrees and subscriptions kept) and only the changed middle window
    /// is removed/re-inserted. Batch appends/removes therefore cost only their own rows; a full reorder degenerates
    /// to rebuilding the window, which is unavoidable when the source does not say what changed.
    /// </summary>
    private void RebuildBranch(Branch branch)
    {
        var oldChildren = branch.Shadow;
        var newChildren = branch.Source.Cast<object>().ToList();

        var prefix = 0;
        while (prefix < oldChildren.Count && prefix < newChildren.Count
            && ReferenceEquals(oldChildren[prefix], newChildren[prefix]))
        {
            prefix++;
        }

        var oldEnd = oldChildren.Count;
        var newEnd = newChildren.Count;
        while (oldEnd > prefix && newEnd > prefix
            && ReferenceEquals(oldChildren[oldEnd - 1], newChildren[newEnd - 1]))
        {
            oldEnd--;
            newEnd--;
        }

        for (var i = oldEnd - 1; i >= prefix; i--)
        {
            RemoveChild(branch, i);
        }

        for (var i = prefix; i < newEnd; i++)
        {
            InsertChild(branch, i, newChildren[i]);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // ISelectionInfo — the ListView delegates selection bookkeeping here, so select/deselect-all are O(ranges)
    // instead of the platform's per-item teardown, and structural changes (expansion, streaming) shift the
    // selection automatically to follow the same items. Note: with an ISelectionInfo source, ListView's
    // SelectedItems stays empty by design — read TableView.SelectedRanges/SelectedValues instead.
    // ---------------------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public void SelectRange(ItemIndexRange itemIndexRange)
    {
        var first = Math.Max(0, itemIndexRange.FirstIndex);
        var last = Math.Min(Count - 1, itemIndexRange.LastIndex);

        if (last < first)
        {
            return;
        }

        var merged = new List<(int First, int Last)>();
        var i = 0;

        while (i < _selection.Count && _selection[i].Last < first - 1) merged.Add(_selection[i++]);
        while (i < _selection.Count && _selection[i].First <= last + 1)
        {
            first = Math.Min(first, _selection[i].First);
            last = Math.Max(last, _selection[i].Last);
            i++;
        }
        merged.Add((first, last));
        while (i < _selection.Count) merged.Add(_selection[i++]);

        _selection.Clear();
        _selection.AddRange(merged);
    }

    /// <inheritdoc/>
    public void DeselectRange(ItemIndexRange itemIndexRange)
    {
        var first = itemIndexRange.FirstIndex;
        var last = itemIndexRange.LastIndex;
        var split = new List<(int First, int Last)>();

        foreach (var range in _selection)
        {
            if (range.Last < first || range.First > last)
            {
                split.Add(range);
                continue;
            }

            if (range.First < first) split.Add((range.First, first - 1));
            if (range.Last > last) split.Add((last + 1, range.Last));
        }

        _selection.Clear();
        _selection.AddRange(split);
    }

    /// <inheritdoc/>
    public bool IsSelected(int index)
    {
        var lo = 0;
        var hi = _selection.Count - 1;

        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (index < _selection[mid].First) hi = mid - 1;
            else if (index > _selection[mid].Last) lo = mid + 1;
            else return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public IReadOnlyList<ItemIndexRange> GetSelectedRanges()
        => [.. _selection.Select(r => new ItemIndexRange(r.First, (uint)(r.Last - r.First + 1)))];

    /// <summary>
    /// Adjusts the selection intervals for a row inserted at the given flat index: selected rows keep their
    /// selection (indexes shift), and a row inserted INSIDE a selected range is NOT selected (the range splits).
    /// </summary>
    private void ShiftSelectionForInsert(int index)
    {
        for (var i = _selection.Count - 1; i >= 0; i--)
        {
            var (first, last) = _selection[i];

            if (first >= index)
            {
                _selection[i] = (first + 1, last + 1);
            }
            else if (last >= index)
            {
                _selection[i] = (first, index - 1);
                _selection.Insert(i + 1, (index + 1, last + 1));
            }
        }
    }

    /// <summary>
    /// Adjusts the selection intervals for a row removed at the given flat index.
    /// </summary>
    private void ShiftSelectionForRemove(int index)
    {
        for (var i = _selection.Count - 1; i >= 0; i--)
        {
            var (first, last) = _selection[i];

            if (first > index)
            {
                _selection[i] = (first - 1, last - 1);
            }
            else if (last >= index)
            {
                if (last - 1 < first)
                {
                    _selection.RemoveAt(i);
                }
                else
                {
                    _selection[i] = (first, last - 1);
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // IItemsRangeInfo — the platform pushes the visible/buffered row ranges here on every viewport change.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Gets the most recently reported on-screen row range, or <see langword="null"/> before the first report.
    /// </summary>
    public ItemIndexRange? VisibleRange { get; private set; }

    /// <summary>
    /// Gets the most recently reported tracked (visible + realization buffer) row ranges. Prefer these for
    /// update throttling so buffered rows are fresh when they scroll into view.
    /// </summary>
    public IReadOnlyList<ItemIndexRange> TrackedRanges { get; private set; } = [];

    void IItemsRangeInfo.RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
    {
        VisibleRange = visibleRange;
        TrackedRanges = trackedItems;
        RangesChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------------------------------------------------
    // IObservableVector<object> — a read-only flat view; mutate the hierarchical sources instead.
    // ---------------------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public object this[int index]
    {
        get => _rows[index];
        set => throw new NotSupportedException("The flat view is read-only; mutate the hierarchical sources.");
    }

    /// <inheritdoc/>
    public int Count => _rows.Count;

    /// <inheritdoc/>
    public bool IsReadOnly => true;

    /// <inheritdoc/>
    public bool Contains(object item) => _handles.ContainsKey(item);

    /// <inheritdoc/>
    public void CopyTo(object[] array, int arrayIndex)
    {
        foreach (var value in this)
        {
            array[arrayIndex++] = value;
        }
    }

    /// <inheritdoc/>
    public int IndexOf(object item) => _handles.TryGetValue(item, out var handle) ? _rows.IndexOf(handle) : -1;

    /// <inheritdoc/>
    public IEnumerator<object> GetEnumerator() => _rows.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _rows.GetEnumerator();

    /// <inheritdoc/>
    public void Add(object item) => throw new NotSupportedException("The flat view is read-only; mutate the hierarchical sources.");

    /// <inheritdoc/>
    public void Insert(int index, object item) => throw new NotSupportedException("The flat view is read-only; mutate the hierarchical sources.");

    /// <inheritdoc/>
    public bool Remove(object item) => throw new NotSupportedException("The flat view is read-only; mutate the hierarchical sources.");

    /// <inheritdoc/>
    public void RemoveAt(int index) => throw new NotSupportedException("The flat view is read-only; mutate the hierarchical sources.");

    /// <inheritdoc/>
    public void Clear() => throw new NotSupportedException("The flat view is read-only; mutate the hierarchical sources.");

    /// <summary>
    /// Detaches from every tracked collection.
    /// </summary>
    public void Dispose()
    {
        foreach (var branch in _branches.Values.ToList())
        {
            Unsubscribe(branch);
        }

        GC.SuppressFinalize(this);
    }

    private sealed class VectorChangedArgs(CollectionChange change, uint index) : IVectorChangedEventArgs
    {
        public CollectionChange CollectionChange => change;
        public uint Index => index;
    }
}
