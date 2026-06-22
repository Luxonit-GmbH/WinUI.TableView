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
        var count = Children.Count;
        if (count == 0)
        {
            return new Size(0, 0);
        }

        var offsets = OwningTableView?.ScrollableColumnOffsets ?? [];
        var height = double.IsInfinity(availableSize.Height) ? 0d : availableSize.Height;

        // Offsets not ready / out of sync with the children: measure the way a horizontal StackPanel would (each
        // cell's own Width drives its desired width).
        if (offsets.Length != count)
        {
            var total = 0d;
            for (var i = 0; i < count; i++)
            {
                var child = Children[i];
                child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
                total += child.DesiredSize.Width;

                if (double.IsInfinity(availableSize.Height))
                {
                    height = Math.Max(height, child.DesiredSize.Height);
                }
            }

            return new Size(total, height);
        }

        // Measure every cell at its column width. Off-screen cells collapse their own content internally, so this
        // stays cheap for non-visible columns while still giving on-screen cells their real width.
        for (var i = 0; i < count; i++)
        {
            var width = offsets[i] - (i == 0 ? 0d : offsets[i - 1]);
            Children[i].Measure(new Size(width, availableSize.Height));

            if (double.IsInfinity(availableSize.Height))
            {
                height = Math.Max(height, Children[i].DesiredSize.Height);
            }
        }

        return new Size(offsets[^1], height);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = Children.Count;
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
                var w = Children[i].DesiredSize.Width;
                Children[i].Arrange(new Rect(x, 0, w, finalSize.Height));
                x += w;
            }

            return finalSize;
        }

        // Always arrange every cell at its column's cumulative offset (not offset-adjusted) — the row's
        // RenderTransform pans the whole panel, so off-screen cells land in the correct place when revealed.
        for (var i = 0; i < count; i++)
        {
            var left = i == 0 ? 0d : offsets[i - 1];
            Children[i].Arrange(new Rect(left, 0, offsets[i] - left, finalSize.Height));
        }

        return finalSize;
    }
}
