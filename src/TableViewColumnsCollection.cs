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
        
        if (_movingColumn) return; // Skip processing if it's a move action

        UpdateFrozenColumns();

        var index = (int)args.Index;

        switch (args.CollectionChange)
        {
            case CollectionChange.ItemInserted:
                if (args.Index < Count)
                {
                    var column = (TableViewColumn)sender[index];
                    column.SetOwningCollection(this);
                    column.SetOwningTableView(((ITableViewColumnsCollection)this).TableView!);
                    CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, column, (int)args.Index));
                }
                break;
            case CollectionChange.ItemRemoved:
                if (args.Index < _itemsCopy.Length)
                {
                    var column = _itemsCopy[index];
                    column.SetOwningCollection(null!);
                    column.SetOwningTableView(null!);
                    CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, column, (int)args.Index));
                }
                break;
            case CollectionChange.Reset:
                foreach (var item in _itemsCopy)
                {
                    item.SetOwningCollection(null!);
                    item.SetOwningTableView(null!);
                    CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
                }
                break;
        }

        _itemsCopy = new TableViewColumn[Count];
        CopyTo(_itemsCopy, 0);
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
    /// 
    /// </summary>
    /// <param name="column"></param>
    /// <returns></returns>
    public int VisibleColumnIndex(TableViewColumn column)
    {
        _visibleColumnsMapCached ??= VisibleColumns.Select((x, i) => (i , x)).ToDictionary(x => x.x, x => x.i);
        return _visibleColumnsMapCached.GetValueOrDefault(column, -1);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="column"></param>
    /// <returns></returns>
    public int VisibleFrozenColumnIndex(TableViewColumn column)
    {
        _visibleFrozenColumnsMapCached ??= VisibleFrozenColumns.Select((x, i) => (i , x)).ToDictionary(x => x.x, x => x.i);
        return _visibleFrozenColumnsMapCached.GetValueOrDefault(column, -1);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="column"></param>
    /// <returns></returns>
    public int VisibleScrollableColumnIndex(TableViewColumn column)
    {
        _visibleScrollableColumnsMapCached ??= VisibleScrollableColumns.Select((x, i) => (i , x)).ToDictionary(x => x.x, x => x.i);
        return _visibleScrollableColumnsMapCached.GetValueOrDefault(column, -1);
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
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Move, column, newIndex, oldIndex));

        _movingColumn = false;
    }
}