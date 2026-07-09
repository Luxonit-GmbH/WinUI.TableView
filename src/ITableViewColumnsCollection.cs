using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;

namespace WinUI.TableView;

/// <summary>
/// Represents a collection of columns in a <see cref="WinUI.TableView.TableView"/>, providing functionality to manage and interact with the columns.
/// </summary>
/// <remarks>This interface extends <see cref="IList{T}"/> to provide standard list operations for <see
/// cref="TableViewColumn"/> objects. It also implements <see cref="INotifyCollectionChanged"/> to notify subscribers of
/// changes to the collection, such as additions or removals.</remarks>
public interface ITableViewColumnsCollection : IList<TableViewColumn>, INotifyCollectionChanged
{
    /// <summary>
    /// Occurs when a property of a column changes.
    /// </summary>
    /// <remarks>
    /// This event is triggered to notify subscribers about changes to column properties, such as width, visibility, or other attributes.
    /// Handlers can use the <see cref="TableViewColumnPropertyChangedEventArgs"/> parameter to access details about the specific property that changed.
    /// </remarks>
    event EventHandler<TableViewColumnPropertyChangedEventArgs>? ColumnPropertyChanged;

    /// <summary>
    /// Moves a column from one index to another within the collection.
    /// </summary>
    /// <param name="oldIndex">The zero-based index of the column to move.</param>
    /// <param name="newIndex">The zero-based index to move the column to.</param>
    void Move(int oldIndex, int newIndex);

    /// <summary>
    /// Appends a range of columns to the end of the collection, raising a single
    /// <see cref="INotifyCollectionChanged.CollectionChanged"/> notification for the whole range.
    /// </summary>
    /// <remarks>
    /// Prefer this over calling <see cref="ICollection{T}.Add"/> in a loop when adding many columns: it suspends the
    /// per-item notifications and raises one <see cref="NotifyCollectionChangedAction.Add"/> event, so the
    /// <see cref="WinUI.TableView.TableView"/> realizes the new columns in a single pass instead of once per column.
    /// </remarks>
    /// <param name="columns">The columns to append. Must not be <see langword="null"/> nor contain <see langword="null"/> items.</param>
    /// <exception cref="ArgumentNullException"><paramref name="columns"/> or any contained column is <see langword="null"/>.</exception>
    void AddRange(IEnumerable<TableViewColumn> columns);

    /// <summary>
    /// Replaces the entire contents of the collection with the specified columns, raising a single
    /// <see cref="NotifyCollectionChangedAction.Reset"/> notification.
    /// </summary>
    /// <remarks>
    /// Equivalent to clearing the collection and adding <paramref name="columns"/>, but performed as one batch: a
    /// single <see cref="INotifyCollectionChanged.CollectionChanged"/> reset event is raised, prompting subscribers to
    /// re-synchronize from the new contents in a single pass rather than reacting to each removal and addition.
    /// </remarks>
    /// <param name="columns">The columns that become the new contents. Must not be <see langword="null"/> nor contain <see langword="null"/> items.</param>
    /// <exception cref="ArgumentNullException"><paramref name="columns"/> or any contained column is <see langword="null"/>.</exception>
    void Reset(IEnumerable<TableViewColumn> columns);

    /// <summary>
    /// Gets the list of visible <see cref="TableViewColumn"/>s.
    /// </summary>
    /// <remarks>
    /// The result is a list of columns that are currently visible in the table view, meaning their <see cref="TableViewColumn.Visibility"/> is set to <see cref="Visibility.Visible"/>.
    /// The result is also ordered by the <see cref="TableViewColumn.Order"/> property, allowing for a consistent display order of the columns.
    /// </remarks>
    IList<TableViewColumn> VisibleColumns { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="column"></param>
    /// <returns></returns>
    int VisibleColumnIndex(TableViewColumn column);

    /// <summary>
    /// 
    /// </summary>
    IList<TableViewColumn> VisibleFrozenColumns { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="column"></param>
    /// <returns></returns>
    int VisibleFrozenColumnIndex(TableViewColumn column);

    /// <summary>
    /// 
    /// </summary>
    IList<TableViewColumn> VisibleScrollableColumns { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="column"></param>
    /// <returns></returns>
    int VisibleScrollableColumnIndex(TableViewColumn column);

    /// <summary>
    /// Gets or sets the <see cref="WinUI.TableView.TableView"/> associated with the collection.
    /// </summary>
    /// <remarks>
    /// This property allows access to the <see cref="WinUI.TableView.TableView"/> that owns this collection of columns.
    /// </remarks>
    TableView? TableView { get; }
}
