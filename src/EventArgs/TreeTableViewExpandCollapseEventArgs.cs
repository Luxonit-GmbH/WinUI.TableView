using System;

namespace WinUI.TableView;

/// <summary>
/// Provides data for the <see cref="TreeTableView.ExpandRequested"/> and
/// <see cref="TreeTableView.CollapseRequested"/> events. These events only ever fire for items implementing
/// <see cref="ITableViewTreeItem"/> (and never for <see cref="ITableViewTreeItem.IsFinalItem"/> items), so the
/// item is strongly typed.
/// </summary>
public partial class TreeTableViewExpandCollapseEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the TreeTableViewExpandCollapseEventArgs class.
    /// </summary>
    /// <param name="item">The tree item the request is for.</param>
    /// <param name="index">The item's flat index in the items source at the time of the request.</param>
    public TreeTableViewExpandCollapseEventArgs(ITableViewTreeItem item, int index)
    {
        Item = item;
        Index = index;
    }

    /// <summary>
    /// Gets the tree item the request is for.
    /// </summary>
    public ITableViewTreeItem Item { get; }

    /// <summary>
    /// Gets the item's flat index in the items source at the time of the request. Only a hint valid at request
    /// time — with asynchronous expansion, locate the item by reference/key when inserting children.
    /// </summary>
    public int Index { get; }
}
