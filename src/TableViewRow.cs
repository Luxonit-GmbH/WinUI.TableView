using Microsoft.UI.Xaml;
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
    private bool _ensureCells = true;
    private Brush? _cellPresenterBackground;
    private Brush? _cellPresenterForeground;
    private int? _cachedIndex;

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

        // Select the row before showing the Context Menu
        if (TableView is not null && TableView.ForceRowOrCellSelectionOnContextRequested && !IsSelected)
        {
            TableView.MakeSelection(new TableViewCellSlot(Index, -1), false);
        }

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

        RowPresenter?.EnsureGridLines();
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
#if !WINDOWS
        RowPresenter = GetTemplateChild("RowPresenter") as TableViewRowPresenter;
        _selectionBackground = GetTemplateChild("SelectionBackground") as Border;
#endif
    }

    /// <inheritdoc/>
    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);

        if (_ensureCells)
        {
            EnsureCells();
        }
        else
        {
            foreach (var cell in Cells)
            {
                // The data item changed; the cached auto-size width no longer reflects this cell's content.
                cell.InvalidateDesiredWidth();
                cell.RefreshElement();
            }

            TableView?.RealizeRowCells(this); // Ensure visible columns are realized for the recycled row.
        }

        RowPresenter?.InvalidateMeasure(); // The cells presenter does not measure every time.

        // On recycle only THIS row's index (and therefore its alternate color) changed — re-color just this row,
        // synchronously and O(1), instead of enqueuing a full-grid re-color pass on every recycled container.
        EnsureAlternateColors();
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (TableView is { IsEditing: false })
        {
            base.OnPointerPressed(e);
        }

        if (!KeyboardHelper.IsShiftKeyDown() && TableView is not null)
        {
            TableView.SelectionStartRowIndex = Index;
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (!KeyboardHelper.IsShiftKeyDown() && TableView is not null)
        {
            TableView.SelectionStartCellSlot = null;
            TableView.SelectionStartRowIndex = Index;
        }
    }

    /// <inheritdoc/>
    protected override void OnTapped(TappedRoutedEventArgs e)
    {
        base.OnTapped(e);

        if (TableView?.SelectionUnit is TableViewSelectionUnit.Row or TableViewSelectionUnit.CellOrRow)
        {
            TableView.CurrentRowIndex = Index;
            TableView.LastSelectionUnit = TableViewSelectionUnit.Row;
        }
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

        return finalSize;
    }

    /// <summary>
    /// Ensures cells are created for the row.
    /// </summary>
    internal void EnsureCells()
    {
        if (TableView is null)
        {
            return;
        }

        if (RowPresenter is not null && _ensureCells)
        {
            RowPresenter.ClearCells();

            AddCells(TableView.Columns.VisibleColumns);
            _ensureCells = false;

            TableView.RealizeRowCells(this); // No-op unless column virtualization is enabled.
        }
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
            case NotifyCollectionChangedAction.Reset when RowPresenter is not null:
                RowPresenter.ClearCells();
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
        if (RowPresenter is not null)
        {
            foreach (var column in columns)
            {
                var cell = RowPresenter.GetCellForColumn(column);
                if (cell is not null)
                {
                    RowPresenter.RemoveCell(cell);
                }
            }
        }
    }

    /// <summary>
    /// Adds cells for the specified columns.
    /// </summary>
    private void AddCells(IEnumerable<TableViewColumn> columns)
    {
        if (RowPresenter is not null && TableView is not null)
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
        foreach (var cell in Cells)
        {
            if (column == null || cell.Column == column)
            {
                cell.EnsureStyle(dataItem ?? Content);
            }
        }
    }

    /// <summary>
    /// Applies the current cell state to the specified slot.
    /// </summary>
    internal void ApplyCurrentCellState(TableViewCellSlot slot)
    {
        if (slot.Column >= 0 && slot.Column < Cells.Count)
        {
            var cell = Cells[slot.Column];
            cell.ApplyCurrentCellState();
        }
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
        _selectionIndicator ??= _itemPresenter?.FindDescendants()
                                               .OfType<Border>()
                                               .FirstOrDefault(x => x is { Width: 3 });

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

        _selectionBackground ??= _itemPresenter?.FindDescendants()
                                                .OfType<Border>()
                                                .FirstOrDefault(x => x.Name is not Selection_Background && x.Margin == _selectionBackgroundMargin);

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
}
