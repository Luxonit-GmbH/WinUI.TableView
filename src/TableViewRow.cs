using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;
using WinUI.TableView.Extensions;
using WinUI.TableView.Helpers;

namespace WinUI.TableView;

/// <summary>
/// Represents a row in a TableView.
/// </summary>

#if WINDOWS
[WinRT.GeneratedBindableCustomProperty]
#endif
public partial class TableViewRow : ListViewItem
{
    private const string Selection_Background = "SelectionBackground";
    private const double Selection_IndicatorHeight = 16d;
    private const string Check_Mark = "\uE73E";
    private Thickness _focusVisualMargin = new(1);
    private readonly Thickness _selectionBackgroundMargin = new(4, 2, 4, 2);
    private readonly Thickness _selectionIndicatorMargin = new(4, 0, 0, 0);
    private ListViewItemPresenter? _itemPresenter;
    private Border? _selectionBackground;
    private Border? _selectionIndicator;
    private Border? _multiSelectIndicator;
    private bool _selectionIndicatorResolved;  // the visual-tree search for it has run (its result may be null)
    private bool _selectionBackgroundResolved;
    private bool _ensureCells = true;
    private int _syncedColumnLayoutVersion = -1; // the column-layout version this row's cell widths match
    private Brush? _cellPresenterBackground;
    private Brush? _cellPresenterForeground;
    private int? _cachedIndex;

    /// <summary>
    /// The column band this row's cells are currently flagged for, and the wider range whose content it is still
    /// holding. Remembering them per row is what lets a band change touch only the columns that actually crossed an
    /// edge — typically one or two — instead of re-walking every column of every row, and lets a recycled row whose
    /// band has not moved do no work at all. <c>(-2, -2)</c> means "never applied": walk the row in full.
    /// </summary>
    internal (int First, int Last) AppliedBand { get; set; } = (-2, -2);

    /// <inheritdoc cref="AppliedBand"/>
    internal (int First, int Last) AppliedKeep { get; set; } = (-2, -2);

    /// <summary>
    /// Forces the next realize pass to walk this row's cells in full, after something the per-row memo cannot see
    /// (a column-set change, virtualization being toggled, a cell rebuild).
    /// </summary>
    internal void InvalidateAppliedBand()
    {
        AppliedBand = (-2, -2);
        AppliedKeep = (-2, -2);
    }

    /// <summary>
    /// Initializes a new instance of the TableViewRow class.
    /// </summary>
    public TableViewRow()
    {
        DefaultStyleKey = typeof(TableViewRow);

        SizeChanged += OnSizeChanged;
        Loaded += TableViewRow_Loaded;
#if WINDOWS
        ContextRequested += OnContextRequested;
        RegisterPropertyChangedCallback(IsSelectedProperty, delegate { OnIsSelectedChanged(); });
#endif
        RegisterPropertyChangedCallback(ForegroundProperty, delegate { OnForegroundChanged(); });
        RegisterPropertyChangedCallback(BackgroundProperty, delegate { OnBackgroundChanged(); });
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

        // Select the row before showing the Context Menu, honouring Ctrl/Shift like a left click would. A cell
        // inside this row claims the click first when there is one, so this is the row-header/empty-space path.
        TableView?.ApplyContextRequestSelection(new TableViewCellSlot(Index, -1), IsSelected);

        e.Handled = TableView?.ShowRowContext(this, position) is true;
    }

#if WINDOWS
    /// <summary>
    /// Handles the IsSelected property changed.
    /// </summary>
    private void OnIsSelectedChanged()
    {
        EnsureLayout();
        RowPresenter?.SetRowDetailsVisibility();
    }
#endif

    /// <summary>
    /// Handles the Foreground property changed.
    /// </summary>
    private void OnForegroundChanged()
    {
        _cellPresenterForeground = Foreground;
        EnsureAlternateColors();
    }

    /// <summary>
    /// Handles the Background property changed.
    /// </summary>
    private void OnBackgroundChanged()
    {
        _cellPresenterBackground = Background;
        EnsureAlternateColors();
    }

