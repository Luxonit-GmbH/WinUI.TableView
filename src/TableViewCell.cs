using Microsoft.UI;
using Microsoft.UI.Xaml;
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
    private ScrollViewer? _scrollViewer;
    private ContentPresenter? _contentPresenter;
    private Border? _selectionBorder;
    private Rectangle? _v_gridLine;
    private object? _uneditedValue;
    private RoutedEventArgs? _editingArgs;
    private double _contentDesiredWidth = double.NaN;
    private bool _contentPending;
    private bool _isInViewport;

    /// <summary>
    /// Initializes a new instance of the TableViewCell class.
    /// </summary>
    public TableViewCell()
    {
        DefaultStyleKey = typeof(TableViewCell);
        ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY;
        Loaded += OnLoaded;
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

        // Select the cell before showing the Context Menu
        if (TableView is not null && TableView.ForceRowOrCellSelectionOnContextRequested && !IsSelected)
        {
            TableView.MakeSelection(Slot, false);
        }

        e.Handled = TableView?.ShowCellContext(this, position) is true;
    }


    /// <summary>
    /// Handles the Loaded event.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InvalidateMeasure();
        ApplySelectionState();
    }

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _contentPresenter = GetTemplateChild("Content") as ContentPresenter;
        _selectionBorder = GetTemplateChild("SelectionBorder") as Border;
        _v_gridLine = GetTemplateChild("VerticalGridLine") as Rectangle;

        EnsureGridLines();
        EnsureStyle(Row?.Content);
    }

    /// <inheritdoc/>
    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);

        // The content element changed, so any cached desired width is stale.
        _contentDesiredWidth = double.NaN;

        // Re-measuring unconstrained once the content loads only feeds the column's desired (auto) width.
        // Skip subscribing for fixed and star sized columns to avoid an extra measure pass per cell.
        if (Column?.Width.IsAuto is true && newContent is ContentControl contentControl)
        {
            contentControl.Loaded += OnContentLoaded;
        }

        void OnContentLoaded(object sender, RoutedEventArgs e)
        {
            ((ContentControl)sender).Loaded -= OnContentLoaded;
            _contentDesiredWidth = double.NaN;
            InvalidateMeasure();
        }
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
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
            if (Column.Width.IsAuto)
            {
                EnsureDesiredWidth(element);
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
        if (Content is not FrameworkElement element)
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
        desiredWidth += _v_gridLine?.ActualWidth ?? 0d;

        return desiredWidth;
    }

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

        // TEMP_FIX_FOR_ISSUE https://github.com/microsoft/microsoft-ui-xaml/issues/9860
        var contentWidth = Column.ActualWidth;
        contentWidth -= element.Margin.Left;
        contentWidth -= element.Margin.Right;
        contentWidth -= Padding.Left;
        contentWidth -= Padding.Right;
        contentWidth -= BorderThickness.Left;
        contentWidth -= BorderThickness.Right;
        contentWidth -= _selectionBorder?.BorderThickness.Left ?? 0;
        contentWidth -= _selectionBorder?.BorderThickness.Right ?? 0;
        contentWidth -= _v_gridLine?.ActualWidth ?? 0d;

        var height = Height is double.NaN ? double.PositiveInfinity : Height;
        var contentHeight = Math.Min(height, MaxHeight);
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

        if (TableView?.CurrentCellSlot != Slot || TableView?.LastSelectionUnit is TableViewSelectionUnit.Row)
        {
            MakeSelection();
            e.Handled = true;
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

        if (!KeyboardHelper.IsShiftKeyDown() && TableView is not null)
        {
            TableView.SelectionStartCellSlot = TableView.SelectionUnit is not TableViewSelectionUnit.Row || !IsReadOnly ? Slot : default;
            TableView.SelectionStartRowIndex = Index;
            CapturePointer(e.Pointer);

            // Start drag selection (auto-scroll + optional rectangle visual)
            var point = e.GetCurrentPoint(this).Position;
            var canvasPoint = TransformPointToCanvas(point);
            if (canvasPoint.HasValue)
            {
                TableView.StartDragSelection(canvasPoint.Value);
            }
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (!KeyboardHelper.IsShiftKeyDown() && TableView is not null)
        {
            var cell = FindCell(e.GetCurrentPoint(this).Position);
            TableView.SelectionStartCellSlot = TableView.SelectionUnit is not TableViewSelectionUnit.Row || !IsReadOnly ? cell?.Slot : default;
            TableView.SelectionStartRowIndex = cell?.Slot.Row;
        }

        TableView?.EndDragSelection();
        ReleasePointerCaptures();

        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        base.OnPointerCaptureLost(e);

        TableView?.EndDragSelection();
    }

    /// <inheritdoc/>
    protected override void OnManipulationDelta(ManipulationDeltaRoutedEventArgs e)
    {
        base.OnManipulationDelta(e);

        if (PointerCaptures?.Any() is true)
        {
            // Update drag rectangle visual and auto-scroll
            if (TableView?.IsDragSelecting is true)
            {
                var canvasPoint = TransformPointToCanvas(e.Position);
                if (canvasPoint.HasValue)
                {
                    TableView.UpdateDragRectangleVisual(canvasPoint.Value);
                }
            }

            // Selection via FindCell — same proven path whether rectangle is on or off.
            // When the pointer is outside the viewport, FindCell returns null and selection
            // is updated by the ViewChanged handler on the next auto-scroll tick.
            var cell = FindCell(e.Position);

            if (cell is not null && cell.Slot != TableView?.CurrentCellSlot)
            {
                var ctrlKey = KeyboardHelper.IsCtrlKeyDown();
                TableView?.MakeSelection(cell.Slot, true, ctrlKey);
            }
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

    /// <summary>
    /// Finds the cell at the specified position.
    /// </summary>
    private TableViewCell? FindCell(Point position)
    {
        _scrollViewer ??= TableView?.FindDescendant<ScrollViewer>();
        if (_scrollViewer is null) return null;

        var transformedPoint = TransformToVisual(null).TransformPoint(position);
#if WINDOWS
        return VisualTreeHelper.FindElementsInHostCoordinates(transformedPoint, _scrollViewer)
#else
        return VisualTreeHelper.FindElementsInHostCoordinates(transformedPoint, _scrollViewer, true)
                               .OfType<ContentPresenter>()
                               .Where(x => x.Name is "Content")
                               .Select(x => x.FindAscendant<TableViewCell>() is { } header ? header : default)
#endif
                               .OfType<TableViewCell>()
                               .FirstOrDefault();
    }

    /// <summary>
    /// Transforms a point relative to this cell to coordinates relative to the drag rectangle canvas.
    /// </summary>
    private Point? TransformPointToCanvas(Point position)
    {
        if (TableView?.DragRectangleCanvas is null) return null;

        try
        {
            var transform = TransformToVisual(TableView.DragRectangleCanvas);
            return transform.TransformPoint(position);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    protected override void OnDoubleTapped(DoubleTappedRoutedEventArgs e)
    {
        var eventArgs = new TableViewCellDoubleTappedEventArgs(Slot, this, Row?.Content);
        TableView?.OnCellDoubleTapped(eventArgs);
        e.Handled = eventArgs.Handled;

        if (e.Handled) return;

        base.OnDoubleTapped(e);

        if (!IsReadOnly && TableView is not null && !TableView.IsEditing && !Column?.UseSingleElement is true)
        {
            e.Handled = BeginCellEditing(e);
        }
        else
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Makes a selection based on the current cell.
    /// </summary>
    private void MakeSelection()
    {
        var shiftKey = KeyboardHelper.IsShiftKeyDown();
        var ctrlKey = KeyboardHelper.IsCtrlKeyDown();

        if (TableView is null || Column is null)
        {
            return;
        }

        if ((TableView.IsEditing || Column.UseSingleElement) && IsCurrent)
        {
            return;
        }

        if (IsSelected && (ctrlKey || TableView.SelectionMode is ListViewSelectionMode.Multiple) && !shiftKey)
        {
            TableView.DeselectCell(Slot);
        }
        else
        {
            if (Column.UseSingleElement)
            {
                TableView.DeselectCell(Slot);
            }

            TableView.MakeSelection(Slot, shiftKey, ctrlKey);
        }

        TableView.SetIsEditing(false);
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
    /// Applies the selection state to the cell.
    /// </summary>
    internal void ApplySelectionState(bool onlyToStateSelected = false)
    {
        var isSelected = IsSelected;
        
        if (onlyToStateSelected && !isSelected)
        {
            return;
        }
        
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

            await Task.Delay(20);
            if (Content is UIElement { IsHitTestVisible: true } element)
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
    /// Sets whether this cell's column is currently within the horizontal viewport. When it leaves the viewport the
    /// cell collapses its content (skipping the content measure on subsequent passes); when it re-enters, the content
    /// is restored. Only meaningful while <see cref="WinUI.TableView.TableView.IsColumnVirtualizationEnabled"/> is set.
    /// </summary>
    internal void SetInViewport(bool value)
    {
        if (_isInViewport == value)
        {
            return;
        }

        _isInViewport = value;
        InvalidateMeasure();
    }

    /// <summary>
    /// Ensures grid lines are applied to the cell.
    /// </summary>
    internal void EnsureGridLines()
    {
        if (_v_gridLine is not null && TableView is not null)
        {
            _v_gridLine.Fill = TableView.GridLinesVisibility is TableViewGridLinesVisibility.All or TableViewGridLinesVisibility.Vertical
                               ? TableView.VerticalGridLinesStroke : new SolidColorBrush(Colors.Transparent);
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
        Style? winningStyle = null;
        
        // Column styles
        if (winningStyle == null)
        {
             var columnStyles = Column?.ConditionalCellStyles;
             if (columnStyles is { Count: > 0 })
             {
                 winningStyle = columnStyles.FirstOrDefault(c => c.Predicate?.Invoke(new(Column!, item)) is true)?.Style ?? null;
             }
        }
        
        // Table View styles
        if (winningStyle == null)
        {
             var tableViewStyles = TableView?.ConditionalCellStyles;
             if (tableViewStyles is { Count: > 0 })
             {
                 winningStyle = tableViewStyles.FirstOrDefault(c => c.Predicate?.Invoke(new(Column!, item)) is true)?.Style ?? null;
             }
        }
        
        // Result style
        Style = winningStyle ?? Column?.CellStyle ?? TableView?.CellStyle;
    }

    /// <summary>
    /// Gets a value indicating whether the cell is read-only.
    /// </summary>
    public bool IsReadOnly => TableView?.IsReadOnly is true || Column is TableViewTemplateColumn { EditingTemplate: null } or { IsReadOnly: true };

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
}
