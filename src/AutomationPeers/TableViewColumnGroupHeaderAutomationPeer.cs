using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Automation;

namespace WinUI.TableView.AutomationPeers;

/// <summary>
/// Exposes a column-group banner to assistive technology: its name, and — when the group can be collapsed — the
/// expand/collapse pattern so a screen reader can both announce and change the state.
/// </summary>
public partial class TableViewColumnGroupHeaderAutomationPeer : FrameworkElementAutomationPeer, IExpandCollapseProvider
{
    private readonly TableViewColumnGroupHeader _owner;

    /// <summary>
    /// Initializes a new instance of the TableViewColumnGroupHeaderAutomationPeer class.
    /// </summary>
    /// <param name="owner">The banner this peer represents.</param>
    public TableViewColumnGroupHeaderAutomationPeer(TableViewColumnGroupHeader owner) : base(owner)
    {
        _owner = owner;
    }

    /// <inheritdoc/>
    protected override string GetClassNameCore() => nameof(TableViewColumnGroupHeader);

    /// <inheritdoc/>
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Header;

    /// <inheritdoc/>
    protected override string GetNameCore()
        => _owner.Group?.Header?.ToString()
           ?? _owner.Group?.Name
           ?? base.GetNameCore();

    /// <inheritdoc/>
    protected override object GetPatternCore(PatternInterface patternInterface)
        => patternInterface is PatternInterface.ExpandCollapse && _owner.Group is { IsCollapsible: true }
            ? this
            : base.GetPatternCore(patternInterface);

    /// <inheritdoc/>
    /// <remarks>
    /// A group that cannot be collapsed reports LeafNode rather than a state it will never leave.
    /// </remarks>
    public ExpandCollapseState ExpandCollapseState => _owner.Group switch
    {
        { IsCollapsible: false } => ExpandCollapseState.LeafNode,
        { IsCollapsed: true } => ExpandCollapseState.Collapsed,
        { } => ExpandCollapseState.Expanded,
        _ => ExpandCollapseState.LeafNode,
    };

    /// <inheritdoc/>
    public void Expand()
    {
        if (_owner.Group?.IsCollapsed is true)
        {
            _owner.Toggle();
        }
    }

    /// <inheritdoc/>
    public void Collapse()
    {
        if (_owner.Group?.IsCollapsed is false)
        {
            _owner.Toggle();
        }
    }
}
