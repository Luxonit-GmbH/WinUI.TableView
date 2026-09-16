using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using WinUI.TableView.Columns;
using WinUI.TableView.Extensions;
using WinUI.TableView.Helpers;

namespace WinUI.TableView;

/// <summary>
/// Represents a cell in a TableView.
/// </summary>
[TemplateVisualState(Name = VisualStates.StateNormal, GroupName = VisualStates.GroupCommon)]
[TemplateVisualState(Name = VisualStates.StatePointerOver, GroupName = VisualStates.GroupCommon)]
[TemplateVisualState(Name = VisualStates.StateRegular, GroupName = VisualStates.GroupCurrent)]
[TemplateVisualState(Name = VisualStates.StateCurrent, GroupName = VisualStates.GroupCurrent)]
[TemplateVisualState(Name = VisualStates.StateSelected, GroupName = VisualStates.GroupSelection)]
[TemplateVisualState(Name = VisualStates.StateUnselected, GroupName = VisualStates.GroupSelection)]
#if WINDOWS
[WinRT.GeneratedBindableCustomProperty]
#endif
public partial class TableViewCell : ContentControl
{
    private ContentPresenter? _contentPresenter;
    private Border? _selectionBorder;
    private Border? _backgroundBorder;
    private Border? _rootBorder;
    private Rectangle? _v_gridLine;
    private object? _uneditedValue;
    private RoutedEventArgs? _editingArgs;
    private double _contentDesiredWidth = double.NaN;
    private bool _contentPending;
    private bool _isInViewport;

    /// <summary>
    /// Whether this cell's column is inside the realized band, as a plain managed field rather than a read of
    /// <see cref="UIElement.Visibility"/>.
    /// </summary>
    /// <remarks>
    /// The cells panel reads this to decide which children to measure and arrange, and it is written by the same
    /// call that writes <see cref="UIElement.Visibility"/>, so the two cannot drift apart. Reading the visibility
    /// property instead would cost a projected call per child per layout pass, which is the very thing the skip
    /// exists to avoid.
    /// </remarks>
    internal bool IsInViewport => _isInViewport;
    private bool _dataContextPinned;              // content element's DataContext is held as a local value (see PinContentDataContext)
    private object? _pinnedItem;                  // the row item that local value was taken from; same item on unpin = nothing to rebind
    // Cache key for the last applied content constraint (see ConstrainContent): the constraint depends only on these,
    // not on the cell's value, so unchanged passes can skip the recompute + the MaxWidth/MaxHeight/Visibility sets.
    private FrameworkElement? _constrainedElement;
    // The selection visual state last handed to the visual state manager, so a repeat is a field
    // compare. Null means "never applied", which is also the state after a template (re)application,
    // since applying a template discards the visual state the manager had set.
    private bool? _appliedSelectionState;
    private double _constrainedColumnWidth = double.NaN;
    private double _constrainedRowHeight = double.NaN;
    private object? _resolvedContentKey;          // Content reference the cached resolved element was computed for
    private FrameworkElement? _resolvedContentElement;
    private bool _autoMinWidthMeasured;           // AutoSizeMinWidth: this cell already contributed its first-render width
    private IList<TableViewConditionalCellStyle>? _cellStyles;
    private bool _resizePreviewActive;
    private double _resizePreviewWidth;
    private double _resizePreviewMaxWidth;
    private RectangleGeometry? _resizeClipGeometry;
    private TranslateTransform? _gridLineShiftTransform;
    private TranslateTransform? _downstreamShiftTransform;

    /// <summary>
    /// Initializes a new instance of the TableViewCell class.
    /// </summary>
    public TableViewCell()
    {
        DefaultStyleKey = typeof(TableViewCell);
        // No Loaded handler. WinUI raises Loaded on every descendant when a recycled container is re-attached to
        // the live tree, so a per-cell handler is a full pass over every column of every recycled row — an entire
        // O(columns) cost on the hottest path in the control, invisible to anyone reading call sites. What it did
        // is covered elsewhere: the row invalidates its presenter's measure on recycle, and selection state is
        // applied from OnApplyTemplate and again whenever a cell is revealed (see SetInViewport).
#if WINDOWS
        ContextRequested += OnContextRequested;
#endif
    }

#if !WINDOWS
    /// <inheritdoc/>
    protected override void OnRightTapped(RightTappedRoutedEventArgs e)
    {
        base.OnRightTapped(e);

        var position = e.GetPosition(this);
#else
    /// <summary>
    /// Handles the ContextRequested event.
    /// </summary>
    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (!e.TryGetPosition(sender, out var position)) return;
#endif

        // Select the cell before showing the Context Menu, honouring Ctrl/Shift like a left click would.
        TableView?.ApplyContextRequestSelection(Slot, IsSelected);

        e.Handled = TableView?.ShowCellContext(this, position) is true;
    }


    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _contentPresenter = GetTemplateChild("Content") as ContentPresenter;
        _selectionBorder = GetTemplateChild("SelectionBorder") as Border;
        _backgroundBorder = GetTemplateChild("BackgroundBorder") as Border;
        _rootBorder = GetTemplateChild("RootBorder") as Border;
        _v_gridLine = GetTemplateChild("VerticalGridLine") as Rectangle;

        TableView?.NoteCellTemplateApplied();

        EnsureGridLines();
        EnsureStyle(Row?.Content);

        // A fresh template has no visual state, so whatever was applied before no longer holds. Clear the memo
        // BEFORE re-applying, or the call below sees "already in that state" and the cell renders unselected.
        _appliedSelectionState = null;

