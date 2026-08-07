using System.Collections.Generic;
using System.ComponentModel;

namespace WinUI.TableView;

/// <summary>
/// Provides data for <see cref="TableView.Grouping"/>, raised whenever the grid is about to project its items
/// into groups.
/// </summary>
/// <remarks>
/// Set <see cref="Groups"/> and <see cref="HandledEventArgs.Handled"/> to group the items yourself — the same
/// shape as taking over <see cref="TableView.Sorting"/>. This is how grouping stays correct over a live source:
/// the grid cannot know when a re-group is warranted at thousands of mutations a second, but the app can, and
/// calls <see cref="TableView.RefreshGrouping"/> when it does.
/// </remarks>
public partial class TableViewGroupingEventArgs : HandledEventArgs
{
    /// <summary>
    /// Initializes a new instance of the TableViewGroupingEventArgs class.
    /// </summary>
    /// <param name="items">The items to be grouped, in source order.</param>
    /// <param name="groupByPath">The current <see cref="TableView.GroupByPath"/>, which may be null.</param>
    public TableViewGroupingEventArgs(IReadOnlyList<object> items, string? groupByPath)
    {
        Items = items;
        GroupByPath = groupByPath;
    }

    /// <summary>
    /// Gets the items to be grouped, in source order.
    /// </summary>
    public IReadOnlyList<object> Items { get; }

    /// <summary>
    /// Gets the grid's current <see cref="TableView.GroupByPath"/>. A handler may honour it, ignore it, or group
    /// by something the grid could not express as a path at all.
    /// </summary>
    public string? GroupByPath { get; }

    /// <summary>
    /// Gets or sets the groups to display, in display order. Only used when <see cref="HandledEventArgs.Handled"/>
    /// is set.
    /// </summary>
    /// <remarks>
    /// Expansion state is carried across a re-group by <see cref="TableViewGroup.Key"/>, so a group the user had
    /// collapsed stays collapsed as long as its key is stable.
    /// </remarks>
    public IList<TableViewGroup>? Groups { get; set; }
}
