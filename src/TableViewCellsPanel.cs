using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Foundation;
using WinUI.TableView.Extensions;

namespace WinUI.TableView;

/// <summary>
/// Hosts a row's scrollable <see cref="TableViewCell"/>s, sizing and positioning them by the cached cumulative
/// column offsets. The horizontal measure-virtualization itself lives in <see cref="TableViewCell"/>: off-screen
/// columns (flagged by RealizeVisibleCells) collapse their content and skip the expensive content measure. This
/// panel always measures each cell at its full column width and arranges it at its column offset, so the row's
/// clip and RenderTransform pan the cells exactly as before. When the offsets aren't yet available it falls back to
/// a horizontal StackPanel-equivalent layout.
/// </summary>
public partial class TableViewCellsPanel : Panel
{
    private TableView? _tableView;

    private TableView? OwningTableView => _tableView ??= this.FindAscendant<TableView>();

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        // Cache the children collection and each child reference: Children / Children[i] are COM interop calls
        // (UIElementCollection.get_Children / get_ListItem), and a 50-column row measured thousands of times turns
        // those into hundreds of thousands of calls. Fetch once.
        var children = Children;
        var count = children.Count;
        if (count == 0)
        {
            return new Size(0, 0);
        }

        var tableView = OwningTableView;
        var offsets = tableView?.ScrollableColumnOffsets ?? [];
        var availableHeight = availableSize.Height;

        // Rows are uniform, so the panel's height is the (explicit) RowHeight or the finite available height — no need
        // to read every child's DesiredSize.Height and Max them (which the profile showed as 300k+ calls). Only fall
        // back to tracking the tallest child when neither is known.
        var knownHeight = tableView is { RowHeight: var rowHeight } && !double.IsNaN(rowHeight) ? rowHeight
                        : !double.IsInfinity(availableHeight) ? availableHeight
                        : double.NaN;
        var trackHeight = double.IsNaN(knownHeight);
        var measuredHeight = 0d;

        // Offsets not ready / out of sync with the children: measure the way a horizontal StackPanel would (each
        // cell's own Width drives its desired width).
        if (offsets.Length != count)
        {
            var total = 0d;
            for (var i = 0; i < count; i++)
            {
                var child = children[i];
                child.Measure(new Size(double.PositiveInfinity, availableHeight));
                total += child.DesiredSize.Width;

                if (trackHeight)
                {
                    measuredHeight = Math.Max(measuredHeight, child.DesiredSize.Height);
                }
            }

            return new Size(total, trackHeight ? measuredHeight : knownHeight);
        }

        // Measure every cell at its column width. Off-screen cells are Collapsed (Measure is a no-op for them), so
        // this only does real work for the on-screen band.
        for (var i = 0; i < count; i++)
        {
            var child = children[i];
            var width = offsets[i] - (i == 0 ? 0d : offsets[i - 1]);
            child.Measure(new Size(width, availableHeight));

            if (trackHeight)
            {
                measuredHeight = Math.Max(measuredHeight, child.DesiredSize.Height);
            }
        }

        return new Size(offsets[^1], trackHeight ? measuredHeight : knownHeight);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Children;
        var count = children.Count;
        if (count == 0)
        {
            return finalSize;
        }

        var offsets = OwningTableView?.ScrollableColumnOffsets ?? [];

        if (offsets.Length != count)
        {
            // Fallback: lay out left-to-right by desired width (StackPanel-equivalent).
            var x = 0d;
            for (var i = 0; i < count; i++)
            {
                var child = children[i];
                var w = child.DesiredSize.Width;
                child.Arrange(new Rect(x, 0, w, finalSize.Height));
                x += w;
            }

            return finalSize;
        }

        // Always arrange every cell at its column's cumulative offset (not offset-adjusted) — the row's
        // RenderTransform pans the whole panel, so off-screen cells land in the correct place when revealed.
        for (var i = 0; i < count; i++)
        {
            var left = i == 0 ? 0d : offsets[i - 1];
            children[i].Arrange(new Rect(left, 0, offsets[i] - left, finalSize.Height));
        }

        return finalSize;
    }
}
