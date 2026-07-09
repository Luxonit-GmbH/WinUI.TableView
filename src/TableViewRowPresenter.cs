using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;
using WinUI.TableView.Extensions;

namespace WinUI.TableView;

/// <summary>
/// Represents a control that presents visuals for the <see cref="WinUI.TableView.TableViewRow"/>.
/// </summary>
[TemplateVisualState(Name = VisualStates.StateDetailsVisible, GroupName = VisualStates.GroupRowDetails)]
[TemplateVisualState(Name = VisualStates.StateDetailsCollapsed, GroupName = VisualStates.GroupRowDetails)]
[TemplateVisualState(Name = VisualStates.StateDetailsButtonVisible, GroupName = VisualStates.GroupRowDetailsButton)]
[TemplateVisualState(Name = VisualStates.StateDetailsButtonCollapsed, GroupName = VisualStates.GroupRowDetailsButton)]
public partial class TableViewRowPresenter : Control
{
    private TableViewRowHeader? _rowHeader;
    private Panel? _rootPanel;
    private Panel? _scrollableCellsPanel;
    private StackPanel? _frozenCellsPanel;
    private readonly List<TableViewCell> _cellsList = [];
    private readonly Dictionary<TableViewColumn, TableViewCell> _cellsByColumn = [];
    private Rectangle? _v_gridLine;
    private Rectangle? _h_gridLine;
    private Panel? _detailsPanel;
    private ContentPresenter? _detailsPresenter;
    private ToggleButton? _detailsToggleButton;
    private ListViewItemPresenter? _itemPresenter;
    private long? _detailsPanelVisibilityCallbackToken;
    private RectangleGeometry? _scrollableCellsClip;
    private RectangleGeometry? _detailsClip;
    private TranslateTransform? _scrollableCellsTransform;
    private TranslateTransform? _detailsTransform;

    /// <summary>
    /// Initializes a new instance of the <see cref="TableViewRowPresenter"/> class.
    /// </summary>
    public TableViewRowPresenter()
    {
        DefaultStyleKey = typeof(TableViewRowPresenter);
    }

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _detailsToggleButton?.Tapped -= OnDetailsToggleButtonTapped;

        _detailsPanel?.SizeChanged -= OnDetailsPanelSizeChanged;

        if (_detailsPanelVisibilityCallbackToken is long token)
        {
            _detailsPanel?.UnregisterPropertyChangedCallback(VisibilityProperty, token);
            _detailsPanelVisibilityCallbackToken = null;
        }

        _rowHeader = GetTemplateChild("RowHeader") as TableViewRowHeader;
        _rootPanel = GetTemplateChild("RootPanel") as Panel;
        _scrollableCellsPanel = GetTemplateChild("ScrollableCellsPanel") as Panel;
        _frozenCellsPanel = GetTemplateChild("FrozenCellsPanel") as StackPanel;
        _cellsList.Clear(); // Template (re)applied: the new panels start empty.
        _scrollableCellsTransform = null; // RenderTransform is (re)attached to the new panel in ApplyHorizontalScroll.
        _detailsTransform = null;
        _v_gridLine = GetTemplateChild("VerticalGridLine") as Rectangle;
        _h_gridLine = GetTemplateChild("HorizontalGridLine") as Rectangle;
        _detailsPanel = GetTemplateChild("DetailsPanel") as Panel;
        _detailsPresenter = GetTemplateChild("DetailsPresenter") as ContentPresenter;
        _detailsToggleButton = GetTemplateChild("DetailsToggleButton") as ToggleButton;

        _itemPresenter = this.FindAscendant<ListViewItemPresenter>();
        TableViewRow = this.FindAscendant<TableViewRow>();
        TableView = TableViewRow?.TableView;
        _rowHeader?.TableView = TableView;
        _rowHeader?.TableViewRow = TableViewRow;

        _detailsToggleButton?.Tapped += OnDetailsToggleButtonTapped;

        if (_detailsPanel is not null)
        {
            _detailsPanel.SizeChanged += OnDetailsPanelSizeChanged;
            _detailsPanelVisibilityCallbackToken =
                _detailsPanel.RegisterPropertyChangedCallback(VisibilityProperty, OnDetailsPanelVisibilityChanged);
        }

