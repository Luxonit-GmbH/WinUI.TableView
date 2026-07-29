namespace WinUI.TableView;

/// <summary>
/// The comparison used by a <see cref="TableViewFilterDescriptor"/>.
/// </summary>
public enum TableViewFilterOperator
{
    /// <summary>The classic checkbox list: the cell value must be one of the selected values.</summary>
    SelectedValues,

    /// <summary>The cell value equals the filter value.</summary>
    Equals,

    /// <summary>The cell value does not equal the filter value.</summary>
    NotEquals,

    /// <summary>The cell text contains the filter text.</summary>
    Contains,

    /// <summary>The cell text does not contain the filter text.</summary>
    NotContains,

    /// <summary>The cell text starts with the filter text.</summary>
    StartsWith,

    /// <summary>The cell text ends with the filter text.</summary>
    EndsWith,

    /// <summary>The cell value is greater than the filter value.</summary>
    GreaterThan,

    /// <summary>The cell value is greater than or equal to the filter value.</summary>
    GreaterThanOrEqual,

    /// <summary>The cell value is less than the filter value.</summary>
    LessThan,

    /// <summary>The cell value is less than or equal to the filter value.</summary>
    LessThanOrEqual,

    /// <summary>The cell value lies between the filter value and the second filter value (inclusive).</summary>
    Between,

    /// <summary>The cell value is null, empty or whitespace.</summary>
    IsEmpty,

    /// <summary>The cell value is not null, empty or whitespace.</summary>
    IsNotEmpty,
}
