using Microsoft.UI.Xaml.Data;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Foundation.Collections;

namespace WinUI.TableView;

/// <summary>
/// Flattens a nested (collection-in-collection) hierarchy of <see cref="ITableViewTreeItem"/> items into the flat,
/// read-only <see cref="IObservableVector{T}"/> view a <see cref="TreeTableView"/> binds to (use with
/// <see cref="TableView.UseCollectionView"/> = <see langword="false"/>).
/// </summary>
/// <remarks>
/// <para>The app keeps its natural shape: items own children collections in the app's ordering, and mutations go to
/// those collections — this adapter owns all flat-position math. While an item is expanded its children collection
/// is tracked live (<see cref="IObservableVector{T}"/> of object natively, or
/// <see cref="INotifyCollectionChanged"/>); collapsed branches cost nothing, so subscriptions scale with the number
/// of expanded items, not with data size.</para>
/// <para>Built for heavy streaming into expanded branches: rows live in an order-statistic structure and per-item
/// bookkeeping (row handle, visible-subtree size, parent, branch subscription) is a SINGLE dictionary entry, so a
/// child insert/remove costs O(log visibleRows + depth) with one hash lookup per touched item.</para>
/// <para>Expansion flow: wire <see cref="TreeTableView.ExpandRequested"/> to (optionally fetch children, then) call
/// <see cref="Expand"/>, and <see cref="TreeTableView.CollapseRequested"/> to <see cref="Collapse"/>. Collapsing
/// keeps descendants' own expansion state, so re-expanding restores the previous shape. Items with
/// <see cref="ITableViewTreeItem.IsFinalItem"/> are never expanded.</para>
/// <para>Item instances must be unique within the visible tree (flat positions are resolved by reference), and all
/// mutations must happen on the UI thread.</para>
/// </remarks>
public partial class TreeTableViewSource : IObservableVector<object>, ISelectionInfo, IItemsRangeInfo, IDisposable
{
    private static readonly object RootsKey = new();

    private readonly IndexedRows _rows = new();
    private readonly Dictionary<object, NodeEntry> _nodes = [];
    private readonly List<(int First, int Last)> _selection = []; // sorted, disjoint, inclusive flat-index intervals
    private Branch? _rootsBranch;

    /// <inheritdoc/>
    public event VectorChangedEventHandler<object>? VectorChanged;

    private int _bulkDepth;
    private bool _bulkChanged;

    /// <summary>
    /// Gets or sets how many row changes a single structural operation (expanding or collapsing a branch) may
    /// report individually before they are coalesced into ONE reset notification. Defaults to 32.
    /// </summary>
    /// <remarks>
    /// Every per-row notification makes the host ListView run a virtualization and measure pass, so collapsing a
    /// branch with thousands of visible descendants one row at a time freezes the UI for seconds. Above this
    /// threshold the adapter raises a single reset instead: the ListView then regenerates only the containers in
    /// view. Set to <see cref="int.MaxValue"/> to always report row by row.
    /// </remarks>
    public int BulkChangeThreshold { get; set; } = 32;

    /// <summary>
    /// Suppresses per-row notifications for the duration of a bulk operation; the matching
    /// <see cref="EndBulk"/> raises one reset if anything actually changed.
    /// </summary>
    private void BeginBulk() => _bulkDepth++;

    private void EndBulk()
    {
        if (--_bulkDepth > 0 || !_bulkChanged)
        {
            return;
        }

        _bulkChanged = false;
        VectorChanged?.Invoke(this, new VectorChangedArgs(CollectionChange.Reset, 0));
    }

    private void RaiseVectorChanged(CollectionChange change, int index)
    {
        if (_bulkDepth > 0)
        {
            _bulkChanged = true;
            return;
        }

        VectorChanged?.Invoke(this, new VectorChangedArgs(change, (uint)index));
    }

    /// <summary>
    /// Occurs when the platform reports new visible/tracked row ranges (see <see cref="VisibleRange"/> and
    /// <see cref="TrackedRanges"/>) — the authoritative feed for limiting updates to on-screen rows.
    /// </summary>
    public event EventHandler? RangesChanged;

