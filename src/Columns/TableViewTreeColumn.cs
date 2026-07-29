using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using WinUI.TableView.Columns;

namespace WinUI.TableView;

/// <summary>
/// The hierarchy column of a <see cref="TreeTableView"/>: renders per-item indentation, an expander chevron and the
/// bound text for items implementing <see cref="ITableViewTreeItem"/>.
/// </summary>
/// <remarks>
/// The generated element is deliberately lightweight (indent spacer + chevron button + TextBlock). The tree-state
/// visuals (indent, chevron, loading ring) are driven DIRECTLY from the <see cref="ITableViewTreeItem"/> interface —
/// reacting to <see cref="INotifyPropertyChanged"/> — rather than through name-based bindings, so they work for any
/// item type (including rows that do not implement the interface at all: those render as plain rows with no chevron)
/// and independently of reflection/AOT concerns; only the text uses the column's
/// <see cref="TableViewBoundColumn.Binding"/>. The chevron slot is always reserved so leaf rows align with
/// expandable siblings at the same depth. Chevron clicks (and Left/Right keys, double-click — handled by
/// <see cref="TreeTableView"/>) only raise
/// <see cref="TreeTableView.ExpandRequested"/>/<see cref="TreeTableView.CollapseRequested"/> — the items source
/// performs the actual expansion.
/// </remarks>
[StyleTypedProperty(Property = nameof(ElementStyle), StyleTargetType = typeof(TextBlock))]
[StyleTypedProperty(Property = nameof(EditingElementStyle), StyleTargetType = typeof(TextBox))]
#if WINDOWS
[WinRT.GeneratedBindableCustomProperty]
#endif
public partial class TableViewTreeColumn : TableViewBoundColumn
{
    private const double ChevronSlotWidth = 20d;
    private const string CollapsedGlyph = ""; // ChevronRight
    private const string ExpandedGlyph = "";  // ChevronDown

    /// <summary>
    /// Generates the tree cell: indentation sized from <see cref="ITableViewTreeItem.Depth"/>, a chevron reflecting
    /// <see cref="ITableViewTreeItem.HasChildren"/>/<see cref="ITableViewTreeItem.IsExpanded"/>, and the bound text.
    /// </summary>
    /// <param name="cell">The cell for which the element is generated.</param>
    /// <param name="dataItem">The data item associated with the cell.</param>
    /// <returns>The cell element.</returns>
    public override FrameworkElement GenerateElement(TableViewCell cell, object? dataItem)
    {
        var root = new Grid
        {
            Margin = new Thickness(12, 0, 12, 0),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(ChevronSlotWidth) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            },
        };

        var indent = new Border();

        var glyph = new FontIcon
        {
            Glyph = CollapsedGlyph,
            FontSize = 12,
        };

        // Async expansion: while the source loads children (IsLoading), a small ring replaces the chevron glyph.
        // Requests are ignored during the load (TreeTableView.RequestExpandCollapse gates on IsLoading).
        var loadingRing = new ProgressRing
        {
            Width = 14,
            Height = 14,
            IsActive = false,
            Visibility = Visibility.Collapsed,
        };

        var chevronContent = new Grid();
        chevronContent.Children.Add(glyph);
        chevronContent.Children.Add(loadingRing);

        var chevron = new Button
        {
            Content = chevronContent,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsTabStop = false,
        };
        chevron.Click += (_, _) =>
        {
            // The cell is stable while its element is recycled across items, so resolve item and index at click time.
            if (cell.TableView is TreeTableView treeTableView && cell.Row is { Content: ITableViewTreeItem item } row)
            {
                treeTableView.RequestExpandCollapse(item, row.Index, !item.IsExpanded);
            }
        };
        Grid.SetColumn(chevron, 1);

        // Content: a template when one is supplied (the item becomes the ContentControl's DataContext, so the
        // template binds against the row item exactly like a TableViewTemplateColumn), otherwise the bound text.
        FrameworkElement content;

