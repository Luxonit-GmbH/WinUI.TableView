using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.System;

namespace WinUI.TableView.Controls;

/// <summary>
/// Represents the control that displays filter items in the filter flyout of a TableViewColumnHeader.
/// </summary>
public partial class TableViewFilterItemsControl : UserControl
{
    private bool _canSetState = true;

    /// <summary>
    /// Initializes a new instance of the <see cref="TableViewFilterItemsControl"/> class.
    /// </summary>
    public TableViewFilterItemsControl()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Initializes the state of the <see cref="TableViewFilterItemsControl"/>.
    /// </summary>
    internal async void Initialize()
    {
        FilterItems = TableView?.FilterHandler?.GetFilterItems(ColumnHeader?.Column!, null).ToList();

        InitializeOperators();

        if (searchBox is not null)
        {
            await Task.Delay(100);
            await FocusManager.TryFocusAsync(searchBox, FocusState.Programmatic);
        }

        if (filterItemsList is not null && filterItemsList.Items.Count > 0)
        {
            filterItemsList.ScrollIntoView(filterItemsList.Items[0]);
        }
    }

    /// <summary>
    /// Clears the search box text.
    /// </summary>
    internal void ClearSearchBox()
    {
        searchBox?.Text = string.Empty;
    }

    /// <summary>
    /// Builds the operator list for the column and selects the default ("Is one of", the checkbox list). The list
    /// is type aware: text columns get the text operators, numeric and date columns get the comparisons.
    /// </summary>
    private void InitializeOperators()
    {
        if (operatorComboBox is null)
        {
            return;
        }

        if (TableView?.ShowFilterOperators is not true)
        {
            operatorComboBox.Visibility = Visibility.Collapsed;
            operatorValuePanel.Visibility = Visibility.Collapsed;
            return;
        }

        operatorComboBox.Visibility = Visibility.Visible;
        operatorComboBox.ItemsSource = OperatorOption.ForValueType(GetColumnValueType());
        operatorComboBox.SelectedIndex = 0; // SelectedValues — the classic checkbox behavior
        filterValueBox.Text = string.Empty;
        secondFilterValueBox.Text = string.Empty;
    }

    /// <summary>
    /// Infers the column's value type from the loaded filter items so the operator list can be tailored to it.
    /// </summary>
    private Type? GetColumnValueType()
        => FilterItems?.Select(item => item.Value).FirstOrDefault(value => value is not null)?.GetType();

    /// <summary>
    /// Switches the flyout between the checkbox list (SelectedValues) and the value input(s) used by the
    /// comparison operators.
    /// </summary>
    private void OnOperatorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var op = SelectedOperator;
        var usesList = op is TableViewFilterOperator.SelectedValues;
        var needsValue = op is not (TableViewFilterOperator.SelectedValues
            or TableViewFilterOperator.IsEmpty or TableViewFilterOperator.IsNotEmpty);

        operatorValuePanel.Visibility = needsValue ? Visibility.Visible : Visibility.Collapsed;
        secondFilterValueBox.Visibility = op is TableViewFilterOperator.Between ? Visibility.Visible : Visibility.Collapsed;

        searchBox.Visibility = usesList ? Visibility.Visible : Visibility.Collapsed;
        selectAllBorder.Visibility = usesList ? Visibility.Visible : Visibility.Collapsed;
        filterItemsList.Visibility = usesList ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Gets the operator currently selected in the flyout.
    /// </summary>
    internal TableViewFilterOperator SelectedOperator
        => operatorComboBox?.SelectedItem is OperatorOption option
            ? option.Operator
            : TableViewFilterOperator.SelectedValues;

    /// <summary>
    /// Builds the descriptor for the current flyout state: either the checkbox selection or the chosen operator
    /// with its value(s).
    /// </summary>
    /// <param name="column">The column being filtered.</param>
    /// <param name="selectedValues">The values ticked in the checkbox list.</param>
    internal TableViewFilterDescriptor BuildDescriptor(TableViewColumn column, ICollection<object?> selectedValues)
    {
        var op = SelectedOperator;

        return op is TableViewFilterOperator.SelectedValues
            ? new TableViewFilterDescriptor(column, op, selectedValues: selectedValues)
            : new TableViewFilterDescriptor(column, op, filterValueBox?.Text, secondFilterValueBox?.Text);
    }

    /// <summary>
    /// A selectable operator in the flyout's dropdown.
    /// </summary>
    internal sealed class OperatorOption(TableViewFilterOperator op, string displayName)
    {
        public TableViewFilterOperator Operator { get; } = op;

        public string DisplayName { get; } = displayName;

        /// <summary>
        /// The operators offered for a column whose values are of the given type. Text operators are meaningless
        /// for numbers and comparisons are rarely useful for text, so each type gets the relevant set.
        /// </summary>
        public static IList<OperatorOption> ForValueType(Type? valueType)
        {
            var underlying = valueType is null ? null : Nullable.GetUnderlyingType(valueType) ?? valueType;
            var isComparable = underlying is not null
                && underlying != typeof(string)
                && (underlying.IsPrimitive || underlying == typeof(decimal)
                    || underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset)
                    || underlying == typeof(TimeSpan));