    /// <summary>
    /// Handles the Loaded event.
    /// </summary>
    private void TableViewRow_Loaded(object sender, RoutedEventArgs e)
    {
        _focusVisualMargin = FocusVisualMargin;

        // No EnsureGridLines() here. WinUI raises Loaded again every time a recycled container is re-attached, so
        // this was a walk over every cell of the row on every vertical scroll step, re-writing values that cannot
        // have changed just because the container came back. The grid lines are set where they can actually change:
        // TableViewRowPresenter.OnApplyTemplate, TableViewCell.OnApplyTemplate, and the grid-line property handler.
        EnsureLayout();
    }

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _cellPresenterBackground = Background;
        _cellPresenterForeground = Foreground;
        _itemPresenter = GetTemplateChild("Root") as ListViewItemPresenter;
        // The template (re)applied — cached visual-tree parts found under _itemPresenter are now stale.
        _selectionIndicator = null;
        _multiSelectIndicator = null;
        _selectionBackground = null;
        _selectionIndicatorResolved = false;
        _selectionBackgroundResolved = false;
#if !WINDOWS
        RowPresenter = GetTemplateChild("RowPresenter") as TableViewRowPresenter;
        _selectionBackground = GetTemplateChild("SelectionBackground") as Border;
#endif
    }

    /// <inheritdoc/>
    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);

        // A recycled container may swap between a banner item and a data item, so re-decide every time.
        RowPresenter?.ApplyBannerPresentation(newContent);

        // EnsureCells also detects a cell set that no longer matches the columns (see its self-healing guard), so a
        // container recycled across a column-set change rebuilds here instead of rendering the wrong columns.
        // Freshly built cells need no refresh pass, hence the else.
        if (!EnsureCells())
        {
            // Nothing about the columns has moved since this row last synced, so every cell's width already
            // matches its column and there is nothing to refresh. Skipping the walk is the difference between two
            // projected property reads per column per recycled row and a single integer compare — and a vertical
            // scrollbar throw recycles every realized container on every frame.
            var columnsCollection = TableView?.Columns as TableViewColumnsCollection;
            var layoutVersion = columnsCollection?.ColumnLayoutVersion ?? -1;
            var widthsInSync = columnsCollection is not null && _syncedColumnLayoutVersion == layoutVersion;

            // With widths already in sync, no content-sized column to re-measure and no column that overrides
            // RefreshElement, the loop below has nothing left to do for any cell — skip it outright.
            var skipCellPass = widthsInSync
                               && TableView?.HasAnyAutoWidthColumn is false
                               && TableView?.HasAnyRefreshOnRecycleColumn is false;

            foreach (var cell in skipCellPass ? [] : Cells)
            {
                // The data item changed; the cached auto-size width no longer reflects this cell's content.
                cell.InvalidateDesiredWidth();

                // Defensively resync width on reuse — a recycled container can otherwise keep a
                // stale Width if it missed a Column.ActualWidth change while off-screen (e.g. an
                // auto-width recalculation triggered by a sort), leaving cells misaligned with headers.
                if (!widthsInSync && cell.Column is not null && !cell.Width.Equals(cell.Column.ActualWidth))
                {
                    cell.Width = cell.Column.ActualWidth; // a DP write invalidates layout; a matching read costs nothing
                }

                // Only for columns that actually do something in RefreshElement. For a bound column the element
                // follows the DataContext by itself, so the call was a property read plus a virtual dispatch, per
                // cell, per recycled row, to reach an empty method body.
                if (cell.Column?.NeedsRefreshOnRecycle is true)
                {
                    cell.RefreshElement(newContent);
                }
            }

            _syncedColumnLayoutVersion = layoutVersion;

            TableView?.RealizeRowCells(this); // Ensure visible columns are realized for the recycled row.
        }

        RowPresenter?.InvalidateMeasure(); // The cells presenter does not measure every time.

        // On recycle only THIS row's index (and therefore its alternate color) changed — re-color just this row,
        // synchronously and O(1), instead of enqueuing a full-grid re-color pass on every recycled container.
        EnsureAlternateColors();
    }

    /// <inheritdoc/>
    protected override void OnDoubleTapped(DoubleTappedRoutedEventArgs e)
    {
        var eventArgs = new TableViewRowDoubleTappedEventArgs(Index, this, Content);
        TableView?.OnRowDoubleTapped(eventArgs);
        e.Handled = eventArgs.Handled;

        base.OnDoubleTapped(e);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        finalSize = base.ArrangeOverride(finalSize);

        var cornerRadius = _itemPresenter?.CornerRadius ?? new();
        var left = Math.Max(cornerRadius.TopLeft, cornerRadius.BottomLeft);

        _itemPresenter?.Arrange(new Rect(-left, 0, _itemPresenter.ActualWidth + left, _itemPresenter.ActualHeight));

        // Position feeds drag-selection hit testing only, and both of its readers already refresh it on demand, so
        // there is nothing to keep warm here. It is a TransformToVisual — a visual-tree ancestor walk plus matrix
        // composition across the ABI — and this arrange runs for every realized row on every layout pass, which a
        // vertical scroll and a horizontal reveal burst both produce continuously. Refresh it only while a drag is
        // actually reading it.
        if (TableView is { IsDragSelecting: true, IsColumnResizing: false })
        {
            UpdatePosition();
        }

        return finalSize;
    }

    /// <summary>
    /// Updates the position of the row relative to the TableView.
    /// </summary>
    internal void UpdatePosition()
    {
        if (TableView is null) return;

        try
        {
            Position = TransformToVisual(TableView.DragRectangleCanvas).TransformPoint(default);
        }
        catch (Exception ex)
        {
            TableViewTrace.Write($"UpdatePosition failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets or sets the position of the row relative to the TableView.
    /// </summary>
    internal Point Position { get; set; }

    /// <summary>
    /// Marks the row's cells as stale and rebuilds them immediately when the row is already templated. Used by
    /// <see cref="WinUI.TableView.TableView.InvalidateColumns"/> after the column set is replaced.
    /// </summary>
    internal void InvalidateCells()
    {
        _ensureCells = true;
        EnsureCells();
    }

    /// <summary>
    /// Ensures cells are created for the row, rebuilding them when they no longer match the visible columns.
    /// </summary>
    /// <returns>Whether the cells were (re)built by this call.</returns>
    internal bool EnsureCells()
    {
        if (TableView is null)
        {
            return false;
        }

        // Self-healing guard: rebuild whenever the realized cells no longer match the visible column set, even if
        // no flag was raised (a column-set swap can interleave with container recycling in ways that lose events).
        if (RowPresenter is not null && !_ensureCells && Cells.Count != TableView.Columns.VisibleColumns.Count)
        {
            _ensureCells = true;
        }

        if (RowPresenter is not null && _ensureCells)
        {
            RowPresenter.ClearCells();

            // Clear the flag BEFORE adding: AddCells re-raises it when it cannot build (no presenter), and must not
            // be undone by this reset.
            _ensureCells = false;
            AddCells(TableView.Columns.VisibleColumns);

            // These are brand-new cells, so whatever band the row was flagged for describes nothing any more.
            InvalidateAppliedBand();

            TableView.RealizeRowCells(this); // No-op unless column virtualization is enabled.
            return true;
        }

        return false;
    }

    /// <summary>
    /// Handles the SizeChanged event.
    /// </summary>
    private async void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (TableView?.CurrentCellSlot?.Row == Index)
        {
            _ = await TableView.ScrollCellIntoView(TableView.CurrentCellSlot.Value);
        }
    }

    /// <summary>
    /// Handles the collection changed event for the columns.
    /// </summary>
    private void OnColumnsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when (e.NewItems?.OfType<TableViewColumn>() is IEnumerable<TableViewColumn> newItems):
                AddCells(newItems.Where(x => x.Visibility == Visibility.Visible));
                break;
            case NotifyCollectionChangedAction.Remove when (e.OldItems?.OfType<TableViewColumn>() is IEnumerable<TableViewColumn> oldItems):
                RemoveCells(oldItems);
                break;
            case NotifyCollectionChangedAction.Move when (e.NewItems?.Count > 0):
                RowPresenter?.MoveCells(e.NewItems.OfType<TableViewColumn>().First(), e.NewStartingIndex);
                break;
            case NotifyCollectionChangedAction.Reset:
                // Reset means "re-sync from the current columns": rebuild this row's cells, not just clear them.
                // The flag is set UNCONDITIONALLY — a row without a presenter (not templated yet, or waiting in the
                // recycle queue) must still rebuild when it gets one, otherwise it keeps the old column set forever.
                _ensureCells = true;
                EnsureCells();
                break;
        }
    }

    /// <summary>
    /// Handles the property changed event for a column.
    /// </summary>
    private void OnColumnPropertyChanged(object? sender, TableViewColumnPropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TableViewColumn.Visibility) when e.Column.Visibility == Visibility.Visible:
                AddCells([e.Column]);
                break;
            case nameof(TableViewColumn.Visibility):
                RemoveCells([e.Column]);
                break;
            case nameof(TableViewColumn.Order) or nameof(TableViewColumn.IsFrozen) when
                e.Column.Visibility is Visibility.Visible:
                RemoveCells([e.Column]);
                AddCells([e.Column]);
                break;
            case nameof(TableViewColumn.ActualWidth):
                {
                    if (RowPresenter?.GetCellForColumn(e.Column) is { } cell && cell.Width != e.Column.ActualWidth)
                    {
                        cell.Width = e.Column.ActualWidth;
                    }
                    break;
                }
            case nameof(TableViewColumn.IsReadOnly):
                UpdateCellsState();
                break;
            case nameof(TableViewColumn.CellStyle):
                EnsureCellsStyle(e.Column);
                break;
            case nameof(TableViewBoundColumn.ElementStyle):
                EnsureElementStyle(e.Column);
                break;
            case nameof(TableViewBoundColumn.EditingElementStyle):
                EnsureEditingElementStyle(e.Column);
                break;
        }
    }

    /// <summary>
    /// Removes cells for the specified columns.
    /// </summary>
    private void RemoveCells(IEnumerable<TableViewColumn> columns)
    {
        if (RowPresenter is null)
        {
            // No presenter to update yet: remember that this row's cells no longer match the column set so it
            // rebuilds from scratch once it is templated (silently skipping would strand the stale cells).
            _ensureCells = true;
            return;
        }

        foreach (var column in columns)
        {
            var cell = RowPresenter.GetCellForColumn(column);
            if (cell is not null)
            {
                RowPresenter.RemoveCell(cell);
            }
        }
    }

    /// <summary>
    /// Adds cells for the specified columns.
    /// </summary>
    private void AddCells(IEnumerable<TableViewColumn> columns)
    {
        if (RowPresenter is null || TableView is null)
        {
            // See RemoveCells: mark for a full rebuild instead of dropping the change on the floor.
            _ensureCells = true;
            return;
        }

        {
            foreach (var column in columns)
            {
                var cell = new TableViewCell
                {
                    Row = this,
                    // TableView must be assigned before Column so the Column setter can observe the
                    // virtualization setting and defer content generation accordingly.
                    TableView = TableView,
                    Column = column,
                    Index = TableView.Columns.VisibleColumnIndex(column),
                    Width = column.ActualWidth,
                    // Set heights directly instead of per-cell bindings (these values rarely change and are
                    // re-applied via ApplyCellHeights on change). Avoids 3 bindings per cell.
                    Height = TableView.RowHeight,
                    MaxHeight = TableView.RowMaxHeight,
                    MinHeight = TableView.RowMinHeight
                };

                RowPresenter.InsertCell(cell);
            }
        }
    }

    /// <summary>
    /// Applies the TableView's row height values to all cells. Called when cells are created and whenever
    /// <see cref="TableView.RowHeight"/>, <see cref="TableView.RowMinHeight"/> or <see cref="TableView.RowMaxHeight"/> change.
    /// </summary>
    internal void ApplyCellHeights()
    {
        if (TableView is null)
        {
            return;
        }

        foreach (var cell in Cells)
        {
            cell.Height = TableView.RowHeight;
            cell.MaxHeight = TableView.RowMaxHeight;
            cell.MinHeight = TableView.RowMinHeight;
        }
    }

    /// <summary>
    /// Handles the TableView changing event.
    /// </summary>
    private void OnTableViewChanging()
    {
        if (TableView is not null)
        {
            TableView.IsReadOnlyChanged -= OnTableViewIsReadOnlyChanged;

            if (TableView.Columns is not null)
            {
                TableView.Columns.CollectionChanged -= OnColumnsCollectionChanged;
                TableView.Columns.ColumnPropertyChanged -= OnColumnPropertyChanged;
            }
        }
    }

    /// <summary>
    /// Handles the TableView changed event.
    /// </summary>
    private void OnTableViewChanged()
    {
        if (TableView is not null)
        {
            TableView.IsReadOnlyChanged += OnTableViewIsReadOnlyChanged;

            if (TableView.Columns is not null)
            {
                TableView.Columns.CollectionChanged += OnColumnsCollectionChanged;
                TableView.Columns.ColumnPropertyChanged += OnColumnPropertyChanged;
            }
        }
    }

    /// <summary>
    /// Handles the IsReadOnly property changed event for the TableView.
    /// </summary>
    private void OnTableViewIsReadOnlyChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateCellsState();
    }

    /// <summary>
    /// Updates the state of the cells.
    /// </summary>
    private void UpdateCellsState()
    {
        foreach (var cell in Cells)
        {
            cell.UpdateElementState();
        }
    }

    private void EnsureElementStyle(TableViewColumn column)
    {
        foreach (var cell in Cells)
        {
            if (cell.Column == column
                && cell.Content is FrameworkElement element
                && cell.Column is TableViewBoundColumn boundColumn
                && (TableView?.IsEditing is false || TableView?.CurrentCellSlot != cell.Slot))
            {
                element.Style = boundColumn.ElementStyle;
            }
        }
    }

    private void EnsureEditingElementStyle(TableViewColumn column)
    {
        if (TableView?.IsEditing is true
            && TableView.CurrentCellSlot is not null
            && column is TableViewBoundColumn boundColumn
            && TableView.GetCellFromSlot(TableView.CurrentCellSlot.Value) is { } cell
            && cell.Column == column
            && cell.Content is FrameworkElement element)
        {
            element.Style = boundColumn.EditingElementStyle;
        }
    }

    /// <summary>
    /// Ensures the cells style is applied.
    /// </summary>
    internal void EnsureCellsStyle(TableViewColumn? column = null, object? dataItem = null)
    {
        // One column's style changed: go straight to its cell instead of walking every cell to find it.
        if (column is not null)
        {
            RowPresenter?.GetCellForColumn(column)?.EnsureStyle(dataItem ?? Content);
            return;
        }

        // Nothing anywhere configures a cell style, so every cell would resolve to null and write null over null.
        if (TableView?.HasAnyCellStyling is false)
        {
            return;
        }

        // Grid-level values are the same for every cell; read them once for the whole row.
        var item = dataItem ?? Content;
        var tableViewStyles = TableView?.ConditionalCellStyles;
        var tableViewCellStyle = TableView?.CellStyle;

        foreach (var cell in Cells)
        {
            cell.EnsureStyle(item, tableViewStyles, tableViewCellStyle);
        }
    }

    /// <summary>
    /// Applies the current cell state to the specified slot.
    /// </summary>
    internal void ApplyCurrentCellState(TableViewCellSlot slot)
    {
        if (slot.Column < 0 || slot.Column >= Cells.Count)
        {
            return;
        }

        // Only the row that actually owns the current slot may take focus. Without this check every recycled row
        // ran the focusing path for the cell sitting at the current slot's column index, so scrolling with a
        // current cell set fired a focus call (twice, across a delay) per recycled row — mid-scroll.
        var isCurrentRow = slot.Row == Index;
        Cells[slot.Column].ApplyCurrentCellState(skipFocus: !isCurrentRow);
    }

    /// <summary>
    /// Applies the selection state to the cells.
    /// </summary>
    internal void ApplyCellsSelectionState(bool onlyToStateSelected = false)
    {
        foreach (var cell in Cells)
        {
            cell.ApplySelectionState(onlyToStateSelected);
        }
    }

    /// <summary>
    /// Ensures the layout of the row.
    /// </summary>
    internal void EnsureLayout()
    {
        var cornerRadius = _itemPresenter?.CornerRadius ?? new();
        var left = Math.Max(cornerRadius.TopLeft, cornerRadius.BottomLeft) / 2;
        var detailsHeight = RowPresenter?.GetDetailsContentHeight() ?? 0d;
#if WINDOWS
        // These template parts are stable for the lifetime of the container, so find them once and cache them
        // (reset in OnApplyTemplate) instead of walking the visual tree on every EnsureLayout call.
        // `??=` is not a cache when the answer can legitimately be null: a null result was re-walked on every call,
        // and this walk spans the whole row subtree — every cell and its template. Resolve once, remember that we
        // did, and reset the flag wherever the cached parts are reset (OnApplyTemplate).
        if (!_selectionIndicatorResolved)
        {
            _selectionIndicatorResolved = true;
            _selectionIndicator ??= _itemPresenter?.FindDescendants()
                                                   .OfType<Border>()
                                                   .FirstOrDefault(x => x is { Width: 3 });
        }

        var cellsHeight = ActualHeight - detailsHeight;
        var selectionIndicatorHeight = Math.Max(Selection_IndicatorHeight, cellsHeight - 40);

        if (_selectionIndicator is not null)
        {
            _selectionIndicator.MaxHeight = selectionIndicatorHeight;
            _selectionIndicator.Margin = new Thickness(
                _selectionIndicatorMargin.Left + left,
                _selectionIndicatorMargin.Top,
                _selectionIndicatorMargin.Right,
                _selectionIndicatorMargin.Bottom);
        }

        var selectionIndicator = _selectionIndicator;

        if (TableView is ListView { SelectionMode: ListViewSelectionMode.Multiple })
        {
            _multiSelectIndicator ??= this.FindDescendant<FontIcon>(x => x.Glyph == Check_Mark)?.Parent as Border;
            selectionIndicator = _multiSelectIndicator;
        }

        if (!_selectionBackgroundResolved)
        {
            _selectionBackgroundResolved = true;
            _selectionBackground ??= _itemPresenter?.FindDescendants()
                                                    .OfType<Border>()
                                                    .FirstOrDefault(x => x.Name is not Selection_Background && x.Margin == _selectionBackgroundMargin);
        }

        FocusVisualMargin = new Thickness(
            _focusVisualMargin.Left + left,
            _focusVisualMargin.Top,
            _focusVisualMargin.Right,
            _focusVisualMargin.Bottom + GetHorizontalGridlineHeight());

        EnsureSelectionIndicatorPosition(detailsHeight, selectionIndicator);
#endif
        if (_selectionBackground is not null)
        {
            _selectionBackground.Name = Selection_Background;
            _selectionBackground.Margin = new Thickness(
                _selectionBackgroundMargin.Left + left,
                _selectionBackgroundMargin.Top,
                _selectionBackgroundMargin.Right,
                _selectionBackgroundMargin.Bottom + GetHorizontalGridlineHeight() + detailsHeight);
        }
    }

    /// <summary>
    /// Ensures the position of the selection indicator.
    /// </summary>
    private async void EnsureSelectionIndicatorPosition(double detailsHeight, Border? selectionIndicator)
    {
        // Check before yielding, not after. Awaiting first posts a dispatcher continuation for every row on every
        // recycle, including the overwhelming majority that have no indicator and nothing to move — a steady drip
        // of work items onto the same thread the scroll is running on.
        if (selectionIndicator is null)
        {
            return;
        }

        await Task.Yield(); // let the animations and visual state changes complete

        if (selectionIndicator is not null)
        {
            // Assign a TranslateTransform for animation
            var translateTransform = new TranslateTransform();
            selectionIndicator.RenderTransform = translateTransform;

            var toValue = RowPresenter?.IsDetailsPanelVisible ?? false ? Math.Round(-detailsHeight / 2) : 0; // move up or down

            var animation = new DoubleAnimation
            {
                To = toValue,
                Duration = new Duration(TimeSpan.Zero)
            };

            var storyboard = new Storyboard();
            Storyboard.SetTarget(animation, translateTransform);
            Storyboard.SetTargetProperty(animation, "Y"); // vertical movement
            storyboard.Children.Add(animation);

            storyboard.Begin();
        }
    }

    /// <summary>
    /// Ensures alternate colors are applied to the row.
    /// </summary>
    internal void EnsureAlternateColors()
    {
        if (TableView is null || RowPresenter is null) return;

        var alternateRowBackground = TableView.AlternateRowBackground;
        var alternateRowForeground = TableView.AlternateRowForeground;

        if (alternateRowBackground == null && alternateRowForeground == null)
        {
            RowPresenter.Background = _cellPresenterBackground;
            RowPresenter.Foreground = _cellPresenterForeground;
        }
        else
        {
            // Should alternate, heavy index lookup
            var alternate = Index % 2 == 1;

            RowPresenter.Background =
                alternate && alternateRowBackground is not null ? alternateRowBackground : _cellPresenterBackground;

            RowPresenter.Foreground =
                alternate && alternateRowForeground is not null ? alternateRowForeground : _cellPresenterForeground;
        }
    }

    internal void UpdateSelectCheckMarkOpacity()
    {
        // Reuse the cached multi-select indicator (the checkmark's parent border) instead of walking the tree
        // on every editing toggle for every realized row.
        _multiSelectIndicator ??= this.FindDescendant<FontIcon>(x => x.Glyph == Check_Mark)?.Parent as Border;

        if (_multiSelectIndicator is { } border)
        {
            border.Opacity = TableView?.IsEditing is true ? 0.3 : 1;
        }
    }

    /// <summary>
    /// Gets the height of the horizontal gridlines.
    /// </summary>
    private double GetHorizontalGridlineHeight()
    {
        return TableView?.GridLinesVisibility is TableViewGridLinesVisibility.All or TableViewGridLinesVisibility.Horizontal
            ? TableView.HorizontalGridLinesStrokeThickness : 0d;
    }

    /// <summary>
    /// Gets the list of cells in the row.
    /// </summary>
    public IReadOnlyList<TableViewCell> Cells => RowPresenter?.Cells ?? [];

    /// <summary>
    /// Gets the index of the row. Cached to avoid repeated container lookups; invalidated on (re)binding and on
    /// collection changes via <see cref="InvalidateIndex"/>.
    /// </summary>
    public int Index
    {
        get
        {
            if (_cachedIndex is { } cached)
            {
                return cached;
            }

            var index = TableView?.IndexFromContainer(this) ?? -1;
            if (index >= 0)
            {
                _cachedIndex = index;
            }

            return index;
        }
    }

    /// <summary>
    /// Invalidates the cached <see cref="Index"/> so it is recomputed on next access.
    /// </summary>
    internal void InvalidateIndex()
    {
        _cachedIndex = null;
    }

    /// <summary>
    /// Gets or sets the TableView associated with the row.
    /// </summary>
    public TableView? TableView
    {
        get;
        internal set
        {
            if (field != value)
            {
                OnTableViewChanging();
                field = value;
                OnTableViewChanged();
            }
        }
    }

    /// <inheritdoc/>
    public TableViewRowPresenter? RowPresenter
#if WINDOWS
       => ContentTemplateRoot as TableViewRowPresenter;
#else
    { get; private set; }
#endif

    /// <inheritdoc/>
    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return new AutomationPeers.TableViewRowAutomationPeer(this);
    }
}
