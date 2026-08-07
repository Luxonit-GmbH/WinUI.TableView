using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using Windows.Foundation.Collections;

namespace WinUI.TableView;

/// <summary>
/// Represents a collection of <see cref="TableViewColumn"/> objects used in a <see cref="WinUI.TableView.TableView"/>.
/// </summary>
/// <remarks>This collection provides functionality for managing columns in a <see cref="WinUI.TableView.TableView"/>, including adding,
/// removing,  and tracking changes to column properties. It supports notifications for collection changes and column 
/// property changes, enabling dynamic updates to the <see cref="WinUI.TableView.TableView"/>.</remarks>
public partial class TableViewColumnsCollection : DependencyObjectCollection, ITableViewColumnsCollection
{
    private TableViewColumn[] _itemsCopy = []; // To keep a copy of the items to keep track of removed items
    private bool _movingColumn;
    private bool _suspendNotifications; // batches AddRange/Reset: the per-item events are coalesced into one

    /// <inheritdoc/>
    public event EventHandler<TableViewColumnPropertyChangedEventArgs>? ColumnPropertyChanged;
    /// <inheritdoc/>
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <summary>
    /// The constructor for the <see cref="TableViewColumnsCollection"/> class.
    /// </summary>
    /// <param name="tableView">
    /// The <see cref="WinUI.TableView.TableView"/> that owns this collection.
    /// </param>
    public TableViewColumnsCollection(TableView tableView)
    {
        TableView = tableView ?? throw new ArgumentNullException(nameof(tableView));
        VectorChanged += OnVectorChanged;
    }

