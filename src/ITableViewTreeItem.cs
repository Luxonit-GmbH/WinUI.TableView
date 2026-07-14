using System.Collections.Generic;
using System.ComponentModel;

namespace WinUI.TableView;

/// <summary>
/// Contract a data item exposes so a <see cref="TreeTableView"/> can render hierarchy. Items that do not implement
/// this interface are plain rows — no expansion is possible on them.
/// </summary>
/// <remarks>
/// <para>Hierarchy is rendered from a FLAT view of the visible nodes. Either the app maintains that flat source
/// itself, or — the usual path — it binds a <see cref="TreeTableViewSource"/> over nested
/// <see cref="ChildrenSource"/> collections and lets the library flatten. Expansion is owned by the source side:
/// the grid raises <see cref="TreeTableView.ExpandRequested"/>/<see cref="TreeTableView.CollapseRequested"/> and the
/// handler inserts/removes child rows (typically by calling
/// <see cref="TreeTableViewSource.Expand"/>/<see cref="TreeTableViewSource.Collapse"/> after any asynchronous
/// child fetch).</para>
/// <para>The interface inherits <see cref="INotifyPropertyChanged"/> because the expander visuals are bound to
/// <see cref="IsExpanded"/>/<see cref="IsLoading"/>/<see cref="HasChildren"/> — without change notifications the
/// chevron and loading indicator would go stale the first time a node is expanded.</para>
/// </remarks>
public interface ITableViewTreeItem : INotifyPropertyChanged
{
    /// <summary>
    /// Gets the zero-based depth of the item in the tree; roots are 0. Drives the indentation of the
    /// <see cref="TableViewTreeColumn"/> cell.
    /// </summary>
    int Depth { get; }

    /// <summary>
    /// Gets whether the item has (or may have) children — shows the expander chevron. May return
    /// <see langword="true"/> for unrealized children that are loaded on first expand.
    /// </summary>
    bool HasChildren { get; }

    /// <summary>
    /// Gets whether the item is a terminal leaf that can NEVER have children. When <see langword="true"/>, no
    /// expand/collapse event is ever raised for the item — every gesture path (chevron, keyboard, double-click,
    /// UI Automation) and <see cref="TreeTableViewSource.Expand"/> are hard-gated. Unlike
    /// <see cref="HasChildren"/>, which may change as counts arrive, this is a definitive statement; final items
    /// should also report <see cref="HasChildren"/> = <see langword="false"/> so no chevron is shown.
    /// </summary>
    bool IsFinalItem { get; }

    /// <summary>
    /// Gets or sets whether the item's children are currently shown (present in the flat view after it). Set by
    /// <see cref="TreeTableViewSource.Expand"/>/<see cref="TreeTableViewSource.Collapse"/> (or by the app's own
    /// flat-source handling); raise <see cref="INotifyPropertyChanged.PropertyChanged"/> so the expander updates.
    /// </summary>
    bool IsExpanded { get; set; }

    /// <summary>
    /// Gets whether an asynchronous expansion is in progress (children requested from the backend but not yet
    /// inserted). While <see langword="true"/> the <see cref="TableViewTreeColumn"/> shows a progress indicator in
    /// the chevron slot and <see cref="TreeTableView"/> ignores further expand/collapse requests for the item.
    /// Sources with synchronous expansion can always return <see langword="false"/>.
    /// </summary>
    bool IsLoading { get; }

    /// <summary>
    /// Gets the item's children for <see cref="TreeTableViewSource"/>-based flattening, or <see langword="null"/>
    /// when not (yet) loaded or not applicable (e.g. app-maintained flat sources, or final items).
    /// </summary>
    /// <remarks>
    /// The static type is the covariant <see cref="IEnumerable{T}"/> so any typed app collection fits without
    /// casts; observability is a runtime capability — collections implementing
    /// <see cref="Windows.Foundation.Collections.IObservableVector{T}"/> of <see cref="object"/> (preferred,
    /// native; e.g. <see cref="TreeTableViewChildrenView"/>) or
    /// <see cref="System.Collections.Specialized.INotifyCollectionChanged"/> are tracked live while the item is
    /// expanded, and static collections are simply enumerated once.
    /// </remarks>
    IEnumerable<ITableViewTreeItem>? ChildrenSource { get; }
}
