using Microsoft.UI.Xaml;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using WinUI.TableView.Extensions;

namespace WinUI.TableView;

/// <summary>
/// Row grouping: the consumer binds a FLAT collection and names a property, and the grid projects it into
/// collapsible groups with full-width header rows.
/// </summary>
/// <remarks>
/// The projection is expressed as a tree — each <see cref="TableViewGroup"/> is a parent node — and handed to
/// <see cref="TreeTableViewSource"/>. That reuses the adapter's index math, bulk coalescing, ISelectionInfo and
/// IItemsRangeInfo instead of adding a second flattening implementation, works in direct-binding mode, and lets
/// a group's members be tree items themselves.
/// </remarks>
public partial class TableView
{
    /// <summary>Separates key-chain segments; it cannot occur in a rendered key, so chains never collide.</summary>
    private const char SeparatorChar = '\u001F';

    private IEnumerable? _ungroupedSource;
    private TreeTableViewSource? _groupedSource;

    /// <summary>
    /// Identifies the <see cref="GroupByPath"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty GroupByPathProperty = DependencyProperty.Register(
        nameof(GroupByPath), typeof(string), typeof(TableView), new PropertyMetadata(null, OnGroupingChanged));

    /// <summary>
    /// Identifies the <see cref="ShowGroupHeaders"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty ShowGroupHeadersProperty = DependencyProperty.Register(
        nameof(ShowGroupHeaders), typeof(bool), typeof(TableView), new PropertyMetadata(true, OnGroupingChanged));

    /// <summary>
    /// Identifies the <see cref="ShowGroupItemCount"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty ShowGroupItemCountProperty = DependencyProperty.Register(
        nameof(ShowGroupItemCount), typeof(bool), typeof(TableView), new PropertyMetadata(true));

    /// <summary>
    /// Identifies the <see cref="GroupSortDirection"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty GroupSortDirectionProperty = DependencyProperty.Register(
        nameof(GroupSortDirection), typeof(SortDirection?), typeof(TableView), new PropertyMetadata(SortDirection.Ascending, OnGroupingChanged));

    /// <summary>
    /// Identifies the <see cref="GroupHeaderTemplate"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty GroupHeaderTemplateProperty = DependencyProperty.Register(
        nameof(GroupHeaderTemplate), typeof(DataTemplate), typeof(TableView), new PropertyMetadata(null, OnGroupingChanged));

    /// <summary>
    /// Gets or sets the property path to group rows by. <see langword="null"/> or empty means no grouping.
    /// </summary>
    /// <remarks>
    /// Comma-separate paths to nest: <c>GroupByPath="Department,Currency"</c> groups by department, then by
    /// currency within each. Nesting costs nothing extra structurally — a group is a tree node, so a group's
    /// members being groups is the same mechanism.
    /// </remarks>
    public string? GroupByPath
    {
        get => (string?)GetValue(GroupByPathProperty);
        set => SetValue(GroupByPathProperty, value);
    }

    /// <summary>
    /// Splits <see cref="GroupByPath"/> into its levels.
    /// </summary>
    private string[] GroupByPaths =>
        GroupByPath is { Length: > 0 } path
            ? [.. path.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : [];

    /// <summary>
    /// Gets or sets whether group header rows are shown. When <see langword="false"/> the rows are presented flat,
    /// exactly as if grouping were off.
    /// </summary>
    public bool ShowGroupHeaders
    {
        get => (bool)GetValue(ShowGroupHeadersProperty);
        set => SetValue(ShowGroupHeadersProperty, value);
    }

    /// <summary>
    /// Gets or sets whether the member count appears beside each group's title.
    /// </summary>
    public bool ShowGroupItemCount
    {
        get => (bool)GetValue(ShowGroupItemCountProperty);
        set => SetValue(ShowGroupItemCountProperty, value);
    }

    /// <summary>
    /// Gets or sets how the groups themselves are ordered by key. <see langword="null"/> keeps the order the keys
    /// were first encountered in.
    /// </summary>
    public SortDirection? GroupSortDirection
    {
        get => (SortDirection?)GetValue(GroupSortDirectionProperty);
        set => SetValue(GroupSortDirectionProperty, value);
    }

    /// <summary>
    /// Gets or sets the template for group header rows. Defaults to a chevron, the title and the item count.
    /// </summary>
    public DataTemplate? GroupHeaderTemplate
    {
        get => (DataTemplate?)GetValue(GroupHeaderTemplateProperty);
        set => SetValue(GroupHeaderTemplateProperty, value);
    }

    /// <summary>
    /// Gets the groups currently projected from the items source, in display order. Empty when not grouping.
    /// </summary>
    public IReadOnlyList<TableViewGroup> Groups { get; private set; } = [];

    /// <summary>
    /// Occurs before the items are projected into groups. Set
    /// <see cref="TableViewGroupingEventArgs.Groups"/> and Handled to group them yourself.
    /// </summary>
    /// <remarks>
    /// The grid cannot tell when a live source needs re-grouping — at thousands of mutations a second any
    /// automatic invalidation would either miss changes or thrash — so the app owns that decision and calls
    /// <see cref="RefreshGrouping"/>.
    /// </remarks>
    public event EventHandler<TableViewGroupingEventArgs>? Grouping;

    /// <summary>
    /// Raises the <see cref="Grouping"/> event.
    /// </summary>
    /// <param name="args">The event data.</param>
    protected virtual void OnGrouping(TableViewGroupingEventArgs args) => Grouping?.Invoke(this, args);

    /// <summary>
    /// Re-projects the items into groups, preserving each group's expanded state by
    /// <see cref="TableViewGroup.Key"/>.
    /// </summary>
    /// <remarks>Call after the data changes in a way that should change the grouping.</remarks>
    public void RefreshGrouping() => RebuildGrouping();

    /// <summary>
    /// Gets whether rows are currently being presented in groups.
    /// </summary>
    /// <remarks>
    /// A <see cref="Grouping"/> handler counts as grouping even with no <see cref="GroupByPath"/> set — it may
    /// group by something no property path could express.
    /// </remarks>
    internal bool IsGrouping => ShowGroupHeaders && (!string.IsNullOrWhiteSpace(GroupByPath) || Grouping is not null);

    private static void OnGroupingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TableView tableView)
        {
            tableView.RebuildGrouping();
        }
    }

    /// <summary>
    /// Expands or collapses a group, splicing its member rows in or out.
    /// </summary>
    /// <param name="group">The group to change.</param>
    /// <param name="expanded"><see langword="true"/> to expand.</param>
    public void SetGroupExpanded(TableViewGroup group, bool expanded)
    {
        ArgumentNullException.ThrowIfNull(group);

        if (_groupedSource is null)
        {
            group.IsExpanded = expanded;
            return;
        }

        if (expanded)
        {
            _groupedSource.Expand(group);
        }
        else
        {
            _groupedSource.Collapse(group);
        }
    }

    /// <summary>
    /// Expands or collapses every group at once.
    /// </summary>
    /// <param name="expanded"><see langword="true"/> to expand all.</param>
    public void SetAllGroupsExpanded(bool expanded)
    {
        // Depth-first when expanding so a parent opens before its children are reachable; the reverse when
        // collapsing, since collapsing a parent removes its descendants from the flat view first.
        foreach (var group in expanded ? AllGroups(Groups) : AllGroups(Groups).Reverse())
        {
            SetGroupExpanded(group, expanded);
        }
    }

    /// <summary>
    /// Every group at every level, parents before children.
    /// </summary>
    private static IEnumerable<TableViewGroup> AllGroups(IEnumerable<TableViewGroup> groups)
    {
        foreach (var group in groups)
        {
            yield return group;

            foreach (var nested in AllGroups(group.Items.OfType<TableViewGroup>()))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// Rebuilds the grouped projection from the consumer's source, or unwinds it when grouping is off.
    /// </summary>
    private void RebuildGrouping()
    {
        if (_ungroupedSource is null)
        {
            return; // no source yet; ItemsSourceChanged will route through here once there is one
        }

        ApplyEffectiveItemsSource(_ungroupedSource);
    }

    /// <summary>
    /// Projects the source into groups and wraps them in a tree adapter, or returns the source untouched when
    /// grouping is off.
    /// </summary>
    private IEnumerable? BuildGroupedSource(IEnumerable? source)
    {
        // Remember what was collapsed BEFORE the old groups go away, so a refresh does not silently re-open
        // everything the user had shut. Identified by the CHAIN of keys from the root, not the key alone: the
        // same key can appear under different parents ("EUR" beneath two departments) and those are not the
        // same group.
        var collapsed = new HashSet<string>();
        CaptureCollapsed(Groups, string.Empty, collapsed);

        _groupedSource?.Dispose();
        _groupedSource = null;
        Groups = [];

        if (source is null || !IsGrouping)
        {
            return source;
        }

        var items = source.OfType<object>().ToList();
        var args = new TableViewGroupingEventArgs(items, GroupByPath);
        OnGrouping(args);

        if (args.Handled)
        {
            return FinishGrouping([.. args.Groups ?? []], collapsed);
        }

        var paths = GroupByPaths;

        if (paths.Length == 0)
        {
            return source; // a handler was subscribed but declined, and there is no path to fall back on
        }

        return FinishGrouping(BuildGroups(items, paths, 0), collapsed);
    }

    /// <summary>
    /// Records the key-chain of every collapsed group, at any level.
    /// </summary>
    private static void CaptureCollapsed(IEnumerable<TableViewGroup> groups, string prefix, HashSet<string> collapsed)
    {
        foreach (var group in groups)
        {
            var id = GroupIdentity(prefix, group);

            if (!group.IsExpanded)
            {
                collapsed.Add(id);
            }

            CaptureCollapsed(group.Items.OfType<TableViewGroup>(), id, collapsed);
        }
    }

    /// <summary>
    /// A group's identity across a re-projection: its key chain from the root. The unit separator cannot appear
    /// in a rendered key, so two different chains can never collide.
    /// </summary>
    private static string GroupIdentity(string prefix, TableViewGroup group)
        => prefix + (group.Key?.ToString() ?? string.Empty) + SeparatorChar;

    /// <summary>
    /// Buckets items by one path level, recursing for the next.
    /// </summary>
    /// <remarks>
    /// Recursion is bounded by the number of comma-separated paths, so there is no runaway case to guard.
    /// </remarks>
    private List<TableViewGroup> BuildGroups(List<object> items, string[] paths, int level)
    {
        var path = paths[level];
        var buckets = new List<(object? Key, List<object> Items)>();
        var index = new Dictionary<object, int>();
        var nullBucket = -1;

        foreach (var item in items)
        {
            // Resolved per item rather than once: the source may hold more than one type, and a compiled getter
            // is bound to the type it was built for.
            var key = item.GetCompiledValueGetter(path)?.Invoke(item);

            int bucket;

            if (key is null)
            {
                if (nullBucket < 0)
                {
                    nullBucket = buckets.Count;
                    buckets.Add((null, []));
                }

                bucket = nullBucket;
            }
            else if (!index.TryGetValue(key, out bucket))
            {
                bucket = buckets.Count;
                buckets.Add((key, []));
                index[key] = bucket;
            }

            buckets[bucket].Items.Add(item);
        }

        IEnumerable<(object? Key, List<object> Items)> sorted = GroupSortDirection switch
        {
            SortDirection.Ascending => buckets.OrderBy(bucket => bucket.Key, GroupKeyComparer.Instance),
            SortDirection.Descending => buckets.OrderByDescending(bucket => bucket.Key, GroupKeyComparer.Instance),
            _ => buckets,
        };

        var ordered = new List<TableViewGroup>();

        foreach (var (key, members) in sorted)
        {
            // The next level down produces groups; the last produces the rows themselves.
            var children = level + 1 < paths.Length
                ? BuildGroups(members, paths, level + 1).Cast<object>().ToList()
                : members;

            ordered.Add(new TableViewGroup(key, children, level));
        }

        return ordered;
    }

    /// <summary>
    /// Restores expansion state and wraps the groups in the tree adapter.
    /// </summary>
    /// <remarks>
    /// Expansion is applied BEFORE the adapter is built: it flattens by reading IsExpanded, so a group marked
    /// collapsed afterwards would have already had its members spliced in.
    /// </remarks>
    private IEnumerable FinishGrouping(List<TableViewGroup> groups, HashSet<string> collapsed)
    {
        RestoreCollapsed(groups, string.Empty, collapsed);

        Groups = groups;

        var roots = new ObservableCollection<ITableViewTreeItem>(groups);
        _groupedSource = new TreeTableViewSource(roots);
        return _groupedSource;
    }

    /// <summary>
    /// Re-applies remembered collapse state at every level.
    /// </summary>
    private static void RestoreCollapsed(IEnumerable<TableViewGroup> groups, string prefix, HashSet<string> collapsed)
    {
        foreach (var group in groups)
        {
            var id = GroupIdentity(prefix, group);
            group.IsExpanded = !collapsed.Contains(id);

            RestoreCollapsed(group.Items.OfType<TableViewGroup>(), id, collapsed);
        }
    }

    /// <summary>
    /// Orders group keys, tolerating mixed and non-comparable keys instead of throwing on them.
    /// </summary>
    private sealed class GroupKeyComparer : IComparer<object?>
    {
        public static readonly GroupKeyComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            if (x is IComparable comparable && x.GetType() == y.GetType())
            {
                return comparable.CompareTo(y);
            }

            return string.CompareOrdinal(x.ToString(), y.ToString());
        }
    }
}
