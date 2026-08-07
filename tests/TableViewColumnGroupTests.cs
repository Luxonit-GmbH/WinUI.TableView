using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using System.Linq;
using System.Threading.Tasks;
using WinUI.TableView.Extensions;

namespace WinUI.TableView.Tests;

/// <summary>
/// Covers the column-group model: resolving banners to runs of visible columns, reporting the arrangements that
/// cannot be rendered, and collapsing a group down to its anchor column.
/// </summary>
[TestClass]
public class TableViewColumnGroupTests
{
    // ---------------------------------------------------------------------------------------------------------
    // Resolving spans
    // ---------------------------------------------------------------------------------------------------------

    [UITestMethod]
    public void Spans_CoverGroupedRuns_AndLeaveUngroupedColumnsAlone()
    {
        var tableView = Create(("A", null), ("Bid", "Prices"), ("Ask", "Prices"), ("Z", null));
        AddGroup(tableView, "Prices");

        var spans = Spans(tableView);

        Assert.AreEqual(3, spans.Count, "ungrouped columns stand alone rather than merging into one banner");
        Assert.IsNull(spans[0].Group);
        Assert.AreEqual("Prices", spans[1].Group?.Name);
        Assert.AreEqual(2, spans[1].Length);
        CollectionAssert.AreEqual(new[] { "Bid", "Ask" }, Headers(spans[1]));
        Assert.IsNull(spans[2].Group);
    }

    [UITestMethod]
    public void Spans_ShrinkWhenAMemberIsHidden()
    {
        var tableView = Create(("Bid", "Prices"), ("Mid", "Prices"), ("Ask", "Prices"));
        AddGroup(tableView, "Prices");

        tableView.Columns[1].Visibility = Visibility.Collapsed;

        var span = Spans(tableView).Single();
        Assert.AreEqual(2, span.Length);
        CollectionAssert.AreEqual(new[] { "Bid", "Ask" }, Headers(span));
    }

    [UITestMethod]
    public void Spans_SplitAcrossTheFrozenBoundary_BecauseOnlyOnePanelPans()
    {
        var tableView = Create(("Bid", "Prices"), ("Ask", "Prices"));
        AddGroup(tableView, "Prices");
        tableView.Columns[0].IsFrozen = true;

        var spans = Spans(tableView);

        Assert.AreEqual(2, spans.Count, "a banner cannot cover panels that scroll independently");
        Assert.IsTrue(spans[0].IsFrozen);
        Assert.IsFalse(spans[1].IsFrozen);
    }