    /// <summary>
    /// Handles changes to the underlying vector of <see cref="DependencyObject"/> items.
    /// </summary>
    private void OnVectorChanged(IObservableVector<DependencyObject> sender, IVectorChangedEventArgs args)
    {
        // CLEAR CACHED COLUMNS
        _visibleColumnsCached = null;
        _visibleColumnsMapCached = null;
        _visibleFrozenColumnsCached = null;
        _visibleFrozenColumnsMapCached = null;
        _visibleScrollableColumnsCached = null;
        _visibleScrollableColumnsMapCached = null;
        _visibleScrollableColumnOffsetsCached = null;

        if (_movingColumn) return; // Skip processing if it's a move action

        // During a batch (AddRange/Reset) the frozen refresh, snapshot and the collection-changed event are deferred
        // and performed once by the batch method; here we keep only the per-item owner wiring (and cache reset above)
        // so the columns are immediately usable.
        if (!_suspendNotifications)
        {
            UpdateFrozenColumns();
        }

        var index = (int)args.Index;

        switch (args.CollectionChange)
        {
            case CollectionChange.ItemInserted:
                if (args.Index < Count)
                {
                    var column = (TableViewColumn)sender[index];
                    column.SetOwningCollection(this);
                    column.SetOwningTableView(((ITableViewColumnsCollection)this).TableView!);
                    RaiseCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, column, (int)args.Index));
                }
                break;
            case CollectionChange.ItemRemoved:
                if (args.Index < _itemsCopy.Length)
                {
                    var column = _itemsCopy[index];
                    column.SetOwningCollection(null!);
                    column.SetOwningTableView(null!);
                    RaiseCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, column, (int)args.Index));
                }
                break;
            case CollectionChange.Reset:
                foreach (var item in _itemsCopy)
                {
                    item.SetOwningCollection(null!);
                    item.SetOwningTableView(null!);
                }
                // A reset is a single notification, not one per removed item.
                RaiseCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
                break;
        }

        if (!_suspendNotifications)
        {
            _itemsCopy = new TableViewColumn[Count];
            CopyTo(_itemsCopy, 0);
        }
    }

    /// <summary>
    /// Raises <see cref="CollectionChanged"/> unless notifications are currently suspended for a batch operation
    /// (see <see cref="AddRange"/> and <see cref="Reset"/>), in which case a single coalesced notification is raised
    /// by the batch method once it completes.
    /// </summary>
    /// <param name="args">The change details to report to subscribers.</param>
    private void RaiseCollectionChanged(NotifyCollectionChangedEventArgs args)
    {
        if (_suspendNotifications) return;

        CollectionChanged?.Invoke(this, args);
    }

    /// <summary>
    /// Drops every derived cache (visible column lists, index maps, cumulative offsets) and refreshes frozen state,
    /// so the next read reflects the current columns. Called by
    /// <see cref="WinUI.TableView.TableView.InvalidateColumns"/>.
    /// </summary>
    internal void InvalidateCaches()
    {
        _visibleColumnsCached = null;
        _visibleColumnsMapCached = null;
        _visibleFrozenColumnsCached = null;
        _visibleFrozenColumnsMapCached = null;
        _visibleScrollableColumnsCached = null;
        _visibleScrollableColumnsMapCached = null;
        _visibleScrollableColumnOffsetsCached = null;

        UpdateFrozenColumns();
    }

    internal void UpdateFrozenColumns()
    {
        foreach (var column in this.OfType<TableViewColumn>())
        {
            column.IsFrozen = VisibleColumnIndex(column) < (TableView?.FrozenColumnCount ?? 0);
        }
    }

    /// <summary>
    /// Handles the property changed event for a column.
    /// </summary>
    internal void HandleColumnPropertyChanged(TableViewColumn column, string propertyName)
    {
        if (propertyName is nameof(TableViewColumn.Visibility)
            or nameof(TableViewColumn.Order)
            or nameof(TableViewColumn.IsFrozen)
            or nameof(TableViewColumn.GroupName))
        {
            // Membership or order of the visible column sets changed; every derived cache is stale, not just the
            // offsets (a hidden column must disappear from VisibleColumns immediately).
            _visibleColumnsCached = null;
            _visibleColumnsMapCached = null;
            _visibleFrozenColumnsCached = null;
            _visibleFrozenColumnsMapCached = null;
            _visibleScrollableColumnsCached = null;
            _visibleScrollableColumnsMapCached = null;
            _visibleScrollableColumnOffsetsCached = null;
        }
        else if (propertyName is nameof(TableViewColumn.ActualWidth))
        {
            // Width changes keep membership/order intact; only the cumulative offsets used by horizontal
            // virtualization depend on it.
            _visibleScrollableColumnOffsetsCached = null;
        }

        // A batch operation refreshes frozen state wholesale and raises a single CollectionChanged at the end, so
        // the per-column notifications it would otherwise trigger are suppressed here.
        if (_suspendNotifications) return;

        if (Contains(column) && !_movingColumn)
        {
            var index = IndexOf(column);
            ColumnPropertyChanged?.Invoke(this, new TableViewColumnPropertyChangedEventArgs(column, propertyName, index));
        }
    }

    /// <inheritdoc/>
    public TableView? TableView { get; }

    private IList<TableViewColumn>? _visibleColumnsCached;
    private Dictionary<TableViewColumn, int>? _visibleColumnsMapCached;
    private IList<TableViewColumn>? _visibleFrozenColumnsCached;
    private Dictionary<TableViewColumn, int>? _visibleFrozenColumnsMapCached;
    private IList<TableViewColumn>? _visibleScrollableColumnsCached;
    private Dictionary<TableViewColumn, int>? _visibleScrollableColumnsMapCached;
    private double[]? _visibleScrollableColumnOffsetsCached;

    /// <inheritdoc/>
    public IList<TableViewColumn> VisibleColumns =>
        _visibleColumnsCached ??= [
            .. this.OfType<TableViewColumn>()
                .Where(x => x.Visibility == Visibility.Visible)
                .OrderBy(x => x.Order ?? 0)
        ];

    /// <summary>
    /// 
    /// </summary>
    public IList<TableViewColumn> VisibleFrozenColumns =>
        _visibleFrozenColumnsCached ??= VisibleColumns.Where(x => x.IsFrozen).ToList();

    /// <summary>
    /// 
    /// </summary>
    public IList<TableViewColumn> VisibleScrollableColumns =>
        _visibleScrollableColumnsCached ??= VisibleColumns.Where(x => !x.IsFrozen).ToList();

    /// <summary>
    /// Resolves the second header level: the run of visible columns each banner covers, plus the gaps between
    /// them, walked in display order so the header can lay the row out in one pass.
    /// </summary>
    /// <remarks>
    /// <para>Frozen and scrollable columns are walked separately because they live in different panels — only one
    /// of which pans — so a banner can never straddle the two. A group whose columns fall on both sides therefore
    /// yields two spans; <see cref="ValidateColumnGroups"/> is what reports that as a mistake.</para>
    /// <para>Runs are built from adjacency in the visible order, so a group split by a foreign column also yields
    /// more than one span rather than silently swallowing the intruder.</para>
    /// </remarks>
    /// <param name="groups">The defined groups; columns whose GroupName matches none of them are treated as ungrouped.</param>
    /// <returns>Spans in display order: frozen first, then scrollable.</returns>
    internal IReadOnlyList<TableViewColumnGroupSpan> GetColumnGroupSpans(IEnumerable<TableViewColumnGroup>? groups)
    {
        var byName = new Dictionary<string, TableViewColumnGroup>();

        foreach (var group in groups ?? [])
        {
            if (!string.IsNullOrEmpty(group.Name))
            {
                byName[group.Name] = group;
            }
        }

        List<TableViewColumnGroupSpan> spans = [];
        AppendSpans(spans, VisibleFrozenColumns, byName, isFrozen: true);
        AppendSpans(spans, VisibleScrollableColumns, byName, isFrozen: false);
        return spans;
    }

    private static void AppendSpans(
        List<TableViewColumnGroupSpan> spans,
        IList<TableViewColumn> columns,
        Dictionary<string, TableViewColumnGroup> byName,
        bool isFrozen)
    {
        var index = 0;

        while (index < columns.Count)
        {
            var group = ResolveGroup(columns[index], byName);
            var end = index + 1;

            // Extend while the neighbours resolve to the SAME group instance. Ungrouped columns resolve to null
            // and each stand alone, so an ungrouped run does not become one giant empty banner.
            if (group is not null)
            {
                while (end < columns.Count && ReferenceEquals(ResolveGroup(columns[end], byName), group))
                {
                    end++;
                }
            }

            spans.Add(new TableViewColumnGroupSpan(
                group,
                [.. columns.Skip(index).Take(end - index)],
                index,
                isFrozen));
            index = end;
        }
    }

    private static TableViewColumnGroup? ResolveGroup(
        TableViewColumn column,
        Dictionary<string, TableViewColumnGroup> byName)
        => column.GroupName is { Length: > 0 } name && byName.TryGetValue(name, out var group) ? group : null;

    /// <summary>
    /// Reports the ways a set of column groups cannot be rendered, so the mistake surfaces as a message rather
    /// than as a banner drawn in the wrong place.
    /// </summary>
    /// <remarks>
    /// Checks whole columns, not just visible ones: a group split by a hidden column is still a latent bug that
    /// appears the moment that column is shown.
    /// </remarks>
    /// <param name="groups">The defined groups.</param>
    /// <returns>One message per problem; empty when the groups are sound.</returns>
    internal IReadOnlyList<string> ValidateColumnGroups(IEnumerable<TableViewColumnGroup>? groups)
    {
        List<string> problems = [];
        var defined = new HashSet<string>();

        foreach (var group in groups ?? [])
        {
            if (string.IsNullOrEmpty(group.Name))
            {
                problems.Add($"A {nameof(TableViewColumnGroup)} has no {nameof(TableViewColumnGroup.Name)}, so no column can join it.");
            }
            else if (!defined.Add(group.Name))
            {
                problems.Add($"More than one {nameof(TableViewColumnGroup)} is named '{group.Name}'.");
            }
        }

        var ordered = this.OfType<TableViewColumn>().OrderBy(column => column.Order ?? 0).ToList();
        var seen = new Dictionary<string, (int Last, bool IsFrozen)>();

        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].GroupName is not { Length: > 0 } name)
            {
                continue;
            }

            if (!defined.Contains(name))
            {
                problems.Add($"Column '{Describe(ordered[i])}' names group '{name}', which is not defined.");
                continue;
            }

            if (seen.TryGetValue(name, out var previous))
            {
                if (previous.Last != i - 1)
                {
                    problems.Add(
                        $"Group '{name}' is not contiguous: a banner spans one run of columns, but '{Describe(ordered[i])}' " +
                        "is separated from the rest of its group.");
                }

                if (previous.IsFrozen != ordered[i].IsFrozen)
                {
                    problems.Add(
                        $"Group '{name}' spans both frozen and scrollable columns. The frozen headers do not pan " +
                        "with the scrollable ones, so one banner cannot cover both.");
                }
            }

            seen[name] = (i, ordered[i].IsFrozen);
        }

        return problems;
    }

    private static string Describe(TableViewColumn column)
        => column.Header?.ToString() is { Length: > 0 } header ? header : column.GetType().Name;

    /// <summary>
    /// Gets the cumulative right-edge offset (running sum of <see cref="TableViewColumn.ActualWidth"/>) of each
    /// visible scrollable column. Cached so horizontal virtualization can locate the visible column range with a
    /// binary search instead of re-summing every column width on every scroll tick; invalidated when the scrollable
    /// set or any column's ActualWidth changes.
    /// </summary>
    internal double[] VisibleScrollableColumnOffsets
    {
        get
        {
            if (_visibleScrollableColumnOffsetsCached is null)
            {
                var columns = VisibleScrollableColumns;
                var offsets = new double[columns.Count];
                var x = 0d;

                for (var i = 0; i < columns.Count; i++)
                {
                    x += columns[i].ActualWidth;
                    offsets[i] = x;
                }

                _visibleScrollableColumnOffsetsCached = offsets;
            }

            return _visibleScrollableColumnOffsetsCached;
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="column"></param>
    /// <returns></returns>
    public int VisibleColumnIndex(TableViewColumn column)
    {
        _visibleColumnsMapCached ??= BuildIndexMap(VisibleColumns);
        return _visibleColumnsMapCached.GetValueOrDefault(column, -1);
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="column"></param>
    /// <returns></returns>
    public int VisibleFrozenColumnIndex(TableViewColumn column)
    {
        _visibleFrozenColumnsMapCached ??= BuildIndexMap(VisibleFrozenColumns);
        return _visibleFrozenColumnsMapCached.GetValueOrDefault(column, -1);
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="column"></param>
    /// <returns></returns>
    public int VisibleScrollableColumnIndex(TableViewColumn column)
    {
        _visibleScrollableColumnsMapCached ??= BuildIndexMap(VisibleScrollableColumns);
        return _visibleScrollableColumnsMapCached.GetValueOrDefault(column, -1);
    }

    /// <summary>
    /// Builds a column-to-index lookup. The first index wins when the same column instance appears more than once,
    /// matching <c>IndexOf</c> semantics (a plain ToDictionary would throw on the duplicate key — and, raised inside
    /// a VectorChanged callback, that surfaces as a baffling WinRT "parameter is incorrect" error on Add).
    /// </summary>
    private static Dictionary<TableViewColumn, int> BuildIndexMap(IList<TableViewColumn> columns)
    {
        var map = new Dictionary<TableViewColumn, int>(columns.Count);

        for (var i = 0; i < columns.Count; i++)
        {
            map.TryAdd(columns[i], i);
        }

        return map;
    }

    TableViewColumn IList<TableViewColumn>.this[int index]
    {
        get => (TableViewColumn)base[index];
        set => base[index] = value;
    }

    int ICollection<TableViewColumn>.Count => Count;

    bool ICollection<TableViewColumn>.IsReadOnly => IsReadOnly;

    void ICollection<TableViewColumn>.Add(TableViewColumn item)
    {
        Add(item);
    }

    void ICollection<TableViewColumn>.Clear()
    {
        Clear();
    }

    bool ICollection<TableViewColumn>.Contains(TableViewColumn item)
    {
        return Contains(item);
    }

    void ICollection<TableViewColumn>.CopyTo(TableViewColumn[] array, int arrayIndex)
    {
        CopyTo(array, arrayIndex);
    }

    IEnumerator<TableViewColumn> IEnumerable<TableViewColumn>.GetEnumerator()
    {
        foreach (var item in this)
        {
            yield return (TableViewColumn)item;
        }
    }

    int IList<TableViewColumn>.IndexOf(TableViewColumn item)
    {
        return IndexOf(item);
    }

    void IList<TableViewColumn>.Insert(int index, TableViewColumn item)
    {
        Insert(index, item);
    }

    bool ICollection<TableViewColumn>.Remove(TableViewColumn item)
    {
        var index = IndexOf(item);

        if (index >= 0)
        {
            RemoveAt(index);
            return true;
        }

        return false;
    }

    void IList<TableViewColumn>.RemoveAt(int index)
    {
        RemoveAt(index);
    }

    /// <inheritdoc/>
    public void Move(int oldIndex, int newIndex)
    {
        _movingColumn = true;

        if (oldIndex < 0 || oldIndex >= Count)
        {
            throw new ArgumentOutOfRangeException(nameof(oldIndex), "Old index is out of range.");
        }
        if (newIndex < 0 || newIndex >= Count)
        {
            throw new ArgumentOutOfRangeException(nameof(newIndex), "New index is out of range.");
        }
        if (oldIndex == newIndex)
        {
            return; // No need to move if the indices are the same
        }

        var column = (TableViewColumn)this[oldIndex];
        var columnBefore = (TableViewColumn)this[newIndex];

        // Assign the same order value as the target column, ensuring correct order for the VisibleColumns collection.
        column.Order = columnBefore.Order;

        RemoveAt(oldIndex);
        Insert(newIndex, column);

        UpdateFrozenColumns();
        RaiseCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Move, column, newIndex, oldIndex));

        _movingColumn = false;
    }

    /// <inheritdoc/>
    public void AddRange(IEnumerable<TableViewColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        // Materialize and validate up front so an invalid item cannot leave a partially populated, un-notified
        // collection behind.
        var added = columns.ToList();
        foreach (var column in added)
        {
            ArgumentNullException.ThrowIfNull(column, nameof(columns));
        }

        if (added.Count == 0) return;

        var startIndex = Count;

        _suspendNotifications = true;
        try
        {
            foreach (var column in added)
            {
                Add(column);
            }

            // Apply the deferred per-item work once for the whole batch, while still suspended so it raises no
            // intermediate notifications. Frozen state must be correct before subscribers read it below.
            UpdateFrozenColumns();
            _itemsCopy = new TableViewColumn[Count];
            CopyTo(_itemsCopy, 0);
        }
        finally
        {
            _suspendNotifications = false;
        }

        // One notification for the whole range. Subscribers (header row, rows) iterate NewItems, so the added
        // columns are realized incrementally rather than rebuilt.
        RaiseCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, added, startIndex));
    }

    /// <inheritdoc/>
    public void Reset(IEnumerable<TableViewColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        // Materialize and validate up front so an invalid item cannot clear the collection without repopulating it.
        var newColumns = columns.ToList();
        foreach (var column in newColumns)
        {
            ArgumentNullException.ThrowIfNull(column, nameof(columns));
        }

        _suspendNotifications = true;
        try
        {
            Clear();

            foreach (var column in newColumns)
            {
                Add(column);
            }

            UpdateFrozenColumns();
            _itemsCopy = new TableViewColumn[Count];
            CopyTo(_itemsCopy, 0);
        }
        finally
        {
            _suspendNotifications = false;
        }

        // A single Reset tells subscribers to re-sync from the new contents in one pass.
        RaiseCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}