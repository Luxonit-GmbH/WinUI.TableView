using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
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
    private Panel? _pinnedHeaderPanel;
    private bool _pinnedToPan;
    private bool _detailsPinned;
    private Panel? _scrollableCellsPanel;
    private StackPanel? _frozenCellsPanel;
    private readonly List<TableViewCell> _cellsList = [];
    private readonly Dictionary<TableViewColumn, TableViewCell> _cellsByColumn = [];
    private Rectangle? _v_gridLine;
    private Rectangle? _h_gridLine;
    private Panel? _detailsPanel;
    private ContentPresenter? _detailsPresenter;
    private ContentPresenter? _bannerPresenter;
    private ToggleButton? _detailsToggleButton;
    private ListViewItemPresenter? _itemPresenter;
    private long? _detailsPanelVisibilityCallbackToken;
    private int _rowHeaderLayoutVersion = -1; // TableView.RowHeaderLayoutVersion the header was last invalidated for

    /// <summary>
    /// Initializes a new instance of the <see cref="TableViewRowPresenter"/> class.
    /// </summary>
    public TableViewRowPresenter()
    {
        DefaultStyleKey = typeof(TableViewRowPresenter);
        RegisterPropertyChangedCallback(PaddingProperty, delegate { ApplyRootPanelMargin(); });
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
        _pinnedHeaderPanel = GetTemplateChild("PinnedHeaderPanel") as Panel;
        _scrollableCellsPanel = GetTemplateChild("ScrollableCellsPanel") as Panel;
        _frozenCellsPanel = GetTemplateChild("FrozenCellsPanel") as StackPanel;
        _cellsList.Clear(); // Template (re)applied: the new panels start empty.
        _pinnedToPan = false;   // ...and the new chrome needs re-binding to the pan offset.
        _detailsPinned = false;
        _v_gridLine = GetTemplateChild("VerticalGridLine") as Rectangle;
        _h_gridLine = GetTemplateChild("HorizontalGridLine") as Rectangle;
        _detailsPanel = GetTemplateChild("DetailsPanel") as Panel;
        _detailsPresenter = GetTemplateChild("DetailsPresenter") as ContentPresenter;
        _bannerPresenter = GetTemplateChild("BannerPresenter") as ContentPresenter;
        _detailsToggleButton = GetTemplateChild("DetailsToggleButton") as ToggleButton;

        _itemPresenter = this.FindAscendant<ListViewItemPresenter>();
        TableViewRow = this.FindAscendant<TableViewRow>();
        TableView = TableViewRow?.TableView;
        _rowHeader?.TableView = TableView;
        _rowHeader?.TableViewRow = TableViewRow;

        ApplyRootPanelMargin();

        _detailsToggleButton?.Tapped += OnDetailsToggleButtonTapped;

        if (_detailsPanel is not null)
        {
            _detailsPanel.SizeChanged += OnDetailsPanelSizeChanged;
            _detailsPanelVisibilityCallbackToken =
                _detailsPanel.RegisterPropertyChangedCallback(VisibilityProperty, OnDetailsPanelVisibilityChanged);
        }

        // The template is applied AFTER PrepareContainerForItemOverride ran, so the call from there found no
        // presenter yet. Settle it here from the row's own item, the reliable hook.
        ApplyBannerPresentation(TableViewRow?.Content);

        TableViewRow?.EnsureCells();
        EnsureGridLines();
        SetRowHeaderHeights();
        SetRowHeaderVisibility();
        SetRowHeaderTemplate();
        SetRowHeaderWidth();
        SetRowDetailsVisibility();
        SetRowDetailsTemplate();

        PinChromeToPan();
    }

    /// <summary>
    /// Positions the root panel: the presenter's own padding, plus the shift that keeps the cells in place while
    /// the item presenter hangs its rounded corner off the left edge (or, in multi-select, clears the check box).
    /// </summary>
    /// <remarks>
    /// This replaces an explicit re-arrange of the panel at a shifted origin after every base arrange, which cost
    /// the grid a second full column and row resolution per row per layout pass. The right margin is the negative
    /// of the left one so the panel keeps its full width and overhangs on the right, exactly as the explicit
    /// arrange left it. Re-applied when the padding or the selection mode changes.
    /// </remarks>
    internal void ApplyRootPanelMargin()
    {
        if (_rootPanel is null)
        {
            return;
        }

        var cornerRadius = _itemPresenter?.CornerRadius ?? new CornerRadius(0);
        var isMultiSelection = TableView is ListView { SelectionMode: ListViewSelectionMode.Multiple };
        var left = isMultiSelection ? 44 : Math.Max(cornerRadius.TopLeft, cornerRadius.BottomLeft);
        var padding = Padding;
        var margin = new Thickness(padding.Left + left, padding.Top, padding.Right - left, padding.Bottom);

        if (_rootPanel.Margin != margin)
        {
            _rootPanel.Margin = margin;
        }
    }

    /// <summary>
    /// Counter-translates the chrome that must not scroll. The items panel pans as a single visual, which carries
    /// every row with it; these three ride back the other way so they hold their place.
    /// </summary>
    /// <remarks>
    /// Bound once per template application, to expression animations over the TableView's shared offset — so a
    /// scroll tick costs one scalar write for the whole grid rather than a property set per row. The template
    /// gives them a higher Canvas.ZIndex so the cells sliding underneath stay underneath, for hit-testing too.
    /// </remarks>
    private void PinChromeToPan()
    {
        if (_pinnedToPan || TableView is null)
        {
            return;
        }

        if (_pinnedHeaderPanel is null && _v_gridLine is null && _frozenCellsPanel is null)
        {
            return; // template not applied yet; the next OnApplyTemplate will bind
        }

        if (_pinnedHeaderPanel is not null) TableView.BindToPan(_pinnedHeaderPanel, pinned: true);
        if (_v_gridLine is not null) TableView.BindToPan(_v_gridLine, pinned: true);
        if (_frozenCellsPanel is not null) TableView.BindToPan(_frozenCellsPanel, pinned: true);

        _pinnedToPan = true;
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
        // The row header lays out from a handful of grid properties (its width settings, the headers' visibility,
        // the row heights, its template) and from its own content, and a change to any of those invalidates it by
        // itself — the content through the normal child-to-parent propagation, the properties through the version
        // the grid bumps in their handlers. It used to be invalidated here unconditionally, so every row a band
        // change dirtied re-measured its header for nothing. Now only when something it depends on has changed
        // since this presenter last looked.
        if (TableView is { } tableView && !tableView.IsColumnResizing && _rowHeaderLayoutVersion != tableView.RowHeaderLayoutVersion)
        {
            _rowHeaderLayoutVersion = tableView.RowHeaderLayoutVersion;
            _rowHeader?.InvalidateMeasure();
        }

        return base.MeasureOverride(availableSize);
    }

    /// <summary>
    /// The built-in group header: a chevron, the title and the count. Built once, in code, because a code-only
    /// control needs no XAML file to be referenced from.
    /// </summary>
    private static DataTemplate? _defaultGroupHeaderTemplate;

    private static DataTemplate DefaultGroupHeaderTemplate =>
        _defaultGroupHeaderTemplate ??= (DataTemplate)XamlReader.Load(
            """
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                          xmlns:tv="using:WinUI.TableView">
                <tv:TableViewGroupHeader />
            </DataTemplate>
            """);

    /// <summary>
    /// Switches the row between the normal cell layout and a single full-width banner.
    /// </summary>
    /// <param name="item">The row's item; a banner is shown when it is an <see cref="ITableViewBannerItem"/>.</param>
    internal void ApplyBannerPresentation(object? item)
    {
        if (_bannerPresenter is null)
        {
            return;
        }

        if (item is ITableViewBannerItem banner)
        {
            _bannerPresenter.Content = banner.BannerContent ?? item;

            // A grouping header gets the group template (or the built-in chevron/title/count) rather than the
            // generic banner template, so grouping needs no setup from the consumer.
            _bannerPresenter.ContentTemplate = item is TableViewGroup
                ? TableView?.GroupHeaderTemplate ?? DefaultGroupHeaderTemplate
                : TableView?.BannerRowTemplate;

            _bannerPresenter.Visibility = Visibility.Visible;

            // Replace, never overlay: the cells and details are hand-arranged and would be drawn on top.
            if (_rootPanel is not null)
            {
                _rootPanel.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            _bannerPresenter.Content = null;
            _bannerPresenter.Visibility = Visibility.Collapsed;

            if (_rootPanel is not null)
            {
                _rootPanel.Visibility = Visibility.Visible;
            }
        }
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        finalSize = base.ArrangeOverride(finalSize);

        // A banner row has no cells to place, and the hand-arranging below would drag the collapsed row layout
        // back over the banner — the same way a hardcoded y=0 did to the column-group headers.
        if (_bannerPresenter?.Visibility is Visibility.Visible)
        {
            return finalSize;
        }

        if (TableView is not null)
        {
            var cornerRadius = _itemPresenter?.CornerRadius ?? new CornerRadius(0);

            // RootPanel is no longer re-arranged here: its shift is a margin (see ApplyRootPanelMargin). Arranging
            // it a second time at a shifted origin re-ran the grid's whole column and row resolution on every pass,
            // and the row above did the same one level up at a shifted SIZE, which cascaded down through this
            // presenter and the cells panel. The scrollable cells panel needs no explicit arrange either: the grid
            // already places it exactly where the old call put it, and the per-row transform that call was written
            // for no longer exists, since the items panel pans as one visual.
            if (_detailsPanel?.Visibility is Visibility.Visible && _v_gridLine is not null)
            {
                var x = _v_gridLine.ActualOffset.X + _v_gridLine.ActualWidth;
                var y = _scrollableCellsPanel?.ActualHeight ?? _v_gridLine.ActualOffset.Y;
                _detailsPanel.Arrange(new(x, y, _detailsPanel.ActualWidth, _detailsPanel.ActualHeight));
            }

            ApplyHorizontalScroll();

            // CellsHorizontalOffset is the boundary between the row header and the data cells — it's positioned
            // purely by HeaderColumn's width (see TableViewRowPresenter.xaml's ColumnDefinitions), so it never
            // depends on any data column's width and is safe to skip recomputing during a resize drag.
            //
            // It is also uniform across rows, so even when it IS recomputed only the first row to arrange in a
            // given layout pass computes it; the rest read the published value. The two conditions are
            // independent: one skips the work for a whole gesture, the other de-duplicates it within a single pass.
            if (!TableView.IsColumnResizing && _rootPanel is not null && _pinnedHeaderPanel is not null && TableView.TryClaimCellsOffsetUpdate())
            {
                // From the inputs that place the boundary, not from where something ended up. The root panel's
                // left margin is the presenter's padding plus the corner shift (ApplyRootPanelMargin), the pinned
                // header panel is the whole of the column before the cells, and both are arrange-current here,
                // after the base pass. This used to be an ActualOffset walk up from the vertical grid line, and
                // whichever row claimed the pass first published what its walk found: 0 from a row whose line was
                // collapsed or not yet arranged, and the header row's corner panel took that as its width.
                //
                // Layout positions only — NOT TransformToVisual. This value is a layout boundary (where the cells
                // start), and TransformToVisual mixes in composition state: it reports the chrome's
                // counter-translation once the compositor has committed it, and not before. In the synchronous
                // layout pass right after a scroll it has not, so subtracting HorizontalOffset from it went
                // negative, clamped to 0, and the header's corner panel collapsed — every header slid 16px (the
                // row header width) left of its cells, intermittently, depending on whether a later re-arrange
                // happened to run after the commit.
                var offset = _rootPanel.Margin.Left + _pinnedHeaderPanel.ActualWidth;
                offset -= Math.Max(cornerRadius.TopLeft, cornerRadius.BottomLeft);

                TableView.SetValue(TableView.CellsHorizontalOffsetProperty, Math.Max(0, offset));
            }
        }

        return finalSize;
    }

    /// <summary>
    /// Binds this row's frozen row-details panel to the shared pan offset, once.
    /// </summary>
    /// <remarks>
    /// The name is historical. Rows no longer move their own cells: the items panel pans as a single composition
    /// visual and carries every row with it, so a scroll tick is one scalar write for the whole grid rather than a
    /// transform and a clip per row. The only thing left for a row to do is counter-translate a details panel that
    /// is pinned, and even that is bound once rather than applied per tick.
    /// </remarks>
    internal void ApplyHorizontalScroll()
    {
        // The cells no longer move per row: the items panel pans as one visual and carries them. All that is left
        // here is the row-details panel, which pans with the row unless it is frozen, in which case it needs the
        // same counter-translation as the rest of the pinned chrome. Bound once, not per tick.
        if (TableView is null || _detailsPanel is null || _detailsPinned)
        {
            return;
        }

        if (_detailsPanel.Visibility is Visibility.Visible && TableView.AreRowDetailsFrozen)
        {
            TableView.BindToPan(_detailsPanel, pinned: true);
            _detailsPinned = true;
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

    /// <summary>
    /// Sizes the row header to the row: the same effective heights the cells get, where an explicit RowHeight caps
    /// RowMinHeight. Set directly rather than bound — the grid re-applies them from its row height handler through
    /// <see cref="TableViewRow.ApplyCellHeights"/>, which is also what keeps the cells current — so three bindings
    /// per row go away, and the header can no longer disagree with the cells about the minimum.
    /// </summary>
    internal void SetRowHeaderHeights()
    {
        if (_rowHeader is null || TableView is null)
        {
            return;
        }

        _rowHeader.Height = TableView.RowHeight;
        _rowHeader.MaxHeight = TableView.RowMaxHeight;
        _rowHeader.MinHeight = TableView.EffectiveRowMinHeight;
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
            (_scrollableCellsPanel as TableViewCellsPanel)?.InvalidateChildSnapshot();
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
            (_scrollableCellsPanel as TableViewCellsPanel)?.InvalidateChildSnapshot();
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
        (_scrollableCellsPanel as TableViewCellsPanel)?.InvalidateChildSnapshot();
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
    /// Gets the panel hosting scrollable (non-frozen) cells. Used to shift the whole scrollable
    /// region in one shot when a frozen column is being resized, instead of shifting every
    /// scrollable cell individually.
    /// </summary>
    internal Panel? ScrollableCellsPanel => _scrollableCellsPanel;

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