    [UITestMethod]
    public void Spans_TreatAnUndefinedGroupNameAsUngrouped()
    {
        var tableView = Create(("Bid", "Ghost"), ("Ask", "Ghost"));

        var spans = Spans(tableView); // no group defined

        Assert.AreEqual(2, spans.Count);
        Assert.IsTrue(spans.All(span => span.Group is null));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Validation
    // ---------------------------------------------------------------------------------------------------------

    [UITestMethod]
    public void Validate_AcceptsAContiguousGroup()
    {
        var tableView = Create(("A", null), ("Bid", "Prices"), ("Ask", "Prices"));
        AddGroup(tableView, "Prices");

        Assert.AreEqual(0, tableView.ValidateColumnGroups().Count);
    }

    [UITestMethod]
    public void Validate_ReportsANonContiguousGroup()
    {
        var tableView = Create(("Bid", "Prices"), ("Middle", null), ("Ask", "Prices"));
        AddGroup(tableView, "Prices");

        var problems = tableView.ValidateColumnGroups();

        Assert.AreEqual(1, problems.Count);
        StringAssert.Contains(problems[0], "not contiguous");
    }

    [UITestMethod]
    public void Validate_ReportsAGroupStraddlingTheFrozenBoundary()
    {
        var tableView = Create(("Bid", "Prices"), ("Ask", "Prices"));
        AddGroup(tableView, "Prices");
        tableView.Columns[0].IsFrozen = true;

        var problems = tableView.ValidateColumnGroups();

        Assert.AreEqual(1, problems.Count);
        StringAssert.Contains(problems[0], "frozen");
    }

    [UITestMethod]
    public void Validate_ReportsAColumnNamingAGroupThatDoesNotExist()
    {
        var tableView = Create(("Bid", "Ghost"));

        var problems = tableView.ValidateColumnGroups();

        Assert.AreEqual(1, problems.Count);
        StringAssert.Contains(problems[0], "not defined");
    }

    [UITestMethod]
    public void Validate_ReportsDuplicateGroupNames()
    {
        var tableView = Create(("Bid", "Prices"));
        AddGroup(tableView, "Prices");
        AddGroup(tableView, "Prices");

        var problems = tableView.ValidateColumnGroups();

        Assert.IsTrue(problems.Any(problem => problem.Contains("More than one")));
    }

    [UITestMethod]
    public void Validate_SeesAGroupSplitByAHiddenColumn()
    {
        var tableView = Create(("Bid", "Prices"), ("Middle", null), ("Ask", "Prices"));
        AddGroup(tableView, "Prices");
        tableView.Columns[1].Visibility = Visibility.Collapsed;

        // The spans look fine while the intruder is hidden, but showing it again would split the banner — so the
        // problem is reported now rather than lying in wait.
        Assert.AreEqual(1, Spans(tableView).Count, "the visible run is contiguous today");
        Assert.AreEqual(1, tableView.ValidateColumnGroups().Count, "but the arrangement is still wrong");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Reorder constraints and the frozen boundary (phase 3)
    // ---------------------------------------------------------------------------------------------------------

    [UITestMethod]
    public void Reorder_KeepsAMemberInsideItsOwnGroup()
    {
        var tableView = Create(("A", null), ("Bid", "Prices"), ("Mid", "Prices"), ("Ask", "Prices"), ("Z", null));
        AddGroup(tableView, "Prices");
        var columns = (TableViewColumnsCollection)tableView.Columns;

        // Dragging Mid (index 2) out to either end must stop at its group's run, indexes 1..3.
        Assert.AreEqual(1, columns.ConstrainDropIndex(tableView.ColumnGroups, tableView.Columns[2], 0));
        Assert.AreEqual(3, columns.ConstrainDropIndex(tableView.ColumnGroups, tableView.Columns[2], 4));
        Assert.AreEqual(3, columns.ConstrainDropIndex(tableView.ColumnGroups, tableView.Columns[2], 3), "inside is fine");
    }

    [UITestMethod]
    public void Reorder_KeepsAnOutsiderFromSplittingAGroup()
    {
        var tableView = Create(("A", null), ("Bid", "Prices"), ("Mid", "Prices"), ("Ask", "Prices"), ("Z", null));
        AddGroup(tableView, "Prices");
        var columns = (TableViewColumnsCollection)tableView.Columns;
        var outsider = tableView.Columns[0];

        // Dropping between two members would cut the banner in half; it snaps to the nearer edge of the run.
        Assert.AreEqual(1, columns.ConstrainDropIndex(tableView.ColumnGroups, outsider, 2), "nearer the start");
        Assert.AreEqual(4, columns.ConstrainDropIndex(tableView.ColumnGroups, outsider, 3), "nearer the end");
        Assert.AreEqual(1, columns.ConstrainDropIndex(tableView.ColumnGroups, outsider, 1), "the boundary is allowed");
    }

    [UITestMethod]
    public void Reorder_IsUnconstrainedWithoutGroups()
    {
        var tableView = Create(("A", null), ("B", null), ("C", null));
        var columns = (TableViewColumnsCollection)tableView.Columns;

        Assert.AreEqual(2, columns.ConstrainDropIndex(tableView.ColumnGroups, tableView.Columns[0], 2));
    }

    [UITestMethod]
    public async Task FreezingOneMember_FreezesTheWholeGroup()
    {
        var tableView = Create(("Bid", "Prices"), ("Mid", "Prices"), ("Ask", "Prices"));
        AddGroup(tableView, "Prices");
        SetWidths(tableView, 100, 100, 100);
        await LoadAsync(tableView);

        // The frozen panel does not pan and the scrollable one does, so a straddling group is unrenderable.
        // Rather than let it happen and report it, the group follows.
        tableView.Columns[1].IsFrozen = true;
        await SettleAsync(tableView);

        Assert.IsTrue(tableView.Columns.All(column => column.IsFrozen));
        Assert.AreEqual(0, tableView.ValidateColumnGroups().Count);

        tableView.Columns[0].IsFrozen = false;
        await SettleAsync(tableView);

        Assert.IsTrue(tableView.Columns.All(column => !column.IsFrozen));

        await UnloadAsync(tableView);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Keyboard and accessibility (phase 4)
    // ---------------------------------------------------------------------------------------------------------

    [UITestMethod]
    public async Task Banner_ReportsExpandCollapseToAutomation()
    {
        var tableView = Create(("Bid", "Prices"), ("Ask", "Prices"));
        var group = AddGroup(tableView, "Prices");
        SetWidths(tableView, 100, 100);
        await LoadAsync(tableView);

        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(Banners(tableView)[0]);
        var pattern = (IExpandCollapseProvider)peer.GetPattern(PatternInterface.ExpandCollapse);

        Assert.AreEqual("Prices", peer.GetName());
        Assert.AreEqual(ExpandCollapseState.Expanded, pattern.ExpandCollapseState);

        pattern.Collapse();
        await SettleAsync(tableView);

        Assert.IsTrue(group.IsCollapsed);
        Assert.AreEqual(ExpandCollapseState.Collapsed, pattern.ExpandCollapseState);

        pattern.Expand();
        await SettleAsync(tableView);
        Assert.IsFalse(group.IsCollapsed);

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task Banner_ThatCannotCollapse_ReportsLeafNode()
    {
        var tableView = Create(("Bid", "Prices"), ("Ask", "Prices"));
        var group = AddGroup(tableView, "Prices");
        group.IsCollapsible = false;
        SetWidths(tableView, 100, 100);
        await LoadAsync(tableView);

        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(Banners(tableView)[0]);

        Assert.IsNull(peer.GetPattern(PatternInterface.ExpandCollapse),
            "a group that can never collapse must not advertise the pattern");

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task Banner_TogglesFromTheKeyboard()
    {
        var tableView = Create(("Bid", "Prices"), ("Ask", "Prices"));
        var group = AddGroup(tableView, "Prices");
        SetWidths(tableView, 100, 100);
        await LoadAsync(tableView);

        Assert.IsTrue(Banners(tableView)[0].Toggle());
        await SettleAsync(tableView);
        Assert.IsTrue(group.IsCollapsed);

        Assert.IsTrue(Banners(tableView)[0].Toggle());
        await SettleAsync(tableView);
        Assert.IsFalse(group.IsCollapsed);

        await UnloadAsync(tableView);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Collapse and expand
    // ---------------------------------------------------------------------------------------------------------

    [UITestMethod]
    public void Collapse_KeepsTheAnchorColumnVisible()
    {
        var tableView = Create(("Bid", "Prices"), ("Mid", "Prices"), ("Ask", "Prices"));
        var group = AddGroup(tableView, "Prices");

        tableView.SetColumnGroupCollapsed(group, true);

        Assert.IsTrue(group.IsCollapsed);
        CollectionAssert.AreEqual(new[] { "Bid" }, VisibleHeaders(tableView),
            "a zero-width banner would leave nothing to click to expand again");
    }

    [UITestMethod]
    public void Collapse_HonoursAChosenAnchor()
    {
        var tableView = Create(("Bid", "Prices"), ("Mid", "Prices"), ("Ask", "Prices"));
        var group = AddGroup(tableView, "Prices");
        group.CollapsedColumn = tableView.Columns[1];

        tableView.SetColumnGroupCollapsed(group, true);

        CollectionAssert.AreEqual(new[] { "Mid" }, VisibleHeaders(tableView));
    }

    [UITestMethod]
    public void Expand_RestoresWhatWasVisible_NotEverything()
    {
        var tableView = Create(("Bid", "Prices"), ("Mid", "Prices"), ("Ask", "Prices"));
        var group = AddGroup(tableView, "Prices");

        tableView.Columns[2].Visibility = Visibility.Collapsed; // the app hid Ask deliberately

        tableView.SetColumnGroupCollapsed(group, true);
        tableView.SetColumnGroupCollapsed(group, false);

        CollectionAssert.AreEqual(new[] { "Bid", "Mid" }, VisibleHeaders(tableView),
            "expanding a group must not resurrect a column the app had hidden");
        Assert.IsFalse(group.IsCollapsed);
    }

    [UITestMethod]
    public void Collapse_IsIdempotent()
    {
        var tableView = Create(("Bid", "Prices"), ("Mid", "Prices"));
        var group = AddGroup(tableView, "Prices");

        tableView.SetColumnGroupCollapsed(group, true);
        tableView.SetColumnGroupCollapsed(group, true);
        tableView.SetColumnGroupCollapsed(group, false);

        CollectionAssert.AreEqual(new[] { "Bid", "Mid" }, VisibleHeaders(tableView));
    }

    [UITestMethod]
    public void Collapse_LeavesOtherGroupsAlone()
    {
        var tableView = Create(("Bid", "Prices"), ("Ask", "Prices"), ("Qty", "Size"), ("Lot", "Size"));
        var prices = AddGroup(tableView, "Prices");
        AddGroup(tableView, "Size");

        tableView.SetColumnGroupCollapsed(prices, true);

        CollectionAssert.AreEqual(new[] { "Bid", "Qty", "Lot" }, VisibleHeaders(tableView));
    }

    [UITestMethod]
    public void Xaml_NameAttribute_SetsTheGroupName()
    {
        // TableViewColumnGroup is a DependencyObject, not a FrameworkElement, so there is no built-in Name for
        // XAML to bind the attribute to. If it ever stopped reaching the CLR property the groups would silently
        // match no columns and the banner row would come up empty — with no build error to notice.
        const string xaml = """
            <TableViewColumnGroup xmlns="using:WinUI.TableView" Name="Prices" Header="Prices" />
            """;

        var group = (TableViewColumnGroup)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);

        Assert.AreEqual("Prices", group.Name);
        Assert.AreEqual("Prices", group.Header);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Rendering
    // ---------------------------------------------------------------------------------------------------------

    [UITestMethod]
    public async Task Rendering_ProducesOneBannerPerSpan_SizedToItsColumns()
    {
        var tableView = Create(("A", null), ("Bid", "Prices"), ("Ask", "Prices"));
        AddGroup(tableView, "Prices");
        SetWidths(tableView, 100, 120, 140);

        await LoadAsync(tableView);

        var banners = Banners(tableView);
        Assert.AreEqual(2, banners.Count, "one filler over the ungrouped column, one banner over the group");
        Assert.IsNull(banners[0].Group);
        Assert.AreEqual("Prices", banners[1].Group?.Name);
        Assert.AreEqual(100d, banners[0].Width, 1d);
        Assert.AreEqual(260d, banners[1].Width, 1d, "the banner spans both of its columns");

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task Rendering_AddsNothingWhenNoGroupsAreDefined()
    {
        var tableView = Create(("A", null), ("B", null));
        SetWidths(tableView, 100, 100);

        await LoadAsync(tableView);

        Assert.AreEqual(0, Banners(tableView).Count,
            "an ungrouped grid must pay nothing, and the Auto row measures to zero");

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task Rendering_FollowsACollapse()
    {
        var tableView = Create(("Bid", "Prices"), ("Mid", "Prices"), ("Ask", "Prices"));
        var group = AddGroup(tableView, "Prices");
        SetWidths(tableView, 100, 100, 100);

        await LoadAsync(tableView);
        Assert.AreEqual(300d, Banners(tableView)[0].Width, 1d);

        tableView.SetColumnGroupCollapsed(group, true);
        await SettleAsync(tableView);

        Assert.AreEqual(100d, Banners(tableView)[0].Width, 1d, "the banner shrinks to its anchor column");

        tableView.SetColumnGroupCollapsed(group, false);
        await SettleAsync(tableView);

        Assert.AreEqual(300d, Banners(tableView)[0].Width, 1d);

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task Rendering_PutsBannersABOVE_TheColumnHeaders()
    {
        var tableView = Create(("Bid", "Prices"), ("Ask", "Prices"));
        AddGroup(tableView, "Prices");
        SetWidths(tableView, 100, 100);

        await LoadAsync(tableView);

        var banner = Banners(tableView)[0];
        var header = HeaderRow(tableView).FindDescendants().OfType<TableViewColumnHeader>().First();

        var bannerBottom = banner.TransformToVisual(HeaderRow(tableView)).TransformPoint(default).Y + banner.ActualHeight;
        var headerTop = header.TransformToVisual(HeaderRow(tableView)).TransformPoint(default).Y;

        // ArrangeOverride hand-places the headers panel, so a hardcoded y=0 there drags the headers up ON TOP of
        // the banners — which is exactly what it did, and no structural assertion noticed.
        Assert.IsTrue(banner.ActualHeight > 0, "the banner has height");
        Assert.IsTrue(headerTop >= bannerBottom - 1,
            $"column headers must start below the banners: banner ends at {bannerBottom}, header starts at {headerTop}");

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task Rendering_TurnsTheChevron_ToShowTheState()
    {
        var tableView = Create(("Bid", "Prices"), ("Ask", "Prices"));
        var group = AddGroup(tableView, "Prices");
        SetWidths(tableView, 100, 100);

        await LoadAsync(tableView);

        // The chevron is one glyph turned by a RenderTransform rather than two glyphs swapped, so the angle is
        // what carries the state — and it can be animated without costing a layout pass.
        Assert.AreEqual(0d, ChevronAngle(tableView), 1d, "expanded points one way");

        tableView.SetColumnGroupCollapsed(group, true);
        await SettleAsync(tableView);
        await Task.Delay(250); // let the 150ms turn finish

        Assert.AreEqual(180d, ChevronAngle(tableView), 1d, "collapsed points the other");

        tableView.SetColumnGroupCollapsed(group, false);
        await SettleAsync(tableView);
        await Task.Delay(250);

        Assert.AreEqual(0d, ChevronAngle(tableView), 1d, "and back again");

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    public async Task Rendering_DoesNotSpinTheChevronOnFirstRender()
    {
        var tableView = Create(("Bid", "Prices"), ("Ask", "Prices"));
        var group = AddGroup(tableView, "Prices");
        group.IsCollapsed = true; // already collapsed before the banner exists
        SetWidths(tableView, 100, 100);

        await LoadAsync(tableView);

        // No animation to wait for: a banner appearing already-collapsed must adopt the angle, not turn into it,
        // or every banner spins on load and while scrolling recycles them.
        Assert.AreEqual(180d, ChevronAngle(tableView), 1d);

        await UnloadAsync(tableView);
    }

    private static double ChevronAngle(TableView tableView)
        => Banners(tableView)[0]
            .FindDescendants()
            .OfType<FontIcon>()
            .Select(icon => (icon.RenderTransform as RotateTransform)?.Angle ?? double.NaN)
            .First();

    [UITestMethod]
    public async Task Rendering_SplitsAcrossTheFrozenAndScrollablePanels()
    {
        var tableView = Create(("Key", "Ids"), ("Bid", "Prices"), ("Ask", "Prices"));
        AddGroup(tableView, "Ids");
        AddGroup(tableView, "Prices");
        SetWidths(tableView, 100, 100, 100);
        tableView.Columns[0].IsFrozen = true;

        await LoadAsync(tableView);

        var headerRow = HeaderRow(tableView);
        Assert.AreEqual(1, Panel(headerRow, "FrozenSpannersPanel").Children.Count);
        Assert.AreEqual(1, Panel(headerRow, "ScrollableSpannersPanel").Children.Count);

        await UnloadAsync(tableView);
    }

    private static async Task LoadAsync(TableView tableView)
    {
        tableView.Width = 800;
        tableView.Height = 400;
        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(tableView);
        await SettleAsync(tableView);
    }

    private static async Task SettleAsync(TableView tableView)
    {
        // Header rebuild and width calculation are both dispatcher-coalesced.
        tableView.UpdateLayout();
        await Task.Yield();
        tableView.UpdateLayout();
        await Task.Yield();
        tableView.UpdateLayout();
    }

    private static Task UnloadAsync(TableView tableView)
        => UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);

    private static TableViewHeaderRow HeaderRow(TableView tableView)
        => tableView.FindDescendant<TableViewHeaderRow>()!;

    private static Panel Panel(TableViewHeaderRow headerRow, string name)
        => (Panel)headerRow.FindDescendants().First(element => element is FrameworkElement { } fe && fe.Name == name);

    private static System.Collections.Generic.List<TableViewColumnGroupHeader> Banners(TableView tableView)
        => [.. HeaderRow(tableView).FindDescendants().OfType<TableViewColumnGroupHeader>()];

    private static void SetWidths(TableView tableView, params double[] widths)
    {
        for (var i = 0; i < widths.Length; i++)
        {
            tableView.Columns[i].Width = new GridLength(widths[i], GridUnitType.Pixel);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------

    private static TableViewColumnGroup AddGroup(TableView tableView, string name)
    {
        var group = new TableViewColumnGroup { Name = name, Header = name };
        tableView.ColumnGroups.Add(group);
        return group;
    }

    private static System.Collections.Generic.IReadOnlyList<TableViewColumnGroupSpan> Spans(TableView tableView)
        => ((TableViewColumnsCollection)tableView.Columns).GetColumnGroupSpans(tableView.ColumnGroups);

    private static string[] Headers(TableViewColumnGroupSpan span)
        => [.. span.Columns.Select(column => column.Header?.ToString() ?? "")];

    private static string[] VisibleHeaders(TableView tableView)
        => [.. tableView.Columns.VisibleColumns.Select(column => column.Header?.ToString() ?? "")];

    private static TableView Create(params (string Header, string? GroupName)[] columns)
    {
        var tableView = new TableView { AutoGenerateColumns = false };

        foreach (var (header, groupName) in columns)
        {
            tableView.Columns.Add(new TableViewTextColumn { Header = header, GroupName = groupName });
        }

        return tableView;
    }
}