    /// <summary>
    /// Initializes the source over the given root items. Roots may MIX expandable items (implementing
    /// <see cref="ITableViewTreeItem"/>) with plain leaf rows; pre-expanded items (with a populated
    /// <see cref="ITableViewTreeItem.ChildrenSource"/>) are flattened immediately.
    /// </summary>
    /// <param name="roots">The root items; tracked live when observable (see <see cref="ITableViewTreeItem.ChildrenSource"/>).</param>
    public TreeTableViewSource(IEnumerable roots)
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
    /// Shows the item's children (sets <see cref="ITableViewTreeItem.IsExpanded"/> and splices its
    /// <see cref="ITableViewTreeItem.ChildrenSource"/> — including pre-expanded descendants — into the flat view).
    /// Call after the children collection is populated; typical wiring is from
    /// <see cref="TreeTableView.ExpandRequested"/>, after any asynchronous fetch completes. No-op for
    /// <see cref="ITableViewTreeItem.IsFinalItem"/> items.
    /// </summary>
    /// <param name="item">The item to expand.</param>
    public void Expand(ITableViewTreeItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.IsFinalItem || GetBranch(item) is not null)
        {
            return;
        }

        item.IsExpanded = true;

        if (item.ChildrenSource is not { } children || !_nodes.TryGetValue(item, out var entry))
        {
            return;
        }

        var branch = Subscribe(item, children);
        var flatIndex = _rows.IndexOf(entry.Handle) + 1;

        // Coalesce when the branch is big: one reset costs the host a single viewport regeneration, whereas one
        // notification per row makes it run a full virtualization + measure pass per row.
        var bulk = branch.Shadow.Count > BulkChangeThreshold;

        if (bulk)
        {
            BeginBulk();
        }

