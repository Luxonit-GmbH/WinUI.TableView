using System.Collections.Generic;

namespace WinUI.TableView;

/// <summary>
/// A resolved group: the banner and the run of currently visible columns it covers. Produced by
/// <see cref="TableViewColumnsCollection.GetColumnGroupSpans"/> for the header to lay out, and recomputed
/// whenever the visible column set changes.
/// </summary>
/// <param name="Group">The group definition, or <see langword="null"/> for a run of ungrouped columns.</param>
/// <param name="Columns">The visible columns the banner covers, in display order. Never empty.</param>
/// <param name="FirstIndex">Index of the first covered column within its frozen or scrollable run.</param>
/// <param name="IsFrozen">Whether this span sits in the frozen run rather than the scrollable one.</param>
public readonly record struct TableViewColumnGroupSpan(
    TableViewColumnGroup? Group,
    IReadOnlyList<TableViewColumn> Columns,
    int FirstIndex,
    bool IsFrozen)
{
    /// <summary>
    /// Gets how many visible columns the banner spans.
    /// </summary>
    public int Length => Columns.Count;
}
