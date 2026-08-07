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
    public string? GroupByPath
    {
        get => (string?)GetValue(GroupByPathProperty);
        set => SetValue(GroupByPathProperty, value);
    }

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
    /// Gets whether rows are currently being presented in groups.
    /// </summary>
    internal bool IsGrouping => !string.IsNullOrWhiteSpace(GroupByPath) && ShowGroupHeaders;

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
        foreach (var group in Groups)
        {
            SetGroupExpanded(group, expanded);
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
        _groupedSource?.Dispose();
        _groupedSource = null;
        Groups = [];

        if (source is null || !IsGrouping)
        {
            return source;
        }

        var path = GroupByPath!;
        var ordered = new List<TableViewGroup>();
        var byKey = new Dictionary<object, TableViewGroup>();
        var buckets = new List<(object? Key, List<object> Items)>();
        var index = new Dictionary<object, int>();
        var nullBucket = -1;

        foreach (var item in source.OfType<object>())
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

        foreach (var (key, items) in sorted)
        {
            ordered.Add(new TableViewGroup(key, items));
        }

        Groups = ordered;

        var roots = new ObservableCollection<ITableViewTreeItem>(ordered);
        _groupedSource = new TreeTableViewSource(roots);
        return _groupedSource;
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
