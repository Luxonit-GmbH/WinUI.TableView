using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation.Collections;

namespace WinUI.TableView;

/// <summary>
/// A client-side sorted + filtered children collection for tree nodes (also usable for the roots): the app keeps
/// the full loaded set here and exposes the view as <see cref="ITableViewTreeItem.ChildrenSource"/>; sorting and
/// filtering are applied in-process, per branch, with no backend support required.
/// </summary>
/// <remarks>
/// <para>Two layers: a master list holding everything loaded (arrival order, never dropped by a filter) and a
/// projection the grid sees. <see cref="Apply"/> re-sorts/re-filters the projection and raises a single Reset —
/// which <see cref="TreeTableViewSource"/> diffs, so unchanged rows keep their containers and expansion state
/// (retained on the node objects in the master list) survives filter round-trips.</para>
/// <para>Sorting is SNAPSHOT-style, matching a no-live-shaping grid: items are placed by their values at insert or
/// re-sort time and do not move when properties mutate afterwards. After deliberately changing an item's sort/filter
/// fields, call <see cref="Refresh"/> to re-place that one item.</para>
/// <para>Access by index on the view is O(1) (list-backed); sorted inserts find their position by binary search,
/// O(log children).</para>
/// </remarks>
public partial class TreeTableViewChildrenView : IObservableVector<object>, IEnumerable<ITableViewTreeItem>
{
    private readonly List<ITableViewTreeItem> _all = [];
    private readonly List<ITableViewTreeItem> _view = [];

    /// <inheritdoc/>
    public event VectorChangedEventHandler<object>? VectorChanged;

    /// <summary>
    /// Gets the active sort order, or <see langword="null"/> for arrival order.
    /// </summary>
    public IComparer<ITableViewTreeItem>? Comparer { get; private set; }

    /// <summary>
    /// Gets the active filter, or <see langword="null"/> for no filtering.
    /// </summary>
    public Predicate<ITableViewTreeItem>? Filter { get; private set; }

    /// <summary>
    /// Gets every loaded item, including those currently filtered out of the view.
    /// </summary>
    public IReadOnlyList<ITableViewTreeItem> AllItems => _all;

    /// <summary>
    /// Applies a sort order and/or filter to the view in one pass and raises a single Reset (which the flattening
    /// adapter turns into a minimal diff). Pass <see langword="null"/> to clear either aspect. Sorting is stable, so
    /// equal keys keep their arrival order.
    /// </summary>
    /// <param name="comparer">The sort order, or <see langword="null"/> for arrival order.</param>
    /// <param name="filter">The filter, or <see langword="null"/> for no filtering.</param>
    public void Apply(IComparer<ITableViewTreeItem>? comparer, Predicate<ITableViewTreeItem>? filter)
    {
        Comparer = comparer;
        Filter = filter;

        _view.Clear();
        var passing = Filter is { } activeFilter ? _all.Where(item => activeFilter(item)) : _all;
        _view.AddRange(Comparer is { } activeComparer ? passing.OrderBy(item => item, activeComparer) : passing);

        VectorChanged?.Invoke(this, new VectorChangedArgs(CollectionChange.Reset, 0));
    }

    /// <summary>
    /// Adds a loaded item: always joins the master list; joins the view at its sorted position (binary search)
    /// when it passes the active filter.
    /// </summary>
    /// <param name="item">The item to add.</param>
    public void Add(ITableViewTreeItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        _all.Add(item);
        InsertIntoView(item);
    }

    /// <summary>
    /// Removes an item from the master list and, when present, from the view.
    /// </summary>
    /// <param name="item">The item to remove.</param>
    /// <returns>Whether the item was found in the master list.</returns>
    public bool Remove(ITableViewTreeItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!_all.Remove(item))
        {
            return false;
        }

