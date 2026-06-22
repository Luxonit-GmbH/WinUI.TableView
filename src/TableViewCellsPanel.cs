using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Foundation;
using WinUI.TableView.Extensions;

namespace WinUI.TableView;

/// <summary>
/// Hosts a row's scrollable <see cref="TableViewCell"/>s. When column virtualization is enabled it measures only the
/// cells whose columns fall within the horizontal viewport — off-screen cells are measured at zero width so their
/// content subtree is not measured (the expensive part). Cells are always arranged at their column's cumulative
/// offset, so the row's RenderTransform pans them and an off-screen cell still occupies the correct slot when it is
/// later measured. When virtualization is off (or the column offsets are not yet available) it falls back to a
/// horizontal StackPanel-equivalent layout.
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

        var tableView = OwningTableView;
        var offsets = tableView?.ScrollableColumnOffsets ?? [];
        var height = double.IsInfinity(availableSize.Height) ? 0d : availableSize.Height;

        // Fallback: no virtualization, or the offsets aren't ready / don't line up with the children. Measure every
        // child the way a horizontal StackPanel would (each cell's explicit Width drives its desired width).
        if (tableView is null || !tableView.IsColumnVirtualizationEnabled || offsets.Length != count)
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

        var (first, last) = tableView.GetVisibleScrollableRange();

        for (var i = 0; i < count; i++)
        {
            var width = offsets[i] - (i == 0 ? 0d : offsets[i - 1]);
            var child = Children[i];

            if (first >= 0 && i >= first && i <= last)
            {
                child.Measure(new Size(width, availableSize.Height));

                if (double.IsInfinity(availableSize.Height))
                {
                    height = Math.Max(height, child.DesiredSize.Height);
                }
            }
            else
            {
                // Off-screen: zero available width makes the cell collapse its content presenter, so the content
                // subtree is not measured. The cell still gets its real slot in ArrangeOverride.
                child.Measure(new Size(0d, availableSize.Height));
            }
        }

        // The panel spans the full scrollable width regardless of which cells were measured, so the row's clip and
        // RenderTransform (which handle the actual horizontal scroll) behave exactly as before.
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
