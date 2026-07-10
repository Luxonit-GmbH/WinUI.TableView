using System.ComponentModel;

namespace WinUI.TableView;

/// <summary>
/// Contract a data item exposes so a <see cref="TreeTableView"/> can render hierarchy from a FLAT items source.
/// </summary>
/// <remarks>
/// The source contains only the currently visible (expanded) nodes, in display order, and each item reports its own
/// depth and expansion state — there is deliberately no children collection here: structure stays in the source's
/// adapter, so the grid remains a flat, fully virtualized list at any depth or scale. Expansion is owned by the
/// source, not the grid: the grid raises
/// <see cref="TreeTableView.ExpandRequested"/>/<see cref="TreeTableView.CollapseRequested"/> and the source reacts by
/// inserting or removing child rows, notifying via <see cref="Windows.Foundation.Collections.IObservableVector{T}"/>
/// (consumed natively by the platform) or <see cref="System.Collections.Specialized.INotifyCollectionChanged"/>.
/// The interface inherits <see cref="INotifyPropertyChanged"/> because the expander visuals are bound to
/// <see cref="IsExpanded"/>/<see cref="IsLoading"/>/<see cref="HasChildren"/> — without change notifications the
/// chevron and loading indicator would go stale the first time a node is expanded.
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
    /// Gets whether the item's children are currently shown (present in the flat source after it).
    /// </summary>
    bool IsExpanded { get; }

    /// <summary>
    /// Gets whether an asynchronous expansion is in progress (children requested from the backend but not yet
    /// inserted). While <see langword="true"/> the <see cref="TableViewTreeColumn"/> shows a progress indicator in
    /// the chevron slot and <see cref="TreeTableView"/> ignores further expand/collapse requests for the item.
    /// Set it before starting the child query and clear it (with <see cref="IsExpanded"/> updated) when the
    /// children have been inserted; sources with synchronous expansion can always return <see langword="false"/>.
    /// </summary>
    bool IsLoading { get; }
}
