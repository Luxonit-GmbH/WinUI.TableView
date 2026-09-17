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
    private UIElement[] _childSnapshot = [];
    private double _lastArrangedHeight = double.NaN; // diagnostic: a changed height re-arranges every in-band cell

    private TableView? OwningTableView => _tableView ??= this.FindAscendant<TableView>();

    /// <summary>
    /// Tells the panel its children have changed, so the cached child references are rebuilt on the next pass.
    /// </summary>
    /// <remarks>
    /// Reading <c>Children[i]</c> is two projected calls — the collection indexer, then resolving the returned
    /// <c>IInspectable</c> back to its managed wrapper — and the measure and arrange loops do it once per column
    /// per row per layout pass. Holding the references in a managed array removes about half the interop on that
    /// path. The snapshot MUST be invalidated on every mutation, not merely when the count changes: moving a column
    /// removes and re-inserts one cell, so the count returns to where it was while the order is now different, and
    /// a stale snapshot would measure and arrange every cell at the wrong column offset while the headers, which
    /// live in a different panel, stayed correct.
    /// </remarks>
    internal void InvalidateChildSnapshot()
    {
        _childSnapshot = [];
    }

    /// <summary>
    /// The children as a managed array, rebuilt when the cached copy no longer matches the collection.
    /// </summary>
    private UIElement[] GetChildSnapshot(UIElementCollection children, int count)
    {
        // The count check is a self-heal for any mutation path that forgets to invalidate, not the primary
        // mechanism — see the remarks on InvalidateChildSnapshot for why count alone is not enough.
        if (_childSnapshot.Length == count)
        {
            return _childSnapshot;
        }

        var snapshot = new UIElement[count];
        for (var i = 0; i < count; i++)
        {
            snapshot[i] = children[i];
        }

        return _childSnapshot = snapshot;
    }

    /// <summary>
    /// Whether out-of-band children may be skipped this pass, and a safety net against skipping everything.
    /// </summary>
    /// <remarks>
    /// <para>A cell that is outside the realized band is collapsed, so measuring and arranging it achieves nothing
    /// — but the calls themselves are not free. Each one crosses into the XAML core, and the loops run for every
    /// child of every realized row on every layout pass, which a horizontal band change triggers. At eighty
    /// columns that is the difference between touching the twenty cells in view and touching all eighty.</para>
    /// <para>The decision is read from the cell's own managed flag, written by the same call that collapses it, so
    /// there is no second source of truth to fall out of step — a stale band would show as a permanently blank
    /// column. Two further guards: the skip is off entirely unless column virtualization is on, because with it
    /// off no one sets the flag; and if it would skip every child, nothing is skipped, so a row can never come out
    /// completely empty.</para>
    /// </remarks>
    private bool CanSkipOutOfBand(UIElement[] children, int count)
    {
        if (OwningTableView?.IsColumnVirtualizationEnabled is not true)
        {
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            if (children[i] is TableViewCell { IsInViewport: true })
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsOutOfBand(UIElement child)
    {
        return child is TableViewCell { IsInViewport: false };
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        // Cache the children collection and each child reference: Children / Children[i] are COM interop calls
        // (UIElementCollection.get_Children / get_ListItem), and a 50-column row measured thousands of times turns
        // those into hundreds of thousands of calls. Fetch once.
        var collection = Children;
        var count = collection.Count;
        if (count == 0)
        {
            _childSnapshot = [];
            return new Size(0, 0);
        }

        var children = GetChildSnapshot(collection, count);

        var tableView = OwningTableView;
        tableView?.NoteCellsPanelMeasure();
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

        // Measure the cells in the realized band at their column width, and skip the rest: they are collapsed, so
        // the call would do nothing but cross into the XAML core. See CanSkipOutOfBand.
        var skipOutOfBand = CanSkipOutOfBand(children, count);

        for (var i = 0; i < count; i++)
        {
            var child = children[i];

            if (skipOutOfBand && IsOutOfBand(child))
            {
                continue;
            }

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
        var collection = Children;
        var count = collection.Count;
        if (count == 0)
        {
            _childSnapshot = [];
            return finalSize;
        }

        var children = GetChildSnapshot(collection, count);

        var tableView = OwningTableView;
        tableView?.NoteCellsPanelArrange(finalSize.Height != _lastArrangedHeight);
        _lastArrangedHeight = finalSize.Height;

        var offsets = tableView?.ScrollableColumnOffsets ?? [];

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

        // Arrange each in-band cell at its column's cumulative offset (not offset-adjusted) — the items panel pans
        // as one visual, so a cell lands in the correct place when it is revealed. Out-of-band cells are collapsed
        // and therefore not rendered; arranging them is the same wasted crossing as measuring them, and a cell is
        // always re-measured and re-arranged on the pass that reveals it.
        var skipOutOfBand = CanSkipOutOfBand(children, count);

        for (var i = 0; i < count; i++)
        {
            var child = children[i];

            if (skipOutOfBand && IsOutOfBand(child))
            {
                continue;
            }

            var left = i == 0 ? 0d : offsets[i - 1];
            child.Arrange(new Rect(left, 0, offsets[i] - left, finalSize.Height));
        }

        return finalSize;
    }
}