        TableViewRow?.EnsureCells();
        EnsureGridLines();
        SetRowHeaderBindings();
        SetRowHeaderVisibility();
        SetRowHeaderTemplate();
        SetRowHeaderWidth();
        SetRowDetailsVisibility();
        SetRowDetailsTemplate();
    }

    /// <summary>
    /// Handles size changes in the row details panel.
    /// </summary>
    private void OnDetailsPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        TableViewRow?.EnsureLayout();
    }

    /// <summary>
    /// Handles visibility changes in the row details panel.
    /// </summary>
    private void OnDetailsPanelVisibilityChanged(DependencyObject sender, DependencyProperty dp)
    {
        TableViewRow?.EnsureLayout();
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        _rowHeader?.InvalidateMeasure(); // The row header does not measure every time.
        return base.MeasureOverride(availableSize);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        finalSize = base.ArrangeOverride(finalSize);

        if (TableView is not null)
        {
            var cornerRadius = _itemPresenter?.CornerRadius ?? new CornerRadius(0);
            var isMultiSelection = TableView is ListView { SelectionMode: ListViewSelectionMode.Multiple };
            var left = isMultiSelection ? 44 : Math.Max(cornerRadius.TopLeft, cornerRadius.BottomLeft);

            _rootPanel?.Arrange(new(left, 0, Math.Max(0, _rootPanel.ActualWidth), _rootPanel.ActualHeight));

            // Arrange the scrollable panels at their un-scrolled positions; the horizontal scroll offset is applied
            // via RenderTransform in ApplyHorizontalScroll so that scrolling does not re-run a layout pass.
            if (_detailsPanel?.Visibility is Visibility.Visible && _v_gridLine is not null)
            {
                var x = _v_gridLine.ActualOffset.X + _v_gridLine.ActualWidth;
                var y = _scrollableCellsPanel?.ActualHeight ?? _v_gridLine.ActualOffset.Y;
                _detailsPanel.Arrange(new(x, y, _detailsPanel.ActualWidth, _detailsPanel.ActualHeight));
            }

            if (_scrollableCellsPanel?.ActualWidth > 0 && _frozenCellsPanel is not null)
            {
                var frozenRight = _frozenCellsPanel.ActualOffset.X + _frozenCellsPanel.ActualWidth;
                _scrollableCellsPanel.Arrange(new(frozenRight, 0, _scrollableCellsPanel.ActualWidth, _scrollableCellsPanel.ActualHeight));
            }

            ApplyHorizontalScroll();

            // CellsHorizontalOffset is uniform across rows, so only the first row to arrange in a given layout
            // pass computes it (the TransformToVisual is otherwise repeated for every realized row).
            if (_v_gridLine is not null && TableView.TryClaimCellsOffsetUpdate())
            {
                var transform = _v_gridLine.TransformToVisual(this);
                var relativePosition = transform.TransformPoint(new Point(0, 0));
                var offset = _v_gridLine.Visibility is Visibility.Visible ? relativePosition.X : 0d;
                offset -= Math.Max(cornerRadius.TopLeft, cornerRadius.BottomLeft);

                TableView.SetValue(TableView.CellsHorizontalOffsetProperty, Math.Max(0, offset));
            }
        }

        return finalSize;
    }

    /// <summary>
    /// Applies the current horizontal scroll offset to the scrollable cells (and row-details) panel via a
    /// RenderTransform plus a clip, instead of re-arranging the row. Called from <see cref="ArrangeOverride"/> and
    /// directly on HorizontalOffset changes, so horizontal scrolling does not trigger a layout pass per row.
    /// </summary>
    /// <param name="useCachedClip">
    /// On the per-tick horizontal-scroll path the clip rect is identical for every (uniform) row, so the TableView
    /// computes it once and rows reuse that value here — skipping a per-row panel size read + rect rebuild. The
    /// arrange / column-resize / auto-row-height paths pass <see langword="false"/> to compute from this panel.
    /// </param>
    internal void ApplyHorizontalScroll(bool useCachedClip = false)
    {
        if (TableView is null)
        {
            return;
        }

        var h = TableView.HorizontalOffset;

        if (_scrollableCellsPanel is not null && _frozenCellsPanel is not null)
        {
            if (_scrollableCellsTransform is null)
            {
                _scrollableCellsTransform = new TranslateTransform();
                _scrollableCellsPanel.RenderTransform = _scrollableCellsTransform;
            }

            _scrollableCellsTransform.X = -h;

            if (h <= 0)
            {
                _scrollableCellsPanel.Clip = null;
            }
            else
            {
                _scrollableCellsClip ??= new RectangleGeometry();
                _scrollableCellsClip.Rect = useCachedClip && TableView.CellsClipRect is { } cached
                    ? cached
                    : new(h, 0, Math.Max(0, _scrollableCellsPanel.ActualWidth - h), _scrollableCellsPanel.ActualHeight);
                _scrollableCellsPanel.Clip = _scrollableCellsClip;
            }
        }

        if (_detailsPanel?.Visibility is Visibility.Visible)
        {
            var frozen = TableView.AreRowDetailsFrozen;

            if (_detailsTransform is null)
            {
                _detailsTransform = new TranslateTransform();
                _detailsPanel.RenderTransform = _detailsTransform;
            }

            _detailsTransform.X = frozen ? 0 : -h;

            if (frozen || h <= 0)
            {
                _detailsPanel.Clip = null;
            }
            else
            {
                _detailsClip ??= new RectangleGeometry();
                _detailsClip.Rect = new(h, 0, Math.Max(0, _detailsPanel.ActualWidth - h), _detailsPanel.ActualHeight);
                _detailsPanel.Clip = _detailsClip;
            }
        }
    }

    /// <summary>
    /// Sets the DataTemplate for the row header.
    /// </summary>
    internal void SetRowHeaderTemplate()
    {
        if (_rowHeader is not null && TableView is not null)
        {
            _rowHeader.ContentTemplate =
                TableView.RowHeaderTemplateSelector?.SelectTemplate(TableViewRow?.Content)
                ?? TableView.RowHeaderTemplate;
        }

        SetRowHeaderVisibility();
    }

    /// <summary>
    /// Sets the visibility of the row details based on the <see cref="TableView.RowDetailsVisibilityMode"/>.
    /// </summary>
    internal void SetRowDetailsVisibility()
    {
        EnsureGridLines();

        var mode = TableView?.RowDetailsVisibilityMode;
        var hasTemplate = TableView?.RowDetailsTemplate is not null || TableView?.RowDetailsTemplateSelector is not null;

        if (!hasTemplate)
        {
            VisualStates.GoToState(this, false, VisualStates.StateDetailsCollapsed);
            VisualStates.GoToState(this, false, VisualStates.StateDetailsButtonCollapsed);
        }
        else if (mode is TableViewRowDetailsVisibilityMode.Visible)
        {
            VisualStates.GoToState(this, false, VisualStates.StateDetailsVisible);
            VisualStates.GoToState(this, false, VisualStates.StateDetailsButtonCollapsed);
        }
        else if (mode is TableViewRowDetailsVisibilityMode.VisibleWhenSelected)
        {
            var state = (TableViewRow?.IsSelected ?? false) ? VisualStates.StateDetailsVisible : VisualStates.StateDetailsCollapsed;
            VisualStates.GoToState(this, false, state);
            VisualStates.GoToState(this, false, VisualStates.StateDetailsButtonCollapsed);
        }
        else if (mode is TableViewRowDetailsVisibilityMode.VisibleWhenExpanded)
        {
            VisualStates.GoToState(this, false, VisualStates.StateDetailsButtonVisible);
        }
        else
        {
            VisualStates.GoToState(this, false, VisualStates.StateDetailsCollapsed);
            VisualStates.GoToState(this, false, VisualStates.StateDetailsButtonCollapsed);
        }
    }

    /// <summary>
    /// Handles the Tapped event of the details toggle button.
    /// </summary>
    private void OnDetailsToggleButtonTapped(object sender, TappedRoutedEventArgs e)
    {
        ToggleDetailsPane(TableViewRow?.Content, _detailsToggleButton!.IsChecked ?? false);
    }

    /// <summary>
    /// Toggles the visibility of the details pane.
    /// </summary>
    private void ToggleDetailsPane(object? content, bool isVisible)
    {
        if (TableView is null || content is null) return;

        TableView.DetailsPaneStates.AddOrUpdate(content, isVisible);
        var state = isVisible ? VisualStates.StateDetailsVisible : VisualStates.StateDetailsCollapsed;
        VisualStates.GoToState(this, false, state);
    }

    /// <summary>
    /// Ensures that the details pane visibility is synchronized for the specified item when row.
    /// </summary>
    internal void ApplyDetailsPaneState(object? item)
    {
        if (TableView?.RowDetailsVisibilityMode is TableViewRowDetailsVisibilityMode.VisibleWhenExpanded &&
            _detailsToggleButton is not null && TableView is not null && item is not null)
        {
            var isChecked = TableView.DetailsPaneStates.TryGetValue(item, out var value) && value.Value;
            _detailsToggleButton!.IsChecked = isChecked;
            ToggleDetailsPane(item, isChecked);
        }
    }

    /// <summary>
    /// Sets the DataTemplate for the row details.
    /// </summary>
    internal void SetRowDetailsTemplate()
    {
        if (_detailsPresenter is not null && TableView is not null)
        {
            _detailsPresenter.ContentTemplate =
                TableView.RowDetailsTemplateSelector?.SelectTemplate(TableViewRow?.Content)
                ?? TableView.RowDetailsTemplate;
        }
    }

    /// <summary>
    /// Sets the widths of the row header column.
    /// </summary>
    internal void SetRowHeaderWidth()
    {
        if (_rowHeader is not null && TableView is not null)
        {
            var headerWidth = TableView.RowHeaderWidth is double.NaN ? TableView.RowHeaderActualWidth : TableView.RowHeaderWidth;

            _rowHeader.Width = headerWidth;
            _rowHeader.MinWidth = TableView.RowHeaderMinWidth;
            _rowHeader.MaxWidth = TableView.RowHeaderMaxWidth;

            _rowHeader?.InvalidateMeasure();
            _rowHeader?.InvalidateArrange();
        }
    }

    /// <summary>
    /// Sets the visibility of the row header based on the TableView settings.
    /// </summary>
    internal void SetRowHeaderVisibility()
    {
        if (_rowHeader is not null && TableView is not null)
        {
            var areHeadersVisible = TableView.HeadersVisibility is TableViewHeadersVisibility.All or TableViewHeadersVisibility.Rows;
            var isMultiSelection = TableView is ListView { SelectionMode: ListViewSelectionMode.Multiple };
            var isDetailsToggleButtonVisible = TableView.RowDetailsVisibilityMode is TableViewRowDetailsVisibilityMode.VisibleWhenExpanded
                                               && (TableView.RowDetailsTemplate is not null || TableView.RowDetailsTemplateSelector is not null);

            if (areHeadersVisible && !isMultiSelection &&
               (!isDetailsToggleButtonVisible || TableView.RowHeaderTemplate is not null || TableView.RowHeaderTemplateSelector is not null))
            {
                _rowHeader.Visibility = Visibility.Visible;
                SetRowHeaderWidth();
            }
            else
            {
                _rowHeader.Visibility = Visibility.Collapsed;
            }

            EnsureGridLines();
        }
    }

    internal void SetRowHeaderBindings()
    {
        _rowHeader?.SetBinding(HeightProperty, new Binding
        {
            Path = new PropertyPath($"{nameof(TableViewRowHeader.TableView)}.{nameof(TableView.RowHeight)}"),
            RelativeSource = new RelativeSource { Mode = RelativeSourceMode.Self }
        });

        _rowHeader?.SetBinding(MaxHeightProperty, new Binding
        {
            Path = new PropertyPath($"{nameof(TableViewRowHeader.TableView)}.{nameof(TableView.RowMaxHeight)}"),
            RelativeSource = new RelativeSource { Mode = RelativeSourceMode.Self }
        });

        _rowHeader?.SetBinding(MinHeightProperty, new Binding
        {
            Path = new PropertyPath($"{nameof(TableViewRowHeader.TableView)}.{nameof(TableView.RowMinHeight)}"),
            RelativeSource = new RelativeSource { Mode = RelativeSourceMode.Self }
        });
    }

    /// <summary>
    /// Ensures grid lines are applied to the cells.
    /// </summary>
    internal void EnsureGridLines()
    {
        if (TableView is null) return;

        if (_h_gridLine is not null)
        {
            _h_gridLine.Fill = TableView.HorizontalGridLinesStroke;
            _h_gridLine.Height = TableView.HorizontalGridLinesStrokeThickness;
            _h_gridLine.Visibility = TableView.GridLinesVisibility is TableViewGridLinesVisibility.All or TableViewGridLinesVisibility.Horizontal
                                     ? Visibility.Visible : Visibility.Collapsed;

            if (_v_gridLine is not null)
            {
                var vGridLinesVisibility = TableView.HeaderGridLinesVisibility is TableViewGridLinesVisibility.All or TableViewGridLinesVisibility.Vertical
                                           || TableView.GridLinesVisibility is TableViewGridLinesVisibility.All or TableViewGridLinesVisibility.Vertical;
                var areHeadersVisible = TableView.HeadersVisibility is TableViewHeadersVisibility.All or TableViewHeadersVisibility.Rows;
                var isMultiSelection = TableView is ListView { SelectionMode: ListViewSelectionMode.Multiple };
                var isDetailsToggleButtonVisible = TableView.RowDetailsVisibilityMode is TableViewRowDetailsVisibilityMode.VisibleWhenExpanded
                                                    && (TableView.RowDetailsTemplate is not null || TableView.RowDetailsTemplateSelector is not null);

                _v_gridLine.Fill = TableView.GridLinesVisibility is TableViewGridLinesVisibility.All or TableViewGridLinesVisibility.Vertical
                                   ? TableView.VerticalGridLinesStroke : new SolidColorBrush(Colors.Transparent);
                _v_gridLine.Width = TableView.VerticalGridLinesStrokeThickness;
                _v_gridLine.Visibility = vGridLinesVisibility && (areHeadersVisible || isMultiSelection || isDetailsToggleButtonVisible) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        foreach (var cell in Cells)
        {
            cell.EnsureGridLines();
        }
    }

    internal double GetDetailsContentHeight()
    {
        return _detailsPanel?.Visibility is Visibility.Visible ? _detailsPanel.ActualHeight : 0d;
    }

    /// <summary>
    /// Inserts a cell at the specified index.
    /// </summary>
    /// <param name="cell">The cell to insert.</param>
    public void InsertCell(TableViewCell cell)
    {
        if (TableView is null || cell is not { Column: { } column }) return;

        var frozenColumns = TableView.Columns.VisibleFrozenColumns;
        var scrollableColumns = TableView.Columns.VisibleScrollableColumns;

        if (cell is { Column.IsFrozen: true } && _frozenCellsPanel is not null)
        {
            var index = frozenColumns.IndexOf(column);
            index = Math.Min(index, frozenColumns.Count);
            index = Math.Max(index, 0); // handles -ve index;

            _frozenCellsPanel.Children.Insert(index, cell);
            // Frozen cells occupy the prefix of the ordered cell list.
            _cellsList.Insert(Math.Min(index, _cellsList.Count), cell);
        }
        else if (_scrollableCellsPanel is not null)
        {
            var index = scrollableColumns.IndexOf(column);
            index = Math.Min(index, scrollableColumns.Count);
            index = Math.Max(index, 0); // handles -ve index;

            _scrollableCellsPanel.Children.Insert(index, cell);
            // Scrollable cells follow the frozen cells in the ordered cell list.
            var frozenCount = _frozenCellsPanel?.Children.Count ?? 0;
            _cellsList.Insert(Math.Min(frozenCount + index, _cellsList.Count), cell);
        }

        _cellsByColumn[column] = cell;
        cell.EnsureStyle(TableViewRow?.Content);
    }

    /// <summary>
    /// Removes a cell from the presenter.
    /// </summary>
    /// <param name="cell">The cell to remove.</param>
    public void RemoveCell(TableViewCell cell)
    {
        var removed = false;

        if (_frozenCellsPanel?.Children.Contains(cell) ?? false)
        {
            _frozenCellsPanel.Children.Remove(cell);
            removed = true;
        }
        else if (_scrollableCellsPanel?.Children.Contains(cell) ?? false)
        {
            _scrollableCellsPanel.Children.Remove(cell);
            removed = true;
        }

        if (removed)
        {
            _cellsList.Remove(cell);

            if (cell.Column is not null && _cellsByColumn.TryGetValue(cell.Column, out var existing) && existing == cell)
            {
                _cellsByColumn.Remove(cell.Column);
            }
        }
    }

    /// <summary>
    /// Moves the cell associated with the specified column to a new index.
    /// </summary>
    /// <param name="column">The column associated with the cell to move.</param>
    /// <param name="newIndex">The new index to move the cell to.</param>
    internal void MoveCells(TableViewColumn column, int newIndex)
    {
        if (GetCellForColumn(column) is { } cell)
        {
            RemoveCell(cell);
            InsertCell(cell);
        }

        if (newIndex >= 0 && newIndex < TableView?.FrozenColumnCount &&
           _frozenCellsPanel?.Children.OfType<TableViewCell>().LastOrDefault() is { } frozenCell)
        {
            RemoveCell(frozenCell);
            InsertCell(frozenCell);
        }

        UpdateCellIndexes();
    }

    /// <summary>
    /// Updates the indexes of all cells in the presenter.
    /// </summary>
    private void UpdateCellIndexes()
    {
        if (TableView is null) return;

        foreach (var cell in Cells)
        {
            if (cell.Column is not null)
            {
                var index = TableView.Columns.VisibleColumnIndex(cell.Column);
                if (cell.Index != index)
                    cell.Index = index;
            }
        }
    }

    /// <summary>
    /// Clears all cells from the presenter.
    /// </summary>
    public void ClearCells()
    {
        _frozenCellsPanel?.Children.Clear();
        _scrollableCellsPanel?.Children.Clear();
        _cellsList.Clear();
        _cellsByColumn.Clear();
    }

    /// <summary>
    /// Gets the cell associated with the specified column, or <see langword="null"/> if there is none.
    /// </summary>
    /// <param name="column">The column whose cell to retrieve.</param>
    internal TableViewCell? GetCellForColumn(TableViewColumn column)
    {
        return _cellsByColumn.GetValueOrDefault(column);
    }

    /// <summary>
    /// Gets the list of cells in the presenter.
    /// </summary>
    public IReadOnlyList<TableViewCell> Cells => _cellsList;

    /// <summary>
    /// Gets or sets the TableViewRow associated with the presenter.
    /// </summary>
    public TableViewRow? TableViewRow { get; private set; }

    /// <summary>
    /// Gets or sets the TableView associated with the presenter.
    /// </summary>
    public TableView? TableView { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the row details panel is currently visible.
    /// </summary>
    internal bool IsDetailsPanelVisible => _detailsPanel?.Visibility is Visibility.Visible;

    /// <summary>
    /// Gets the realized row header element.
    /// </summary>
    internal TableViewRowHeader? RowHeader => _rowHeader;

    /// <summary>
    /// Programmatically shows or hides the details pane.
    /// Only takes effect when <see cref="TableView.RowDetailsVisibilityMode"/> is
    /// <see cref="TableViewRowDetailsVisibilityMode.VisibleWhenExpanded"/>.
    /// </summary>
    /// <param name="visible"><see langword="true"/> to expand; <see langword="false"/> to collapse.</param>
    internal void ShowDetailPane(bool visible)
    {
        if (TableView?.RowDetailsVisibilityMode is TableViewRowDetailsVisibilityMode.VisibleWhenExpanded)
        {
            if (_detailsToggleButton is not null)
            {
                _detailsToggleButton.IsChecked = visible;
            }

            ToggleDetailsPane(TableViewRow?.Content, visible);
        }
    }
}
