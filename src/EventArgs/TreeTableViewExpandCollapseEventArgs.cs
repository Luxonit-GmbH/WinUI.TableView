using System;

namespace WinUI.TableView;

/// <summary>
/// Provides data for the <see cref="TreeTableView.ExpandRequested"/> and
/// <see cref="TreeTableView.CollapseRequested"/> events.
/// </summary>
public partial class TreeTableViewExpandCollapseEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the TreeTableViewExpandCollapseEventArgs class.
    /// </summary>
    /// <param name="item">The tree item the request is for.</param>
    /// <param name="index">The item's flat index in the items source at the time of the request.</param>
    public TreeTableViewExpandCollapseEventArgs(object item, int index)
    {
        Item = item;
        Index = index;
    }

    /// <summary>
    /// Gets the tree item the request is for.
    /// </summary>
    public object Item { get; }

    /// <summary>
    /// Gets the item's flat index in the items source at the time of the request.
    /// </summary>
    public int Index { get; }
}