        if (CellTemplate is not null)
        {
            content = new ContentControl
            {
                ContentTemplate = CellTemplate,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsTabStop = false,
            };
            ((ContentControl)content).SetBinding(ContentControl.ContentProperty, new Binding());
        }
        else
        {
            var textBlock = new TextBlock { VerticalAlignment = VerticalAlignment.Center };

            if (Binding is not null)
            {
                textBlock.SetBinding(TextBlock.TextProperty, Binding);
            }

            content = textBlock;
        }

        Grid.SetColumn(content, 2);

        root.Children.Add(indent);
        root.Children.Add(chevron);
        root.Children.Add(content);

        // Interface-driven state: follows the row item across recycling (DataContextChanged) and its
        // INotifyPropertyChanged notifications. Items not implementing ITableViewTreeItem render fail-closed
        // (no indent, no chevron, not clickable).
        _ = new TreeCellVisuals(this, root, indent, glyph, loadingRing, chevron);

        return root;
    }

    /// <summary>
    /// Keeps one tree cell's visuals in sync with its current <see cref="ITableViewTreeItem"/> (or plain item).
    /// Lifetime is tied to the generated element via the DataContextChanged/Unloaded subscriptions.
    /// </summary>
    private sealed class TreeCellVisuals
    {
        private readonly TableViewTreeColumn _column;
        private readonly Border _indent;
        private readonly FontIcon _glyph;
        private readonly ProgressRing _loadingRing;
        private readonly Button _chevron;
        private ITableViewTreeItem? _item;

        public TreeCellVisuals(TableViewTreeColumn column, Grid root, Border indent, FontIcon glyph, ProgressRing loadingRing, Button chevron)
        {
            _column = column;
            _indent = indent;
            _glyph = glyph;
            _loadingRing = loadingRing;
            _chevron = chevron;

            root.DataContextChanged += (_, e) => Attach(e.NewValue);

            // Unloaded/Loaded pairs fire during virtualization churn with the SAME DataContext (so
            // DataContextChanged won't re-fire): Unloaded must only drop the INPC subscription — never reset the
            // visuals — and Loaded re-attaches to the current item.
            root.Unloaded += (_, _) => DetachSubscription();
            root.Loaded += (root, _) => Attach(((FrameworkElement)root).DataContext);
            Attach(root.DataContext);
        }

        private void DetachSubscription()
        {
            if (_item is not null)
            {
                _item.PropertyChanged -= OnItemPropertyChanged;
                _item = null;
            }
        }

        private void Attach(object? dataContext)
        {
            var item = dataContext as ITableViewTreeItem;

            if (ReferenceEquals(_item, item))
            {
                if (item is null)
                {
                    Apply();
                }

                return;
            }

            DetachSubscription();
            _item = item;

            if (_item is not null)
            {
                _item.PropertyChanged += OnItemPropertyChanged;
            }

            Apply();
        }

        private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e) => Apply();

        private void Apply()
        {
            if (_item is null)
            {
                _indent.Width = 0d;
                _glyph.Visibility = Visibility.Collapsed;
                _loadingRing.IsActive = false;
                _loadingRing.Visibility = Visibility.Collapsed;
                _chevron.IsHitTestVisible = false;
                return;
            }

            _indent.Width = _item.Depth > 0 ? _item.Depth * _column.IndentWidth : 0d;
            _glyph.Glyph = _item.IsExpanded ? ExpandedGlyph : CollapsedGlyph;
            _glyph.Opacity = _item.IsLoading ? 0d : 1d; // opacity, not visibility: layout stays stable under the ring
            _glyph.Visibility = _item.HasChildren && !_item.IsFinalItem ? Visibility.Visible : Visibility.Collapsed;
            _loadingRing.IsActive = _item.IsLoading;
            _loadingRing.Visibility = _item.IsLoading ? Visibility.Visible : Visibility.Collapsed;
            _chevron.IsHitTestVisible = !_item.IsFinalItem && (_item.HasChildren || _item.IsLoading);
        }
    }

    /// <summary>
    /// Generates a TextBox for editing the bound text, matching <see cref="TableViewTextColumn"/> behavior.
    /// </summary>
    /// <param name="cell">The cell for which the editing element is generated.</param>
    /// <param name="dataItem">The data item associated with the cell.</param>
    /// <returns>A TextBox element.</returns>
    public override FrameworkElement GenerateEditingElement(TableViewCell cell, object? dataItem)
    {
        if (CellEditingTemplate is not null)
        {
            var host = new ContentControl
            {
                ContentTemplate = CellEditingTemplate,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Content = dataItem,
            };

            return host;
        }

        var textBox = new TextBox();
        if (Binding is not null)
        {
            textBox.SetBinding(TextBox.TextProperty, Binding);
        }
#if !WINDOWS
        textBox.DataContext = dataItem;
#endif
        return textBox;
    }

    /// <summary>
    /// Gets or sets the template used to render the tree cell's content, in place of the bound text. The
    /// indentation and expander chevron are still supplied by the column, so the template only describes the
    /// item's own presentation; the row item is the template's DataContext.
    /// </summary>
    public DataTemplate? CellTemplate
    {
        get => (DataTemplate?)GetValue(CellTemplateProperty);
        set => SetValue(CellTemplateProperty, value);
    }

    /// <summary>
    /// Identifies the CellTemplate dependency property.
    /// </summary>
    public static readonly DependencyProperty CellTemplateProperty = DependencyProperty.Register(nameof(CellTemplate), typeof(DataTemplate), typeof(TableViewTreeColumn), new PropertyMetadata(null));

    /// <summary>
    /// Gets or sets the template used while the tree cell is being edited, in place of the default TextBox.
    /// </summary>
    public DataTemplate? CellEditingTemplate
    {
        get => (DataTemplate?)GetValue(CellEditingTemplateProperty);
        set => SetValue(CellEditingTemplateProperty, value);
    }

    /// <summary>
    /// Identifies the CellEditingTemplate dependency property.
    /// </summary>
    public static readonly DependencyProperty CellEditingTemplateProperty = DependencyProperty.Register(nameof(CellEditingTemplate), typeof(DataTemplate), typeof(TableViewTreeColumn), new PropertyMetadata(null));

    /// <inheritdoc/>
    protected internal override object? PrepareCellForEdit(TableViewCell cell, RoutedEventArgs routedEvent)
    {
        if (cell.Content is TextBox textBox)
        {
            textBox.SelectAll();
            return textBox.Text;
        }

        return base.PrepareCellForEdit(cell, routedEvent);
    }

    /// <inheritdoc/>
    protected internal override void EndCellEditing(TableViewCell cell, object? dataItem, TableViewEditAction editAction, object? uneditedValue)
    {
        if (cell.Content is TextBox textBox && editAction == TableViewEditAction.Commit)
        {
            var bindingExpression = textBox.GetBindingExpression(TextBox.TextProperty);
            bindingExpression?.UpdateSource();
        }
    }

    /// <summary>
    /// Gets or sets the indentation width, in pixels, applied per tree level. Changing it at runtime re-indents
    /// the realized cells immediately; unrealized cells pick the value up when they bind.
    /// </summary>
    public double IndentWidth
    {
        get => (double)GetValue(IndentWidthProperty);
        set => SetValue(IndentWidthProperty, value);
    }

    /// <summary>
    /// Identifies the IndentWidth dependency property.
    /// </summary>
    public static readonly DependencyProperty IndentWidthProperty = DependencyProperty.Register(nameof(IndentWidth), typeof(double), typeof(TableViewTreeColumn), new PropertyMetadata(16d, OnIndentWidthChanged));

    /// <summary>
    /// Re-applies the indent to this column's realized cells so a live IndentWidth change takes effect immediately.
    /// </summary>
    private static void OnIndentWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TableViewTreeColumn { TableView: { } tableView } column)
        {
            return;
        }

        foreach (var row in tableView.Rows)
        {
            if (row.Content is not ITableViewTreeItem item)
            {
                continue;
            }

            foreach (var cell in row.Cells)
            {
                // The first Grid child is the indent spacer (see GenerateElement); editing cells (TextBox) skip.
                if (cell.Column == column && cell.Content is Grid grid
                    && grid.Children.Count > 0 && grid.Children[0] is Border indent)
                {
                    indent.Width = item.Depth > 0 ? item.Depth * column.IndentWidth : 0d;
                }
            }
        }
    }
}