        // The template carries the selection visuals, so a cell whose template is applied late — which is every
        // off-band cell, since a collapsed element is never measured — has to be told its state here. This is what
        // replaces the old per-cell Loaded handler.
        ApplySelectionState();
    }

    /// <inheritdoc/>
    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);

        // The content element changed, so any cached desired width is stale. Allow AutoSizeMinWidth to re-measure
        // this NEW content once (a recycled/regenerated element is "first rendered" content, not a data update).
        _contentDesiredWidth = double.NaN;
        _autoMinWidthMeasured = false;
        _dataContextPinned = false; // a fresh element inherits; it is pinned again if the band has moved away
        _pinnedItem = null;

        // Re-measuring unconstrained once the content loads feeds the column's desired (auto) width and the
        // AutoSizeMinWidth minimum. Skip subscribing for plain fixed/star columns to avoid an extra measure pass.
        if ((Column?.Width.IsAuto is true || Column?.AutoSizeMinWidth is true) && newContent is ContentControl contentControl)
        {
            contentControl.Loaded += OnContentLoaded;
        }

        void OnContentLoaded(object sender, RoutedEventArgs e)
        {
            ((ContentControl)sender).Loaded -= OnContentLoaded;
            _contentDesiredWidth = double.NaN;
            _autoMinWidthMeasured = false; // content is now loaded; let the next pass capture its true width
            InvalidateMeasure();
        }
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        TableView?.NoteCellMeasure();

        // Horizontal measure-virtualization: when the cell's column is outside the viewport (set by
        // RealizeVisibleCells), collapse the content presenter so its subtree is NOT measured — this is the
        // expensive part, and it's skipped even when the content has already been realized (e.g. by prefetch).
        // The cell still occupies its column slot, and SetInViewport(true) re-measures it (content restored)
        // when it scrolls into view. We key off this flag rather than availableSize because ConstrainContent
        // sizes the content from Column.ActualWidth and would otherwise measure the full subtree regardless.
        if (!_isInViewport && _contentPresenter is not null && TableView?.IsColumnVirtualizationEnabled is true)
        {
            _contentPresenter.Visibility = Visibility.Collapsed;
            return base.MeasureOverride(availableSize);
        }

        if (Column is not null && Row is not null && _contentPresenter is not null && ResolveContentElement() is { } element)
        {
            // Measuring the content unconstrained only feeds the column's desired (auto) width. For fixed and
            // star sized columns it is pure overhead on every measure pass, so only do it for auto columns.
            // Skipped while the column is being dragged: it only feeds Column.DesiredWidth, which a pixel-width
            // drag ignores, and a preview-mode drag deliberately does not remeasure this cell per frame.
            if (Column.Width.IsAuto && !Column.IsResizing)
            {
                EnsureDesiredWidth(element);
            }

            // AutoSizeMinWidth: once, on this cell's first render, measure the content's natural width and grow the
            // column's auto minimum. Capture is sealed on first scroll (CanCaptureAutoMinWidth), so only the initial
            // cells contribute — cells realized later while scrolling add no measure overhead. The per-cell flag
            // also stops data updates / recycles from re-measuring.
            if (Column.AutoSizeMinWidth && !_autoMinWidthMeasured && TableView?.CanCaptureAutoMinWidth is true)
            {
                _autoMinWidthMeasured = true;
                Column.GrowAutoMinWidth(!double.IsNaN(_contentDesiredWidth) ? _contentDesiredWidth : MeasureContentDesiredWidth(element));
            }

            ConstrainContent(element);
        }

        return base.MeasureOverride(availableSize);
    }

    /// <summary>
    /// Resolves the content element to measure, drilling into the template root for non-default columns.
    /// Returns <see langword="null"/> when there is nothing measurable.
    /// </summary>
    private FrameworkElement? ResolveContentElement()
    {
        // Resolving walks Content's type/template; the result only changes when Content itself changes. Cache it
        // keyed on the Content reference so the per-measure path (hit on every data-tick re-measure) is a ref compare.
        var content = Content;
        if (ReferenceEquals(content, _resolvedContentKey))
        {
            return _resolvedContentElement;
        }

        _resolvedContentKey = content;
        _resolvedContentElement = ResolveContentElementCore(content);
        return _resolvedContentElement;
    }

    private FrameworkElement? ResolveContentElementCore(object? content)
    {
        if (content is not FrameworkElement element)
        {
            return null;
        }

        if (Column is not IDefaultTableViewColumn)
        {
#if WINDOWS
            if (element is ContentControl { ContentTemplateRoot: FrameworkElement root })
#else
            if (element.FindDescendant<ContentPresenter>() is { ContentTemplateRoot: FrameworkElement root })
#endif
            {
                return root;
            }

            // Non-templated content (e.g. a UserControl) is measured/constrained directly so that auto-sized
            // columns can size to it instead of collapsing to MinColumnWidth.
        }

        return element;
    }

    /// <summary>
    /// Grows the owning column's desired width to fit this cell. The expensive unconstrained measurement is
    /// performed only when the cached value has been invalidated (content swapped or row recycled); see
    /// <see cref="InvalidateDesiredWidth"/>. On unchanged passes the cached value is reused.
    /// </summary>
    private void EnsureDesiredWidth(FrameworkElement element)
    {
        if (Column is null)
        {
            return;
        }

        // Only contribute when the mode actually sizes from cells. Our fork dropped this check during the
        // measure-caching work; upstream still has it, so a Headers-only column was being widened by its content
        // anyway.
        var autoWidthMode = Column.ColumnAutoWidthMode ?? TableView?.ColumnAutoWidthMode;

        if (autoWidthMode is not (TableViewColumnAutoWidthMode.Cells or TableViewColumnAutoWidthMode.Both))
        {
            return;
        }

        if (double.IsNaN(_contentDesiredWidth))
        {
            _contentDesiredWidth = MeasureContentDesiredWidth(element);
        }

        Column.DesiredWidth = Math.Max(Column.DesiredWidth, _contentDesiredWidth);
    }

    /// <summary>
    /// Measures the content unconstrained and returns the desired width including the cell's chrome (padding,
    /// borders and grid line).
    /// </summary>
    private double MeasureContentDesiredWidth(FrameworkElement element)
    {
        // TEMP_FIX_FOR_ISSUE https://github.com/microsoft/microsoft-ui-xaml/issues/9860
        element.MaxWidth = double.PositiveInfinity;
        element.MaxHeight = double.PositiveInfinity;

        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var desiredWidth = element.DesiredSize.Width;
        desiredWidth += Padding.Left;
        desiredWidth += Padding.Right;
        desiredWidth += BorderThickness.Left;
        desiredWidth += BorderThickness.Right;
        desiredWidth += _selectionBorder?.BorderThickness.Right ?? 0;
        desiredWidth += _selectionBorder?.BorderThickness.Left ?? 0;
        desiredWidth += GridLineWidth;

        return desiredWidth;
    }

    /// <summary>
    /// The width the vertical grid line takes out of the content's room: its configured width, when it is shown.
    /// </summary>
    /// <remarks>
    /// Not its <c>ActualWidth</c>. That is 0 until the line's first arrange, and the constraint cache in
    /// <see cref="ConstrainContent"/> is keyed on the column width and the row height alone, so a cell constrained
    /// before that arrange — every cell, on its first measure — kept the 0 for its life and gave the content one
    /// pixel more than the column has. The configured width is what <see cref="EnsureGridLines"/> writes into the
    /// line, and it is right before anything has been laid out, which is also when the idle prefetch constrains.
    /// </remarks>
    private double GridLineWidth
        => _v_gridLine is { Visibility: Visibility.Visible } line && !double.IsNaN(line.Width) ? line.Width : 0d;

    /// <summary>
    /// Invalidates the cached content desired width so the next auto-size measure re-measures the content.
    /// Called when the cell's element or its data item changes.
    /// </summary>
    internal void InvalidateDesiredWidth()
    {
        _contentDesiredWidth = double.NaN;
    }

    /// <summary>
    /// Constrains the content element to the column's actual width and the cell's height, collapsing it when there
    /// is no room. Runs for every column regardless of sizing mode.
    /// </summary>
    private void ConstrainContent(FrameworkElement element)
    {
        if (Column is null || _contentPresenter is null)
        {
            return;
        }

        // While a resize preview is active the content was already generously measured once in BeginResizePreview
        // and must keep that width, so Clip can reveal or hide it each frame without another Measure pass. Using
        // the live Column.ActualWidth here would re-clamp it straight back to the pre-drag size — that width is
        // deliberately frozen for the whole drag (see TableView.UpdateColumnResizePreview). The preview width is
        // itself constant for the gesture, so it also keeps the cache key below stable.
        var columnWidth = _resizePreviewActive ? _resizePreviewWidth : Column.ActualWidth;
        var rowHeight = !double.IsNaN(Height) ? Height
                      : ActualHeight > 0 ? ActualHeight
                      : double.PositiveInfinity;

        // The applied constraint (content MaxWidth/MaxHeight + presenter visibility) depends only on the column
        // width, the row height and the content element — not on the cell's value. Skip the recompute and the DP sets
        // when none changed, which is the steady state for fixed-width columns + uniform rows (i.e. most measures and
        // every value-only data tick).
        if (ReferenceEquals(element, _constrainedElement)
            && columnWidth == _constrainedColumnWidth
            && rowHeight == _constrainedRowHeight)
        {
            return;
        }

        _constrainedElement = element;
        _constrainedColumnWidth = columnWidth;
        _constrainedRowHeight = rowHeight;

        // TEMP_FIX_FOR_ISSUE https://github.com/microsoft/microsoft-ui-xaml/issues/9860
        var contentWidth = columnWidth;
        contentWidth -= element.Margin.Left;
        contentWidth -= element.Margin.Right;
        contentWidth -= Padding.Left;
        contentWidth -= Padding.Right;
        contentWidth -= BorderThickness.Left;
        contentWidth -= BorderThickness.Right;
        contentWidth -= _selectionBorder?.BorderThickness.Left ?? 0;
        contentWidth -= _selectionBorder?.BorderThickness.Right ?? 0;
        contentWidth -= GridLineWidth;

        // rowHeight bounds the content height: when no explicit RowHeight is set the cells panel would otherwise let
        // the content do an unbounded vertical layout (it's inside a vertically-scrolling ItemsStackPanel, so the
        // available height is infinite), on every pass and every data tick. For the common uniform-height grid the
        // settled height is exactly the natural row height, so nothing is clipped. (Set RowHeight for a fixed bound.)
        var contentHeight = Math.Min(rowHeight, MaxHeight);
        contentHeight -= element.Margin.Top;
        contentHeight -= element.Margin.Bottom;
        contentHeight -= Padding.Top;
        contentHeight -= Padding.Bottom;
        contentHeight -= BorderThickness.Top;
        contentHeight -= BorderThickness.Bottom;
        contentHeight -= _selectionBorder?.BorderThickness.Top ?? 0;
        contentHeight -= _selectionBorder?.BorderThickness.Bottom ?? 0;
        contentHeight -= GetHorizontalGridlineHeight();

        if (contentWidth < 0 || contentHeight < 0)
        {
            _contentPresenter.Visibility = Visibility.Collapsed;
        }
        else
        {
            element.MaxWidth = contentWidth;
            element.MaxHeight = contentHeight;
            _contentPresenter.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Measures the cell content unconstrained and grows the owning column's desired width on demand.
    /// Used by the auto-fit gesture for columns that are not auto-sized (and therefore are not measured every pass).
    /// </summary>
    internal void UpdateDesiredWidth()
    {
        if (Column is not null && Row is not null && _contentPresenter is not null && ResolveContentElement() is { } element)
        {
            _contentDesiredWidth = MeasureContentDesiredWidth(element);
            Column.DesiredWidth = Math.Max(Column.DesiredWidth, _contentDesiredWidth);
            InvalidateMeasure(); // Re-apply the content constraint on the next pass.
        }
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        TableView?.NoteCellArrange();

        finalSize = base.ArrangeOverride(finalSize);

        // During a resize-drag preview, manually re-arrange the overlapping template borders wider
        // than the Grid's own column-based sizing would give them (the Grid still thinks this cell is
        // its pre-drag width, since Width itself is left untouched for the whole drag) — this is what
        // lets the generously-premeasured content in BeginResizePreview actually render past the old
        // boundary; Clip then reveals/hides it every frame. Same "arrange a child beyond what the
        // framework gave it" technique already used in TableViewRow.ArrangeOverride for _itemPresenter.
        if (_resizePreviewActive)
        {
            var bordersRect = new Rect(0, 0, _resizePreviewMaxWidth, finalSize.Height);
            _backgroundBorder?.Arrange(bordersRect);
            _selectionBorder?.Arrange(bordersRect);
            _rootBorder?.Arrange(new Rect(0, 0, _resizePreviewWidth, finalSize.Height));
        }

        return finalSize;
    }

    /// <summary>
    /// Begins a live resize-drag preview for this cell: generously (re)measures its content once so
    /// widening can freely reveal more of it, and creates this cell's own <see cref="Clip"/> geometry
    /// and gridline shift transform. These are per-cell instances (not shared across cells — WinUI
    /// throws if the same <see cref="RectangleGeometry"/> is assigned as <see cref="Clip"/> on more
    /// than one element at a time), mutated in place every frame by
    /// <see cref="UpdateResizePreviewClip"/>/<see cref="UpdateGridLineShift"/> — still no Measure/Arrange
    /// per frame, just not a single shared instance across every row.
    /// </summary>
    internal void BeginResizePreview(double maxPreviewWidth)
    {
        _resizePreviewWidth = ActualWidth;

        if (Content is FrameworkElement element)
        {
            if (Column is TableViewTemplateColumn)
            {
#if WINDOWS
                if (element is ContentControl { ContentTemplateRoot: FrameworkElement root })
#else
                if (element.FindDescendant<ContentPresenter>() is { ContentTemplateRoot: FrameworkElement root })
#endif
                    element = root;
                else
                    element = null!;
            }

            if (element is not null)
            {
                element.MaxWidth = maxPreviewWidth;
                element.MaxHeight = double.PositiveInfinity;
                element.Measure(new Size(maxPreviewWidth, double.PositiveInfinity));

                var desiredWidth = element.DesiredSize.Width;
                desiredWidth += element.Margin.Left;
                desiredWidth += element.Margin.Right;
                desiredWidth += Padding.Left;
                desiredWidth += Padding.Right;
                desiredWidth += BorderThickness.Left;
                desiredWidth += BorderThickness.Right;
                desiredWidth += _selectionBorder?.BorderThickness.Left ?? 0;
                desiredWidth += _selectionBorder?.BorderThickness.Right ?? 0;
                desiredWidth += GridLineWidth;

                _resizePreviewWidth = Math.Min(maxPreviewWidth, Math.Max(ActualWidth, desiredWidth));
            }
        }

        _resizePreviewActive = true;
        _resizePreviewMaxWidth = maxPreviewWidth;

        _resizeClipGeometry = new RectangleGeometry { Rect = ComputeClipRect(ActualWidth, ActualHeight) };
        Clip = _resizeClipGeometry;

        if (_v_gridLine is not null)
        {
            _gridLineShiftTransform = new TranslateTransform();
            _v_gridLine.RenderTransform = _gridLineShiftTransform;
        }

        InvalidateArrange();
    }

    /// <summary>
    /// Shifts this cell sideways to visually make room for the column being resized, without any
    /// real layout — creates this cell's own <see cref="TranslateTransform"/>, mutated in place every
    /// frame by <see cref="UpdateDownstreamShift"/>.
    /// </summary>
    internal void ApplyDownstreamShift()
    {
        _downstreamShiftTransform = new TranslateTransform();
        RenderTransform = _downstreamShiftTransform;
    }

    /// <summary>
    /// Updates this resize-preview cell's clip to the given live drag width. No-op if this cell
    /// isn't the one being resized (i.e. <see cref="BeginResizePreview"/> was never called on it).
    /// </summary>
    internal void UpdateResizePreviewClip(double liveWidth, double height)
    {
        if (_resizeClipGeometry is not null)
        {
            _resizeClipGeometry.Rect = ComputeClipRect(liveWidth, height);
        }
    }

    /// <summary>
    /// Shifts this resize-preview cell's own gridline to track the live drag boundary. No-op if this
    /// cell isn't the one being resized.
    /// </summary>
    internal void UpdateGridLineShift(double deltaX)
    {
        if (_gridLineShiftTransform is not null)
        {
            _gridLineShiftTransform.X = deltaX;
        }
    }

    /// <summary>
    /// Updates this downstream cell's shift to the given delta. No-op if <see cref="ApplyDownstreamShift"/>
    /// was never called on this cell.
    /// </summary>
    internal void UpdateDownstreamShift(double deltaX)
    {
        if (_downstreamShiftTransform is not null)
        {
            _downstreamShiftTransform.X = deltaX;
        }
    }

    /// <summary>
    /// Ends a resize-drag preview started by <see cref="BeginResizePreview"/> or
    /// <see cref="ApplyDownstreamShift"/>, reverting this cell to normal layout-driven sizing.
    /// </summary>
    internal void EndResizePreview()
    {
        _resizePreviewActive = false;
        _resizePreviewMaxWidth = 0d;
        Clip = null;
        RenderTransform = null;
        _resizeClipGeometry = null;
        _gridLineShiftTransform = null;
        _downstreamShiftTransform = null;

        if (_v_gridLine is not null)
        {
            _v_gridLine.RenderTransform = null;
        }

        InvalidateMeasure();
        InvalidateArrange();
    }

    /// <summary>
    /// Computes the clip rect that reveals/hides a resize-preview cell's content for a given live
    /// drag width. Pure function — no side effects — so it's directly unit-testable.
    /// </summary>
    internal static Rect ComputeClipRect(double liveWidth, double height)
    {
        return new Rect(0, 0, Math.Max(0, liveWidth), Math.Max(0, height));
    }

    /// <inheritdoc/>
    protected override void OnPointerEntered(PointerRoutedEventArgs e)
    {
        base.OnPointerEntered(e);

        if ((TableView?.SelectionMode is not ListViewSelectionMode.None
           && TableView?.SelectionUnit is not TableViewSelectionUnit.Row)
           || !TableView.IsReadOnly)
        {
            VisualStates.GoToState(this, false, VisualStates.StatePointerOver);
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerExited(PointerRoutedEventArgs e)
    {
        base.OnPointerEntered(e);

        if ((TableView?.SelectionMode is not ListViewSelectionMode.None
            && TableView?.SelectionUnit is not TableViewSelectionUnit.Row)
            || !TableView.IsReadOnly)
        {
            VisualStates.GoToState(this, false, VisualStates.StateNormal);
        }
    }

    /// <inheritdoc/>
    protected override void OnTapped(TappedRoutedEventArgs e)
    {
        base.OnTapped(e);

        if (!TryEndCurrentCellEdit())
        {
            e.Handled = true;
            return;
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!TryEndCurrentCellEdit())
        {
            e.Handled = true;
            return;
        }
    }
    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        base.OnPointerReleased(e);

        if(TableView?.SelectionUnit is not TableViewSelectionUnit.Row)
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Tries to end the current edit operation, if any.
    /// </summary>
    /// <returns>True if an edit operation was successfully ended, or there is no edit operation.
    /// False if the current edit operation can not be ended.</returns>
    private bool TryEndCurrentCellEdit()
    {
        if ((TableView?.IsEditing ?? false) &&
             TableView.CurrentCellSlot != Slot &&
             TableView.CurrentCellSlot.HasValue &&
             TableView.GetCellFromSlot(TableView.CurrentCellSlot.Value) is { } currentCell)
        {
            if (!TableView.EndCellEditing(TableViewEditAction.Commit, currentCell)) return false;

            TableView.SetIsEditing(false);
        }

        return true;
    }

    /// <summary>
    /// Gets the height of the horizontal gridlines/>.
    /// </summary>
    private double GetHorizontalGridlineHeight()
    {
        return TableView?.GridLinesVisibility is TableViewGridLinesVisibility.All or TableViewGridLinesVisibility.Horizontal
            ? TableView.HorizontalGridLinesStrokeThickness : 0d;
    }

    /// <inheritdoc/>
    protected override void OnDoubleTapped(DoubleTappedRoutedEventArgs e)
    {
        var eventArgs = new TableViewCellDoubleTappedEventArgs(Slot, this, Row?.Content);
        TableView?.OnCellDoubleTapped(eventArgs);
        e.Handled = eventArgs.Handled;

        if (e.Handled) return;

        // Double-clicking a tree cell of an expandable node toggles expansion instead of entering edit mode
        // (works in every selection unit, unlike the arrow keys). Leaf nodes fall through to normal editing.
        if (Column is TableViewTreeColumn && TableView is TreeTableView treeTableView
            && treeTableView.ToggleExpandCollapseFromCell(this))
        {
            e.Handled = true;
            return;
        }

        base.OnDoubleTapped(e);

        e.Handled = IsReadOnly || TableView is null || TableView.IsEditing || !Column?.UseSingleElement is not true || BeginCellEditing(e);
    }

    /// <summary>
    /// Initiates editing mode for the current cell, raising the beginning edit event and allowing cancellation.
    /// </summary>
    /// <param name="editingArgs">The event data associated with the editing request. Cannot be null.</param>
    /// <returns>A task that represents the asynchronous operation. The task result is <see langword="true"/> if cell editing was
    /// successfully started; otherwise, <see langword="false"/> if the operation was canceled.</returns>
    internal bool BeginCellEditing(RoutedEventArgs editingArgs)
    {
        EnsureContent(); // Realize deferred content before editing (e.g. UseSingleElement reuses the display element).

        var args = new TableViewBeginningEditEventArgs(this, Row?.Content, Column!, editingArgs);
        TableView?.OnBeginningEdit(args);

        if (!args.Cancel)
        {
            PrepareForEdit(editingArgs);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Prepares the cell for editing.
    /// </summary>
    internal void PrepareForEdit(RoutedEventArgs editingArgs)
    {
        var editingElement = SetEditingElement();
        Content = editingElement;

        if (TableView is not null)
        {
            TableView.SetIsEditing(true);
            TableView.UpdateCornerButtonState();
        }

        if (editingElement is { IsHitTestVisible: true })
        {
            _editingArgs = editingArgs;
            editingElement.Loaded += OnEditingElementLoaded;
        }
    }

    private void OnEditingElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement editingElement)
        {
            editingElement.Loaded -= OnEditingElementLoaded;
            editingElement.Focus(FocusState.Pointer);
            _editingArgs ??= new RoutedEventArgs();

            var args = new TableViewPreparingCellForEditEventArgs(this, Row?.Content, Column!, editingElement, _editingArgs);
            _uneditedValue = Column?.PrepareCellForEdit(this, _editingArgs);
            TableView?.OnPreparingCellForEdit(args);
        }
    }

    /// <summary>
    /// Sets the editing element for the cell.
    /// </summary>
    private FrameworkElement? SetEditingElement()
    {
        if (Column?.UseSingleElement ?? false)
        {
            return Content as FrameworkElement;
        }
        else
        {
            var element = Column?.GenerateEditingElement(this, Row?.Content);

            if (element is not null && Column is TableViewBoundColumn { EditingElementStyle: { } } boundColumn)
            {
                element.Style = boundColumn.EditingElementStyle;
            }

            return element;
        }
    }

    internal void EndEditing(TableViewEditAction editAction)
    {
        Column?.EndCellEditing(this, Row?.Content, editAction, _uneditedValue);
        SetElement();
    }

    /// <summary>
    /// Sets the element for the cell.
    /// </summary>
    internal void SetElement()
    {
        var element = Column?.GenerateElement(this, Row?.Content);

        if (element is not null && Column is TableViewBoundColumn { ElementStyle: { } } boundColumn)
        {
            element.Style = boundColumn.ElementStyle;
        }

        Content = element;

#if !WINDOWS
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(20);
            Focus(FocusState.Pointer);
        });
#endif
        // Note: setting Content already invalidates this cell's measure, so no extra InvalidateMeasure is needed.
    }

    /// <summary>
    /// Refreshes the element for the cell.
    /// </summary>
    internal void RefreshElement()
    {
        Column?.RefreshElement(this, Row?.Content);
    }

    /// <summary>
    /// Refreshes the element against an item the caller already has in hand, so a loop over a row's cells does not
    /// read the row's content once per cell.
    /// </summary>
    internal void RefreshElement(object? dataItem)
    {
        Column?.RefreshElement(this, dataItem);
    }

    /// <summary>
    /// Applies the selection state to the cell.
    /// </summary>
    internal void ApplySelectionState(bool onlyToStateSelected = false)
    {
        var isSelected = IsSelected;

        if (onlyToStateSelected && !isSelected)
        {
            return;
        }

        // Remember what was applied and do nothing when it has not changed. The wholesale apply that runs for
        // every recycled row visits every cell of the row and almost always asks for the state the cell is
        // already in; at eighty columns and fifty rows that was thousands of visual-state transitions, each
        // allocating a params array, per scroll frame. A field compare is the whole saving, and unlike
        // restricting the pass to some subset of the cells it cannot leave a stale Selected visual behind.
        if (_appliedSelectionState == isSelected)
        {
            return;
        }

        _appliedSelectionState = isSelected;

        VisualStates.GoToState(this, false,
            isSelected ? VisualStates.StateSelected : VisualStates.StateUnselected);
    }

    /// <summary>
    /// Applies the current cell state to the cell.
    /// </summary>
    internal async void ApplyCurrentCellState(bool skipFocus = false)
    {
        if (IsCurrent)
        {
            EnsureContent(); // Realize deferred content for the cell becoming current.
        }

        var stateName = IsCurrent ? VisualStates.StateCurrent : VisualStates.StateRegular;
        VisualStates.GoToState(this, false, stateName);

        if (IsCurrent && !skipFocus)
        {
            Focus(FocusState.Pointer);

            var content = Content;
            await Task.Delay(20);

            // Fire-and-forget across a delay: by now the cell may no longer be current (fast keyboard navigation
            // fires this ~30 times a second) or may have been recycled onto another item. Focusing then would
            // steal focus from wherever the user has since moved.
            if (IsCurrent && ReferenceEquals(Content, content) && content is UIElement { IsHitTestVisible: true } element)
            {
                element.Focus(FocusState.Pointer);
            }
        }
    }

    /// <summary>
    /// Updates the element state for the cell.
    /// </summary>
    internal void UpdateElementState()
    {
        Column?.UpdateElementState(this, Row?.Content);
    }

    /// <summary>
    /// Handles changes to the column.
    /// </summary>
    private void OnColumnChanged()
    {
        if (TableView?.IsColumnVirtualizationEnabled is true)
        {
            // Defer content generation until the cell scrolls into the horizontal viewport (see EnsureContent).
            _contentPending = true;
            return;
        }

        if (TableView?.IsEditing == true)
        {
            SetEditingElement();
        }
        else
        {
            SetElement();
        }
    }

    /// <summary>
    /// Generates the cell's content if it was deferred by column virtualization. No-op once realized.
    /// </summary>
    internal bool EnsureContent()
    {
        if (!_contentPending)
        {
            return false;
        }

        _contentPending = false;
        SetElement();
        return true;
    }

    /// <summary>
    /// Drops this cell's content and makes it pending again, so a column the viewport has left far behind stops
    /// costing anything. The inverse of <see cref="EnsureContent"/>.
    /// </summary>
    /// <remarks>
    /// <para>Without this, column virtualization is one-way: content is generated once and kept for the life of the
    /// cell, so after a single sweep across the columns every realized row holds a fully built element for every
    /// column. Collapsing a cell stops it being measured, but it does not stop its bindings — a source-driven
    /// update still runs the binding, the converter and the target write — so a live feed goes on ticking columns
    /// nobody can see, and the saving that virtualization promised quietly disappears.</para>
    /// <para>Only ever called for cells well outside the realized band (see the keep range in
    /// <see cref="WinUI.TableView.TableView.RealizeRowCells(TableViewRow, ValueTuple{int, int}, ValueTuple{int, int})"/>),
    /// never for one in view and never for the cell being edited.</para>
    /// </remarks>
    /// <returns>Whether content was actually released.</returns>
    internal bool ReleaseContent()
    {
        // Visibility is checked as well as the flag, not instead of it: the two can disagree. SetInViewport only
        // writes Visibility while column virtualization is on, so a cell left visible by RealizeAllCells and then
        // caught by virtualization being switched back on reads as out-of-viewport while still being drawn.
        // Dropping its content would blank a cell the user is looking at.
        if (_contentPending || _isInViewport || Visibility is Visibility.Visible || Content is null)
        {
            return false; // nothing realized, or still in view — the realize pass owns it
        }

        if (TableView?.IsEditing is true && TableView.CurrentCellSlot == Slot)
        {
            return false; // dropping the editing element would discard what the user is typing
        }

        // Unpin first: the pin is a local DataContext value on the element we are about to drop, and clearing it
        // here keeps the pinned/unpinned bookkeeping honest if the same element were ever reused.
        PinContentDataContext(pin: false);

        // Setting Content raises OnContentChanged, which resets the desired-width, auto-min-width and pinning
        // state. The resolved-content and constraint caches are keyed on the element reference, so a new element
        // misses them naturally; clearing them here just releases the reference.
        Content = null;
        _contentPending = true;
        _resolvedContentKey = null;
        _resolvedContentElement = null;
        _constrainedElement = null;

        return true;
    }

    /// <summary>
    /// Creates this cell's deferred content now, while it is still outside the viewport, so the scroll that
    /// eventually reveals it has nothing left to generate.
    /// </summary>
    /// <remarks>
    /// The cell stays collapsed, so the new element is neither measured nor drawn; its bindings evaluate once
    /// against the row's current item on attach, and the DataContext is pinned straight away so a recycle
    /// underneath it costs nothing until it actually scrolls into view. Driven by the TableView's idle prefetch
    /// pump, never by layout.
    /// </remarks>
    internal bool PrefetchContent()
    {
        if (_isInViewport || !EnsureContent() || Content is not FrameworkElement element)
        {
            return false; // in view, the realize pass owns it; otherwise nothing was pending
        }

        PinContentDataContext(pin: true);

        // The cell's own template first. A collapsed cell is never measured, so layout never applies it, and until
        // it is applied there is no content presenter for ConstrainContent to work against: the content below was
        // measured unconstrained, and the scroll that revealed the cell then instantiated the cell's template AND
        // measured the content again under the real width — the first-reveal cost this pump exists to move off
        // the scroll path, still on it. Applying it here is the same work a moment earlier, at idle.
        if (ApplyTemplate())
        {
            TableView?.NoteCellTemplatePrefetched();
        }

        // Creation reaches a constructor-built element's cost (a UserControl's InitializeComponent). A templated
        // Control pays instead when its template is applied — normally on first Measure, which does not happen
        // under a collapsed ancestor — so apply it explicitly, then measure once under the constraint the cell will
        // use (ConstrainContent also primes its cache, so the reveal's own pass is a hit). The scroll that reveals
        // it then re-measures an already-built tree under an unchanged constraint.
        (element as Control)?.ApplyTemplate();
        ConstrainContent(element);
        element.Measure(new Size(element.MaxWidth, element.MaxHeight));

        return true;
    }

    /// <summary>
    /// Sets whether this cell's column is currently within the horizontal viewport. When it leaves the viewport the
    /// entire cell is collapsed; a collapsed element's <c>Measure()</c> is a no-op, so its whole template (and content)
    /// is skipped during layout — the dominant cost for many-column grids. It is re-shown and re-measured when the
    /// column scrolls back into the realized band. Only meaningful while
    /// <see cref="WinUI.TableView.TableView.IsColumnVirtualizationEnabled"/> is set. The cell stays in the row's cell
    /// list (it is only hidden), so column-indexed access for selection/editing is unaffected.
    /// </summary>
    internal void SetInViewport(bool value)
    {
        var changed = _isInViewport != value;
        _isInViewport = value;

        // Sync visibility every call (not only on change) so a freshly created off-viewport cell — which starts with
        // _isInViewport == false but Visibility == Visible — is collapsed too.
        if (TableView?.IsColumnVirtualizationEnabled is true)
        {
            var visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (Visibility != visibility)
            {
                Visibility = visibility;
            }

            PinContentDataContext(pin: !value);
        }
        else
        {
            if (Visibility != Visibility.Visible)
            {
                // Virtualization disabled: never leave a cell hidden (RealizeAllCells calls this with true for all cells).
                Visibility = Visibility.Visible;
            }

            PinContentDataContext(pin: false);
        }

        if (changed && value)
        {
            InvalidateMeasure();

            // Coming back into view: re-assert the selection visuals, because a cell can be scrolled past while a
            // selection is made or cleared. Skipped entirely on a grid that has never selected a cell, where there
            // is by definition no state to correct — this runs for every column revealed by every band change, on
            // every realized row.
            if (TableView?.HasOrHadCellSelection is true)
            {
                ApplySelectionState();
            }
        }
    }

    /// <summary>
    /// Freezes (or thaws) the content element's DataContext while the cell is outside the horizontal viewport.
    /// </summary>
    /// <remarks>
    /// Column virtualization collapses out-of-band cells so they are not measured - but their bound content still
    /// inherits the row's DataContext, so every container recycle re-evaluated the bindings of all 80 cells when
    /// only ~26 were visible. That rebind storm during a fast vertical scroll is what starved the live feed.
    /// Assigning the element's own current DataContext as a LOCAL value is a no-op for its bindings and stops
    /// inheritance, so the row can change item underneath it for free; clearing the local value when the cell
    /// comes back into view re-inherits and rebinds exactly once, to the row's current item. Nothing visible ever
    /// shows stale data, because nothing pinned is visible.
    /// </remarks>
    private void PinContentDataContext(bool pin)
    {
        // The cheap checks first. This runs for every cell a band move visits and, since the recycle fix below, for
        // every cell of every recycled row; the binding-expression probe is a crossing into the core and is only
        // needed when a pin or unpin is actually about to happen.
        if (pin == _dataContextPinned)
        {
            return; // already in the requested state
        }

        // Back in view under the SAME item it was pinned to — the common case for prefetched content on the first
        // scroll — the local value is already the right one, and clearing it would only re-run every binding for the
        // same result. Stay pinned. A recycle under a different item is the case that must re-inherit, and it gets
        // here through OnRowItemChanged.
        if (!pin && ReferenceEquals(_pinnedItem, Row?.Content))
        {
            return;
        }

        if (Content is not FrameworkElement element || element.GetBindingExpression(DataContextProperty) is not null)
        {
            return; // no content, or the column binds DataContext itself (the ComboBox column) — a local value would break it
        }

        if (pin)
        {
            // What the element is actually bound to, not the row's item: while a row is held through a fast
            // vertical scroll its cells inherit the previous item from the held panel, and a pin recorded against
            // the row's new item would later pass the "same item, stay pinned" check above while showing the old one.
            var dataContext = element.DataContext;
            _pinnedItem = dataContext;
            element.DataContext = dataContext;
            _dataContextPinned = true;
            return;
        }

        element.ClearValue(DataContextProperty);
        _dataContextPinned = false;
        _pinnedItem = null;
    }

    /// <summary>
    /// Holds this cell's content on whatever it is bound to now, so the recycle about to change the row's item
    /// costs it nothing: the held cells of a row recycled by a fast vertical scroll. The same pin as a prefetched
    /// cell's, undone by <see cref="OnRowItemChanged"/> when the cell is released or the row recycles again. A
    /// column whose element binds its own DataContext cannot be held this way and simply follows the item.
    /// </summary>
    internal void HoldContent() => PinContentDataContext(pin: true);

    /// <summary>
    /// The row this cell belongs to now shows a different item. A cell that is in view must follow it at once.
    /// </summary>
    /// <remarks>
    /// A cell's content is pinned to the item it was built under while the cell is collapsed, so a recycle costs
    /// nothing for columns nobody can see. A prefetched cell that scrolled into view under that same item stayed
    /// pinned, deliberately, since unpinning would re-run its bindings for the same values — and nothing on the
    /// recycle path undid the pin, so the cell went on showing the old item's values after the row moved on. This
    /// is the undo. Collapsed cells stay pinned: they re-inherit when they are next revealed, as before.
    /// </remarks>
    internal void OnRowItemChanged()
    {
        if (_isInViewport && _dataContextPinned)
        {
            PinContentDataContext(pin: false);
        }
    }

    /// <summary>
    /// Ensures grid lines are applied to the cell.
    /// </summary>
    internal void EnsureGridLines()
    {
        if (_v_gridLine is not null && TableView is not null)
        {
            _v_gridLine.Fill = TableView.GridLinesVisibility is TableViewGridLinesVisibility.All or TableViewGridLinesVisibility.Vertical
                               ? TableView.VerticalGridLinesStroke : TableView.TransparentBrush;
            _v_gridLine.Width = TableView.VerticalGridLinesStrokeThickness;
            _v_gridLine.Visibility = TableView.HeaderGridLinesVisibility is TableViewGridLinesVisibility.All or TableViewGridLinesVisibility.Vertical
                                     || TableView.GridLinesVisibility is TableViewGridLinesVisibility.All or TableViewGridLinesVisibility.Vertical
                                     ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Ensures the correct style is applied to the cell.
    /// </summary>
    /// <param name="item">The data item associated with the cell.</param>
    internal void EnsureStyle(object? item)
    {
        EnsureStyle(item, TableView?.ConditionalCellStyles, TableView?.CellStyle);
    }

    /// <summary>
    /// Resolves and applies this cell's style, with the grid-level values supplied by the caller.
    /// </summary>
    /// <remarks>
    /// The grid-level style and conditional-style list are the same for every cell in the row, so a caller looping
    /// over the cells reads them once instead of twice per cell — at eighty columns that is a hundred and sixty
    /// cross-ABI property reads per recycled row that no longer happen. The final assignment is guarded by a
    /// reference compare, because the overwhelmingly common case is writing the same value (usually null) back.
    /// </remarks>
    internal void EnsureStyle(object? item, IList<TableViewConditionalCellStyle>? tableViewStyles, Style? tableViewCellStyle)
    {
        Style? winningStyle = null;

        // Column styles
        var columnStyles = Column?.ConditionalCellStyles;
        if (columnStyles is { Count: > 0 })
        {
            winningStyle = columnStyles.FirstOrDefault(c => c.Predicate?.Invoke(new(Column!, item)) is true)?.Style;
        }

        // Table View styles
        if (winningStyle is null && tableViewStyles is { Count: > 0 })
        {
            winningStyle = tableViewStyles.FirstOrDefault(c => c.Predicate?.Invoke(new(Column!, item)) is true)?.Style;
        }

        // Result style
        var resolved = winningStyle ?? Column?.CellStyle ?? tableViewCellStyle;

        if (!ReferenceEquals(Style, resolved))
        {
            Style = resolved;
        }
    }

    /// <summary>
    /// Gets a value indicating whether the cell is read-only.
    /// </summary>
    public bool IsReadOnly => TableView?.IsReadOnly is true
                              || Column is TableViewTemplateColumn { EditingTemplate: null, EditingTemplateSelector: null } or { IsReadOnly: true };

    /// <summary>
    /// Gets the slot for the cell.
    /// </summary>
    public TableViewCellSlot Slot => new(Row?.Index ?? -1, Index);

    /// <summary>
    /// Gets or sets the index of the cell.
    /// </summary>
    internal int Index { get; set; }

    /// <summary>
    /// Gets a value indicating whether the cell is selected.
    /// </summary>
    public bool IsSelected
    {
        get
        {
            // XXX Eliminate building Slot when selectedCells is empty
            var selectedCells = TableView?.SelectedCells;
            if (selectedCells is { Count: > 0 })
            {
                return selectedCells.Contains(Slot);
            }

            return false;
        }
    }

    /// <summary>
    /// Gets a value indicating whether the cell is the current cell.
    /// </summary>
    public bool IsCurrent => TableView?.CurrentCellSlot == Slot;

    /// <summary>
    /// Gets or sets the column for the cell.
    /// </summary>
    public TableViewColumn? Column
    {
        get;
        internal set
        {
            if (field != value)
            {
                field = value;
                OnColumnChanged();
            }
        }
    }

    /// <summary>
    /// Gets or sets the row for the cell.
    /// </summary>
    public TableViewRow? Row { get; internal set; }

    /// <summary>
    /// Gets or sets the TableView for the cell.
    /// </summary>
    public TableView? TableView { get; internal set; }

    /// <inheritdoc/>
    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return new AutomationPeers.TableViewCellAutomationPeer(this);
    }
}
