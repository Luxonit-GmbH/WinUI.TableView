using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace WinUI.TableView;

/// <summary>
/// One group of rows produced by <see cref="TableView.GroupByPath"/>: a full-width header row that can be
/// collapsed, with its members underneath.
/// </summary>
/// <remarks>
/// It is both an <see cref="ITableViewBannerItem"/> — so the header renders across every column instead of as
/// cells — and an <see cref="ITableViewTreeItem"/>, which is what makes it collapsible AND lets its members be
/// tree items in their own right. Groups containing trees therefore fall out of the design rather than needing
/// a second mechanism.
/// </remarks>
public sealed partial class TableViewGroup : ITableViewTreeItem, ITableViewBannerItem
{
    private bool _isExpanded = true;

    /// <summary>
    /// Initializes a new group. Public so a <see cref="TableView.Grouping"/> handler can build its own.
    /// </summary>
    /// <param name="key">The value the members share. May be <see langword="null"/>.</param>
    /// <param name="items">
    /// The group's members, in the order they should appear. They may themselves be
    /// <see cref="TableViewGroup"/> instances, which is what makes grouping multi-level: a group is a tree node,
    /// so nesting needs no separate mechanism.
    /// </param>
    /// <param name="depth">How deep this group sits, so a tree column indents it correctly. Roots are 0.</param>
    public TableViewGroup(object? key, IEnumerable<object> items, int depth = 0)
    {
        Key = key;
        Items = [.. items];
        Depth = depth;
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets the value every member shares for the grouped property. May be <see langword="null"/>.
    /// </summary>
    public object? Key { get; }

    /// <summary>
    /// Gets the group's members, in source order.
    /// </summary>
    public ObservableCollection<object> Items { get; }

    /// <summary>
    /// Gets how many DATA rows the group holds, counting through any nested groups.
    /// </summary>
    /// <remarks>
    /// A parent in a multi-level grouping reports the leaves beneath it, not its number of sub-groups — "(12)"
    /// on a department should mean twelve people, however many currencies they are split across.
    /// </remarks>
    public int Count => Items.Sum(item => item is TableViewGroup nested ? nested.Count : 1);

    /// <summary>
    /// Gets the text shown in the header — the key, or "(none)" when it is null.
    /// </summary>
    public string Title => Key?.ToString() is { Length: > 0 } text ? text : "(none)";

    /// <inheritdoc/>
    /// <remarks>The group itself, so the header template can bind to <see cref="Title"/> and <see cref="Count"/>.</remarks>
    public object? BannerContent => this;

    /// <inheritdoc/>
    public int Depth { get; }

    /// <inheritdoc/>
    public IEnumerable? ChildrenSource => Items;

    /// <inheritdoc/>
    public bool HasChildren => Items.Count > 0;

    /// <inheritdoc/>
    public bool IsFinalItem => false;

    /// <inheritdoc/>
    public bool IsLoading => false;

    /// <inheritdoc/>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }
    }
}