        RemoveFromView(item);
        return true;
    }

    /// <summary>
    /// Re-places a single item after its sort or filter fields were changed: it leaves/joins the view per the
    /// filter and moves to its current sorted position. This is the opt-in hook for deliberate re-shaping —
    /// ordinary value mutations do not move rows.
    /// </summary>
    /// <param name="item">The mutated item.</param>
    public void Refresh(ITableViewTreeItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!_all.Contains(item))
        {
            return;
        }

        RemoveFromView(item);
        InsertIntoView(item);
    }

    private void InsertIntoView(ITableViewTreeItem item)
    {
        if (Filter is { } filter && !filter(item))
        {
            return;
        }

        var index = Comparer is { } comparer ? FindSortedInsertIndex(item, comparer) : _view.Count;
        _view.Insert(index, item);
        VectorChanged?.Invoke(this, new VectorChangedArgs(CollectionChange.ItemInserted, (uint)index));
    }

    private void RemoveFromView(ITableViewTreeItem item)
    {
        var index = IndexInView(item);

        if (index >= 0)
        {
            _view.RemoveAt(index);
            VectorChanged?.Invoke(this, new VectorChangedArgs(CollectionChange.ItemRemoved, (uint)index));
        }
    }

    /// <summary>
    /// The insert position that keeps the view sorted; among equal keys the new item goes last (stable append).
    /// </summary>
    private int FindSortedInsertIndex(ITableViewTreeItem item, IComparer<ITableViewTreeItem> comparer)
    {
        var lo = 0;
        var hi = _view.Count;

        while (lo < hi)
        {
            var mid = (lo + hi) / 2;

            if (comparer.Compare(_view[mid], item) <= 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>
    /// Locates an item in the view: binary search around its CURRENT key first, falling back to a linear scan when
    /// the item's key mutated since placement (snapshot sorting allows that drift).
    /// </summary>
    private int IndexInView(ITableViewTreeItem item)
    {
        if (Comparer is { } comparer)
        {
            var lo = 0;
            var hi = _view.Count;

            while (lo < hi)
            {
                var mid = (lo + hi) / 2;

                if (comparer.Compare(_view[mid], item) < 0)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            for (var i = lo; i < _view.Count && comparer.Compare(_view[i], item) == 0; i++)
            {
                if (ReferenceEquals(_view[i], item))
                {
                    return i;
                }
            }
        }

        return _view.IndexOf(item);
    }

    // ---------------------------------------------------------------------------------------------------------
    // IObservableVector<object> / IEnumerable<ITableViewTreeItem> — the read-only projection the grid consumes.
    // ---------------------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public object this[int index]
    {
        get => _view[index];
        set => throw new NotSupportedException("The view is a projection; use Add/Remove/Refresh/Apply.");
    }

    /// <inheritdoc/>
    public int Count => _view.Count;

    /// <inheritdoc/>
    public bool IsReadOnly => true;

    /// <inheritdoc/>
    public bool Contains(object item) => item is ITableViewTreeItem treeItem && IndexInView(treeItem) >= 0;

    /// <inheritdoc/>
    public void CopyTo(object[] array, int arrayIndex)
    {
        foreach (var item in _view)
        {
            array[arrayIndex++] = item;
        }
    }

    /// <inheritdoc/>
    public int IndexOf(object item) => item is ITableViewTreeItem treeItem ? IndexInView(treeItem) : -1;

    /// <inheritdoc/>
    public IEnumerator<object> GetEnumerator() => _view.Cast<object>().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _view.GetEnumerator();

    IEnumerator<ITableViewTreeItem> IEnumerable<ITableViewTreeItem>.GetEnumerator() => _view.GetEnumerator();

    /// <inheritdoc/>
    public void Add(object item) => throw new NotSupportedException("Use the typed Add(ITableViewTreeItem).");

    /// <inheritdoc/>
    public void Insert(int index, object item) => throw new NotSupportedException("The view is sorted; use Add.");

    /// <inheritdoc/>
    public bool Remove(object item) => item is ITableViewTreeItem treeItem && Remove(treeItem);

    /// <inheritdoc/>
    public void RemoveAt(int index) => throw new NotSupportedException("Remove by item; the view is a projection.");

    /// <inheritdoc/>
    public void Clear() => throw new NotSupportedException("The view is a projection; use Apply or Remove.");

    private sealed class VectorChangedArgs(CollectionChange change, uint index) : IVectorChangedEventArgs
    {
        public CollectionChange CollectionChange => change;
        public uint Index => index;
    }
}
