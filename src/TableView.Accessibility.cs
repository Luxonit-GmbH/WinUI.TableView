using Microsoft.UI.Xaml.Automation.Peers;
using System.Collections.Generic;
using System.Linq;
using WinUI.TableView.AutomationPeers;

namespace WinUI.TableView;

/// <summary>
/// Partial class for TableView that provides UI Automation support.
/// </summary>
public partial class TableView
{
    /// <summary>
    /// Gets the currently realized row containers, ordered by row index. Returns a snapshot: the fork tracks
    /// realized rows in an unordered set, while automation clients expect indexable, visually ordered rows.
    /// (Casting the set to IReadOnlyList would throw InvalidCastException the moment a UIA client asks.)
    /// Only queried by automation peers, so the copy is off any hot path.
    /// </summary>
    internal IReadOnlyList<TableViewRow> Rows => [.. _rows.OrderBy(static x => x.Index)];

    /// <inheritdoc/>
    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return new TableViewAutomationPeer(this);
    }
}