        try
        {
            foreach (var child in branch.Shadow)
            {
                flatIndex += InsertSubtree(flatIndex, child, item);
            }
        }
        finally
        {
            if (bulk)
            {
                EndBulk();
            }
        }
    }

    /// <summary>
    /// Hides the item's visible descendants and sets <see cref="ITableViewTreeItem.IsExpanded"/> to
    /// <see langword="false"/>. Descendants keep their own expansion state for a later re-expand.
    /// </summary>
    /// <param name="item">The item to collapse.</param>
    public void Collapse(ITableViewTreeItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.IsFinalItem)
        {
            return;
        }

        if (GetBranch(item) is { } branch)
        {
            // The whole visible subtree disappears at once; report it as a single change when it is large (see
            // BulkChangeThreshold) — collapsing thousands of rows one notification at a time is what made the UI
            // freeze, with the host re-running virtualization and measure for every removed row.
            var removing = _nodes.TryGetValue(item, out var entry) ? entry.SubtreeSize - 1 : 0;
            var bulk = removing > BulkChangeThreshold;

            if (bulk)
            {
                BeginBulk();
            }

            try
            {
                foreach (var child in branch.Shadow.ToList())
                {
                    RemoveSubtree(child);
                }
            }
            finally
            {
                if (bulk)
                {
                    EndBulk();
                }
            }

            Unsubscribe(branch);
        }

        item.IsExpanded = false;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Flattening core
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// All per-visible-item bookkeeping in one dictionary slot: the flat-row handle, the visible-subtree size,
    /// the parent key, and (while expanded) the children subscription — one hash lookup per touched item.
    /// </summary>
    private struct NodeEntry
    {
        public IndexedRows.Row Handle;
        public int SubtreeSize;
        public object ParentKey;
        public Branch? Branch;
    }

    /// <summary>
    /// A tracked children collection: the roots, or an expanded item's ChildrenSource. The shadow list resolves
    /// removed items (vector change events carry only the index) and preserves child order.
    /// </summary>
    private sealed class Branch
    {
        public required object Key { get; init; }
        public required IEnumerable Source { get; init; }
        public List<object> Shadow { get; } = [];
        public VectorChangedEventHandler<object>? VectorHandler { get; set; }
        public NotifyCollectionChangedEventHandler? InccHandler { get; set; }
    }

    private Branch? GetBranch(object key)
        => ReferenceEquals(key, RootsKey) ? _rootsBranch
            : _nodes.TryGetValue(key, out var entry) ? entry.Branch : null;

    private void SetBranch(object key, Branch? branch)
    {
        if (ReferenceEquals(key, RootsKey))
        {
            _rootsBranch = branch;
            return;
        }

        ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_nodes, key);

        if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref entry))
        {
            entry.Branch = branch;
        }
    }

    private Branch Subscribe(object key, IEnumerable source)
    {
        var branch = new Branch { Key = key, Source = source };
        branch.Shadow.AddRange(source.Cast<object>());

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

        SetBranch(key, branch);
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

        SetBranch(branch.Key, null);
    }

    /// <summary>
    /// Collects the item and (recursively) its expanded, tracked descendants into the buffer, creating each item's
    /// bookkeeping entry (parent, subtree size, branch subscription) along the way. Returns the visible size.
    /// </summary>
    private int BuildSubtree(object node, object parentKey, List<object> buffer)
    {
        buffer.Add(node);
        _nodes[node] = new NodeEntry { ParentKey = parentKey };
        var size = 1;

        if (node is ITableViewTreeItem { IsFinalItem: false, IsExpanded: true, ChildrenSource: { } children } item
            && GetBranch(item) is null)
        {
            var branch = Subscribe(item, children);

            foreach (var child in branch.Shadow)
            {
                size += BuildSubtree(child, item, buffer);
            }
        }

        ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_nodes, node);
        entry.SubtreeSize = size;
        return size;
    }

    /// <summary>
    /// Inserts the item's visible subtree at the flat index (one ItemInserted per row), grows the ancestors'
    /// stored sizes, and returns the number of rows inserted.
    /// </summary>
    private int InsertSubtree(int flatIndex, object node, object parentKey)
    {
        var buffer = new List<object>();
        var size = BuildSubtree(node, parentKey, buffer);

        for (var i = 0; i < buffer.Count; i++)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_nodes, buffer[i]);
            entry.Handle = _rows.InsertAt(flatIndex + i, buffer[i]);
            ShiftSelectionForInsert(flatIndex + i);
            RaiseVectorChanged(CollectionChange.ItemInserted, flatIndex + i);
        }

        GrowAncestors(parentKey, size);
        return size;
    }

    /// <summary>
    /// Removes the item's visible subtree from the flat view (one ItemRemoved per row), shrinks the ancestors'
    /// stored sizes, and drops the subscriptions of the item and its tracked descendants.
    /// </summary>
    private void RemoveSubtree(object node)
    {
        var entry = _nodes[node];
        var size = entry.SubtreeSize;
        var parentKey = entry.ParentKey;
        var flatIndex = _rows.IndexOf(entry.Handle);

        // Unsubscribe the whole subtree BEFORE the entries disappear (the walk needs their Branch references).
        DropBranchesRecursive(node);

        for (var i = 0; i < size; i++)
        {
            var handle = _rows.SelectAt(flatIndex);

            _rows.Remove(handle);
            _nodes.Remove(handle.Value);
            ShiftSelectionForRemove(flatIndex);

            RaiseVectorChanged(CollectionChange.ItemRemoved, flatIndex);
        }

        GrowAncestors(parentKey, -size);
    }

    private void GrowAncestors(object key, int delta)
    {
        while (!ReferenceEquals(key, RootsKey))
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_nodes, key);

            if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref entry))
            {
                return;
            }

            entry.SubtreeSize += delta;
            key = entry.ParentKey;
        }
    }

    /// <summary>
    /// The item's 1-based position among its visible siblings and the sibling count, for UI Automation
    /// ("item 2 of 5"). Returns <see langword="null"/> when the item is not currently visible.
    /// </summary>
    internal (int Position, int Count)? GetSiblingInfo(object item)
    {
        if (!_nodes.TryGetValue(item, out var entry) || GetBranch(entry.ParentKey) is not { } branch)
        {
            return null;
        }

        var index = branch.Shadow.IndexOf(item);
        return index < 0 ? null : (index + 1, branch.Shadow.Count);
    }

    private void DropBranchesRecursive(object node)
    {
        if (GetBranch(node) is { } branch)
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
            return _rows.IndexOf(_nodes[branch.Shadow[childIndex]].Handle);
        }

        if (branch.Shadow.Count > 0)
        {
            var lastEntry = _nodes[branch.Shadow[^1]];
            return _rows.IndexOf(lastEntry.Handle) + lastEntry.SubtreeSize;
        }

        return ReferenceEquals(branch.Key, RootsKey) ? 0 : _rows.IndexOf(_nodes[branch.Key].Handle) + 1;
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
    public bool Contains(object item) => _nodes.ContainsKey(item);

    /// <inheritdoc/>
    public void CopyTo(object[] array, int arrayIndex)
    {
        foreach (var value in this)
        {
            array[arrayIndex++] = value;
        }
    }

    /// <inheritdoc/>
    public int IndexOf(object item) => _nodes.TryGetValue(item, out var entry) ? _rows.IndexOf(entry.Handle) : -1;

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
        if (_rootsBranch is { } roots)
        {
            Unsubscribe(roots);
        }

        foreach (var entry in _nodes.Values.Where(entry => entry.Branch is not null).ToList())
        {
            Unsubscribe(entry.Branch!);
        }

        GC.SuppressFinalize(this);
    }

    private sealed class VectorChangedArgs(CollectionChange change, uint index) : IVectorChangedEventArgs
    {
        public CollectionChange CollectionChange => change;
        public uint Index => index;
    }
}
