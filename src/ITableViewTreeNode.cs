using System.Collections.Generic;

namespace WinUI.TableView;

/// <summary>
/// A tree item that exposes its own children, for use with <see cref="TreeTableViewSource"/> — the library-provided
/// adapter that flattens a nested (collection-in-collection) hierarchy into the flat view a
/// <see cref="TreeTableView"/> binds to.
/// </summary>
/// <remarks>
/// The app keeps its natural nested shape: each node owns a children collection, maintained in whatever order the
/// app's data layer already produces — inserting a child into a parent's collection is all that is needed; the
/// adapter computes the flat position and notifies the grid. <see cref="ITableViewTreeItem.IsExpanded"/> is
/// re-declared with a setter because the adapter persists expansion state onto the node when
/// <see cref="TreeTableViewSource.Expand"/>/<see cref="TreeTableViewSource.Collapse"/> are called.
/// </remarks>
public interface ITableViewTreeNode : ITableViewTreeItem
{
    /// <summary>
    /// Gets or sets whether the node's children are currently shown. Set by
    /// <see cref="TreeTableViewSource.Expand"/>/<see cref="TreeTableViewSource.Collapse"/>; raise
    /// <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/> so the expander visuals update.
    /// </summary>
    new bool IsExpanded { get; set; }

    /// <summary>
    /// Gets the node's children, or <see langword="null"/> when not (yet) loaded. Children that can nest further
    /// implement <see cref="ITableViewTreeNode"/>; plain <see cref="ITableViewTreeItem"/> children are leaves.
    /// </summary>
    /// <remarks>
    /// The static type is the covariant <see cref="IEnumerable{T}"/> so any typed app collection
    /// (e.g. <c>ObservableCollection&lt;MyNode&gt;</c>) fits without casts; observability is a runtime capability,
    /// not part of the contract — collections implementing
    /// <see cref="Windows.Foundation.Collections.IObservableVector{T}"/> of <see cref="object"/> (preferred, native)
    /// or <see cref="System.Collections.Specialized.INotifyCollectionChanged"/> are tracked live while the node is
    /// expanded, and static collections are simply enumerated once. (A deliberately non-vector static type:
    /// <c>IObservableVector&lt;ITableViewTreeItem&gt;</c> would be invariant — a typed app vector could never be
    /// assigned to it — and would force observability onto branches that never change.)
    /// </remarks>
    IEnumerable<ITableViewTreeItem>? ChildrenSource { get; }
}