            List<OperatorOption> options =
            [
                new(TableViewFilterOperator.SelectedValues, "Is one of"),
                new(TableViewFilterOperator.Equals, "Equals"),
                new(TableViewFilterOperator.NotEquals, "Does not equal"),
            ];

            if (isComparable)
            {
                options.Add(new(TableViewFilterOperator.GreaterThan, "Larger than"));
                options.Add(new(TableViewFilterOperator.GreaterThanOrEqual, "Larger than or equal"));
                options.Add(new(TableViewFilterOperator.LessThan, "Smaller than"));
                options.Add(new(TableViewFilterOperator.LessThanOrEqual, "Smaller than or equal"));
                options.Add(new(TableViewFilterOperator.Between, "Between"));
            }

            if (underlying is null || underlying == typeof(string))
            {
                options.Add(new(TableViewFilterOperator.Contains, "Contains"));
                options.Add(new(TableViewFilterOperator.NotContains, "Does not contain"));
                options.Add(new(TableViewFilterOperator.StartsWith, "Starts with"));
                options.Add(new(TableViewFilterOperator.EndsWith, "Ends with"));
            }

            options.Add(new(TableViewFilterOperator.IsEmpty, "Is empty"));
            options.Add(new(TableViewFilterOperator.IsNotEmpty, "Is not empty"));

            return options;
        }
    }

    private void OnSearchBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        FilterItems = TableView?.FilterHandler?.GetFilterItems(ColumnHeader?.Column!, searchBox!.Text);
    }

    /// <summary>
    /// Handles the KeyDown or PreviewKeyDown event for the searchBox.
    /// </summary>
    private void OnSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && searchBox?.Text.Length > 0)
        {
            ColumnHeader?.HideFlyout();
            ColumnHeader?.ApplyFilter();

            e.Handled = true;
        }
    }

    /// <summary>
    /// Handles the Checked and Unchecked event for the selectAllCheckBox.
    /// </summary>
    private void OnSelectAllCheckBoxCheckChanged(object sender, RoutedEventArgs e)
    {
        SetFilterItemsState(selectAllCheckBox.IsChecked is true);
    }

    /// <summary>
    /// Sets the state of the select all checkbox.
    /// </summary>
    internal void SetSelectAllCheckBoxState()
    {
        if (selectAllCheckBox is null || !_canSetState)
        {
            return;
        }


        selectAllCheckBox.IsChecked = FilterItems?.All(x => x.IsSelected) ?? false ? true
                                      : FilterItems?.All(x => !x.IsSelected) ?? false ? false
                                      : null;
    }

    /// <summary>
    /// Sets the state of the filter items.
    /// </summary>
    /// <param name="isSelected">The state to set.</param>
    internal void SetFilterItemsState(bool isSelected)
    {
        _canSetState = false;

        foreach (var item in filterItemsList.Items.OfType<TableViewFilterItem>())
        {
            item.IsSelected = isSelected;
        }

        _canSetState = true;
    }

    /// <summary>
    /// Attaches property changed handlers to the filter items.
    /// </summary>
    private void AttachPropertyChangedHandlers()
    {
        if (FilterItems?.Count > 0)
        {
            foreach (var item in FilterItems)
            {
                item.PropertyChanged += OnFilterItemPropertyChanged;
            }
        }
    }

    /// <summary>
    /// Detaches property changed handlers from the filter items.
    /// </summary>
    private void DetachPropertyChangedHandlers()
    {
        if (FilterItems?.Count > 0)
        {
            foreach (var item in FilterItems)
            {
                item.PropertyChanged -= OnFilterItemPropertyChanged;
            }
        }
    }

    /// <summary>
    /// Handles the PropertyChanged event for filter items.
    /// </summary>
    private void OnFilterItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        SetSelectAllCheckBoxState();
    }

    /// <summary>
    /// Gets a value indicating whether to apply the filter based on the control state.
    /// </summary>
    internal bool ShouldApplyFilter => SelectedOperator switch
    {
        // Operators that need no value always filter.
        TableViewFilterOperator.IsEmpty or TableViewFilterOperator.IsNotEmpty => true,

        // Comparison/text operators filter once a value has been typed.
        not TableViewFilterOperator.SelectedValues => !string.IsNullOrEmpty(filterValueBox?.Text),

        // Checkbox mode: unchanged behavior — filter unless everything is still selected.
        _ => selectAllCheckBox.IsChecked is not true || !string.IsNullOrEmpty(searchBox.Text),
    };

    /// <summary>
    /// Gets or sets the filter items for the control.
    /// </summary>
    internal ICollection<TableViewFilterItem>? FilterItems
    {
        get;
        set
        {
            if (field == value) return;

            DetachPropertyChangedHandlers();
            field = value;
            filterItemsList.ItemsSource = field;
            AttachPropertyChangedHandlers();
            SetSelectAllCheckBoxState();
        }
    }

    /// <summary>
    /// Gets or sets the column header associated with the filter items control.
    /// </summary>
    public TableViewColumnHeader? ColumnHeader
    {
        get;
        set
        {
            if (value is { FilterItemsControl: null })
            {
                value.FilterItemsControl = this;
            }

            field = value;
        }
    }

    /// <summary>
    /// Gets or sets the TableView associated with the filter items control.
    /// </summary>
    public TableView? TableView { get; internal set; }
}
