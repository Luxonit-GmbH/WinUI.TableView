using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace WinUI.TableView.AutomationPeers;

/// <summary>
/// Exposes <see cref="TableViewRow"/> to UI Automation.
/// Extends <see cref="ListViewItemAutomationPeer"/> to provide row-specific automation information
/// including meaningful names that convey the row index and content.
/// </summary>
public partial class TableViewRowAutomationPeer : ListViewItemAutomationPeer, IExpandCollapseProvider
{
    private readonly TableViewRow _owner;

    /// <summary>
    /// Initializes a new instance of the <see cref="TableViewRowAutomationPeer"/> class.
    /// </summary>
    /// <param name="owner">The <see cref="TableViewRow"/> that is associated with this peer.</param>
    public TableViewRowAutomationPeer(TableViewRow owner) : base(owner)
    {
        _owner = owner;
    }

    /// <inheritdoc/>
    protected override string GetClassNameCore()
    {
        return nameof(TableViewRow);
    }

    /// <inheritdoc/>
    protected override AutomationControlType GetAutomationControlTypeCore()
    {
        return AutomationControlType.DataItem;
    }

    /// <inheritdoc/>
    protected override string GetNameCore()
    {
        var name = AutomationProperties.GetName(_owner);
        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }

        var index = _owner.Index;
        return index >= 0 ? $"Row {index + 1}" : "Row";
    }

    /// <inheritdoc/>
    protected override string GetHelpTextCore()
    {
        var tableView = _owner.TableView;
        if (tableView is null || _owner.Index < 0)
        {
            return base.GetHelpTextCore();
        }

        var parts = new System.Collections.Generic.List<string>();
        foreach (var cell in _owner.Cells)
        {
            if (cell.Column is { } column)
            {
                var headerText = GetColumnHeaderText(column);
                var value = column.GetCellContent(_owner.Content)?.ToString() ?? string.Empty;
                if (!string.IsNullOrEmpty(headerText))
                {
                    parts.Add($"{headerText}: {value}");
                }
            }
        }

        return parts.Count > 0 ? string.Join(", ", parts) : base.GetHelpTextCore();
    }

    /// <inheritdoc/>
    protected override object GetPatternCore(PatternInterface patternInterface)
    {
        if (patternInterface == PatternInterface.ExpandCollapse && (IsTreeRow() || HasRowDetails()))
        {
            return this;
        }

        return base.GetPatternCore(patternInterface);
    }

    /// <inheritdoc/>
    protected override int GetLevelCore()
    {
        // Report the tree level (1-based, per UIA) so screen readers announce the hierarchy.
        return _owner.Content is ITableViewTreeItem item && _owner.TableView is TreeTableView
            ? item.Depth + 1
            : base.GetLevelCore();
    }

    /// <inheritdoc/>
    protected override int GetPositionInSetCore()
        => GetTreeSiblingInfo() is { } info ? info.Position : base.GetPositionInSetCore();

    /// <inheritdoc/>
    protected override int GetSizeOfSetCore()
        => GetTreeSiblingInfo() is { } info ? info.Count : base.GetSizeOfSetCore();

    /// <summary>
    /// Sibling position/count for tree rows ("item 2 of 5"), available when the tree is fed by a
    /// <see cref="TreeTableViewSource"/> (which owns the sibling structure).
    /// </summary>
    private (int Position, int Count)? GetTreeSiblingInfo()
    {
        return _owner.TableView is TreeTableView { ItemsSource: TreeTableViewSource source }
            && _owner.Content is { } item
            ? source.GetSiblingInfo(item)
            : null;
    }

    // IExpandCollapseProvider — a tree row exposes its tree expansion; otherwise the row details panel.

    /// <summary>
    /// Gets the expanded/collapsed state of the row: tree expansion for a <see cref="TreeTableView"/> row whose
    /// item is an <see cref="ITableViewTreeItem"/> (leaves report LeafNode), otherwise the row details panel state.
    /// </summary>
    public ExpandCollapseState ExpandCollapseState
    {
        get
        {
            if (IsTreeRow())
            {
                var item = (ITableViewTreeItem)_owner.Content!;
                return item.IsFinalItem || !item.HasChildren ? ExpandCollapseState.LeafNode
                    : item.IsExpanded ? ExpandCollapseState.Expanded
                    : item.IsLoading ? ExpandCollapseState.PartiallyExpanded
                    : ExpandCollapseState.Collapsed;
            }

            return (_owner.RowPresenter?.IsDetailsPanelVisible ?? false)
                ? ExpandCollapseState.Expanded
                : ExpandCollapseState.Collapsed;
        }
    }

    /// <summary>
    /// Expands the tree row (via <see cref="TreeTableView.RequestExpandCollapse"/>) or the row details panel.
    /// </summary>
    public void Expand()
    {
        if (IsTreeRow())
        {
            ((TreeTableView)_owner.TableView!).RequestExpandCollapse((ITableViewTreeItem)_owner.Content!, _owner.Index, expand: true);
        }
        else if (_owner.TableView is not null && HasRowDetails())
        {
            _owner.RowPresenter?.ShowDetailPane(visible: true);
        }
    }

    /// <summary>
    /// Collapses the tree row (via <see cref="TreeTableView.RequestExpandCollapse"/>) or the row details panel.
    /// </summary>
    public void Collapse()
    {
        if (IsTreeRow())
        {
            ((TreeTableView)_owner.TableView!).RequestExpandCollapse((ITableViewTreeItem)_owner.Content!, _owner.Index, expand: false);
        }
        else if (_owner.TableView is not null && HasRowDetails())
        {
            _owner.RowPresenter?.ShowDetailPane(visible: false);
        }
    }

    private bool IsTreeRow()
    {
        return _owner.TableView is TreeTableView && _owner.Content is ITableViewTreeItem;
    }

    private bool HasRowDetails()
    {
        return (_owner.TableView?.RowDetailsTemplate is not null
            || _owner.TableView?.RowDetailsTemplateSelector is not null)
            && _owner.TableView?.RowDetailsVisibilityMode is TableViewRowDetailsVisibilityMode.VisibleWhenExpanded;
    }

    /// <summary>
    /// Gets the header text for a column as a string.
    /// </summary>
    internal static string GetColumnHeaderText(TableViewColumn column)
    {
        return column.Header switch
        {
            string s => s,
            { } obj => obj.ToString() ?? string.Empty,
            _ => string.Empty
        };
    }
}
