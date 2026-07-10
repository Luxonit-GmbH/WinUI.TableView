using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;
using WinUI.TableView.Columns;

namespace WinUI.TableView;

/// <summary>
/// The hierarchy column of a <see cref="TreeTableView"/>: renders per-item indentation, an expander chevron and the
/// bound text for items implementing <see cref="ITableViewTreeItem"/>.
/// </summary>
/// <remarks>
/// The generated element is deliberately lightweight (indent spacer + chevron button + TextBlock) and fully
/// binding-driven, so container recycling and item mutations update it without column code. The chevron slot is
/// always reserved so leaf rows align with expandable siblings at the same depth. Chevron clicks (and Left/Right
/// keys, handled by <see cref="TreeTableView"/>) only raise
/// <see cref="TreeTableView.ExpandRequested"/>/<see cref="TreeTableView.CollapseRequested"/> — the flat items source
/// performs the actual expansion. For runtime updates, items should raise
/// <see cref="System.ComponentModel.INotifyPropertyChanged"/> for the <see cref="ITableViewTreeItem"/> properties.
/// </remarks>
[StyleTypedProperty(Property = nameof(ElementStyle), StyleTargetType = typeof(TextBlock))]
[StyleTypedProperty(Property = nameof(EditingElementStyle), StyleTargetType = typeof(TextBox))]
#if WINDOWS
[WinRT.GeneratedBindableCustomProperty]
#endif
public partial class TableViewTreeColumn : TableViewBoundColumn
{
    private const double ChevronSlotWidth = 20d;

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
        indent.SetBinding(FrameworkElement.WidthProperty, new Binding
        {
            Path = new PropertyPath(nameof(ITableViewTreeItem.Depth)),
            Converter = new DepthToIndentConverter(this),
        });

        var glyph = new FontIcon
        {
            Glyph = CollapsedGlyph,
            FontSize = 12,
        };
        glyph.SetBinding(FontIcon.GlyphProperty, new Binding
        {
            Path = new PropertyPath(nameof(ITableViewTreeItem.IsExpanded)),
            Converter = new ExpandedToGlyphConverter(),
        });
        glyph.SetBinding(UIElement.VisibilityProperty, new Binding
        {
            Path = new PropertyPath(nameof(ITableViewTreeItem.HasChildren)),
            Converter = new BooleanToVisibilityConverter(),
        });

        // Async expansion: while the source loads children (IsLoading), a small ring replaces the chevron glyph.
        // Requests are ignored during the load (TreeTableView.RequestExpandCollapse gates on IsLoading).
        var loadingRing = new ProgressRing
        {
            Width = 14,
            Height = 14,
            IsActive = false,
        };
        loadingRing.SetBinding(ProgressRing.IsActiveProperty, new Binding
        {
            Path = new PropertyPath(nameof(ITableViewTreeItem.IsLoading)),
        });
        loadingRing.SetBinding(UIElement.VisibilityProperty, new Binding
        {
            Path = new PropertyPath(nameof(ITableViewTreeItem.IsLoading)),
            Converter = new BooleanToVisibilityConverter(),
        });
        glyph.SetBinding(UIElement.OpacityProperty, new Binding
        {
            Path = new PropertyPath(nameof(ITableViewTreeItem.IsLoading)),
            Converter = new LoadingToOpacityConverter(),
        });

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
        chevron.SetBinding(UIElement.IsHitTestVisibleProperty, new Binding
        {
            Path = new PropertyPath(nameof(ITableViewTreeItem.HasChildren)),
        });
        chevron.Click += (_, _) =>
        {
            // The cell is stable while its element is recycled across items, so resolve item and index at click time.
            if (cell.TableView is TreeTableView treeTableView && cell.Row is { Content: ITableViewTreeItem item } row)
            {
                treeTableView.RequestExpandCollapse(item, row.Index, !item.IsExpanded);
            }
        };
        Grid.SetColumn(chevron, 1);

        var textBlock = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (Binding is not null)
        {
            textBlock.SetBinding(TextBlock.TextProperty, Binding);
        }
        Grid.SetColumn(textBlock, 2);

        root.Children.Add(indent);
        root.Children.Add(chevron);
        root.Children.Add(textBlock);

        return root;
    }

    /// <summary>
    /// Generates a TextBox for editing the bound text, matching <see cref="TableViewTextColumn"/> behavior.
    /// </summary>
    /// <param name="cell">The cell for which the editing element is generated.</param>
    /// <param name="dataItem">The data item associated with the cell.</param>
    /// <returns>A TextBox element.</returns>
    public override FrameworkElement GenerateEditingElement(TableViewCell cell, object? dataItem)
    {
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
    /// Re-applies the indent to this column's realized cells: the cell bindings only evaluate on (re)bind, so a
    /// live IndentWidth change must push the new width into the existing indent spacers itself.
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

    private const string CollapsedGlyph = ""; // ChevronRight
    private const string ExpandedGlyph = "";  // ChevronDown

    /// <summary>
    /// Converts an item's depth to the indent spacer width using the owning column's <see cref="IndentWidth"/>.
    /// </summary>
    private sealed partial class DepthToIndentConverter(TableViewTreeColumn column) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is int depth && depth > 0 ? depth * column.IndentWidth : 0d;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Converts <see cref="ITableViewTreeItem.IsExpanded"/> to the chevron glyph.
    /// </summary>
    private sealed partial class ExpandedToGlyphConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is true ? ExpandedGlyph : CollapsedGlyph;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Converts <see cref="ITableViewTreeItem.HasChildren"/> to chevron glyph visibility. The chevron slot itself is
    /// always reserved so leaves align with expandable siblings.
    /// </summary>
    private sealed partial class BooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is true ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Hides the chevron glyph (opacity, so layout is stable) while the loading ring is shown.
    /// </summary>
    private sealed partial class LoadingToOpacityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is true ? 0d : 1d;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
