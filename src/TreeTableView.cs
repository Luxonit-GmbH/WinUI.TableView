using Microsoft.UI.Xaml.Input;
using System;
using Windows.System;

namespace WinUI.TableView;

/// <summary>
/// A <see cref="TableView"/> that renders hierarchical data from a FLAT items source of visible nodes.
/// </summary>
/// <remarks>
/// The hierarchy lives in the data adapter, not the control: the items source contains only the currently visible
/// (expanded) nodes in display order, each implementing <see cref="ITableViewTreeItem"/>. A
/// <see cref="TableViewTreeColumn"/> renders the indentation and expander chevron. The grid never mutates the tree —
/// it raises <see cref="ExpandRequested"/>/<see cref="CollapseRequested"/> (chevron click, Left/Right keys) and the
/// source inserts or removes the child rows. This keeps row/column virtualization, direct-mode binding and all other
/// <see cref="TableView"/> behavior intact regardless of tree depth or size.
/// </remarks>
public partial class TreeTableView : TableView
{
    /// <summary>
    /// Initializes a new instance of the TreeTableView class.
    /// </summary>
    public TreeTableView()
    {
        // Own style slot (BasedOn the TableView style in Generic.xaml) so the implicit style's TargetType matches
        // exactly and consumers can restyle trees independently of plain tables.
        DefaultStyleKey = typeof(TreeTableView);
    }

    /// <summary>
    /// Occurs when the user requests a collapsed item to expand (chevron click or Right key). Handlers insert the
    /// item's child rows into the flat items source right after it and set the item's
    /// <see cref="ITableViewTreeItem.IsExpanded"/> to <see langword="true"/>.
    /// </summary>
    public event EventHandler<TreeTableViewExpandCollapseEventArgs>? ExpandRequested;

    /// <summary>
    /// Occurs when the user requests an expanded item to collapse (chevron click or Left key). Handlers remove the
    /// item's descendant rows from the flat items source and set the item's
    /// <see cref="ITableViewTreeItem.IsExpanded"/> to <see langword="false"/>.
    /// </summary>
    public event EventHandler<TreeTableViewExpandCollapseEventArgs>? CollapseRequested;

    /// <summary>
    /// Raises <see cref="ExpandRequested"/> or <see cref="CollapseRequested"/> for the given item. No-op requests
    /// (expanding an already expanded item, one without children, or one whose asynchronous expansion is still in
    /// flight) are filtered out here so callers and the chevron/keyboard paths share one gate.
    /// </summary>
    /// <remarks>
    /// Expansion may complete asynchronously: the handler starts a backend query (setting
    /// <see cref="ITableViewTreeItem.IsLoading"/>) and inserts the children when they arrive. Because rows can be
    /// inserted or removed while that query runs, <see cref="TreeTableViewExpandCollapseEventArgs.Index"/> is only a
    /// hint valid at request time — locate the parent by item (reference/key) when inserting, not by the index.
    /// </remarks>
    /// <param name="item">The tree item.</param>
    /// <param name="index">The item's flat index in the items source at the time of the request.</param>
    /// <param name="expand"><see langword="true"/> to request an expand; <see langword="false"/> a collapse.</param>
    public void RequestExpandCollapse(ITableViewTreeItem item, int index, bool expand)
    {
        ArgumentNullException.ThrowIfNull(item);

        // IsFinalItem is the definitive leaf marker: no expand/collapse event is EVER raised for such items.
        if (item.IsFinalItem || item.IsLoading || expand == item.IsExpanded || (expand && !item.HasChildren))
        {
            return;
        }

        var args = new TreeTableViewExpandCollapseEventArgs(item, index);

        if (expand)
        {
            ExpandRequested?.Invoke(this, args);
        }
        else
        {
            CollapseRequested?.Invoke(this, args);
        }
    }

    /// <summary>
    /// Raises <see cref="ExpandRequested"/> or <see cref="CollapseRequested"/> for the given item, resolving its
    /// flat index from the items source. With a <see cref="TreeTableViewSource"/> bound the index comes from its
    /// handle map directly (O(log n), no platform round-trip); other sources fall back to
    /// <see cref="ItemsControl.Items"/> and may scan. Prefer the index-taking overload when the index is at hand.
    /// </summary>
    /// <param name="item">The tree item.</param>
    /// <param name="expand"><see langword="true"/> to request an expand; <see langword="false"/> a collapse.</param>
    public void RequestExpandCollapse(ITableViewTreeItem item, bool expand)
    {
        ArgumentNullException.ThrowIfNull(item);

        var index = ItemsSource is TreeTableViewSource source
            ? source.IndexOf(item)
            : Items.IndexOf(item);

        RequestExpandCollapse(item, index, expand);
    }

    /// <summary>
    /// Toggles expansion for the item behind a tree-column cell (double-click path). Returns whether the gesture
    /// was consumed: expandable or loading items toggle (loading ones are gated into a no-op), leaves return
    /// <see langword="false"/> so the caller can fall through to cell editing.
    /// </summary>
    internal bool ToggleExpandCollapseFromCell(TableViewCell cell)
    {
        if (cell.Row is { Content: ITableViewTreeItem { IsFinalItem: false } item } row
            && (item.HasChildren || item.IsLoading))
        {
            RequestExpandCollapse(item, row.Index, !item.IsExpanded);
            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        // Right expands / Left collapses the current row, the standard tree keyboarding. Only intercept when the
        // interaction is row-based (Row unit, or a row selection in CellOrRow/CellWithRow), where Left/Right have
        // no cell-navigation meaning; cell-based interaction keeps arrow navigation and uses double-click instead.
        if (!e.Handled && !IsEditing
            && e.Key is VirtualKey.Right or VirtualKey.Left
            && (SelectionUnit is TableViewSelectionUnit.Row || LastSelectionUnit is TableViewSelectionUnit.Row)
            && CurrentRowIndex is { } rowIndex && rowIndex >= 0 && rowIndex < Items.Count
            && Items[rowIndex] is ITableViewTreeItem item)
        {
            var expand = e.Key is VirtualKey.Right;

            // Only consume the key when it maps to an actual state change, so an already-collapsed row keeps
            // default Left behavior (if any) instead of swallowing the key.
            if (!item.IsFinalItem && !item.IsLoading && expand != item.IsExpanded && (!expand || item.HasChildren))
            {
                RequestExpandCollapse(item, rowIndex, expand);
                e.Handled = true;
                return;
            }
        }

        base.OnKeyDown(e);
    }
}
