using System.ComponentModel;

namespace WinUI.TableView;

/// <summary>
/// Provides data for the event raised when a column filter is being applied in a TableView — the filtering
/// counterpart of <see cref="TableViewSortingEventArgs"/>.
/// </summary>
/// <remarks>
/// Set <see cref="HandledEventArgs.Handled"/> to take over filtering entirely (the built-in filter is then not
/// applied) and filter the data in the application layer using <see cref="Descriptor"/>.
/// </remarks>
public partial class TableViewFilteringEventArgs : HandledEventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TableViewFilteringEventArgs"/> class.
    /// </summary>
    /// <param name="descriptor">The filter being applied.</param>
    public TableViewFilteringEventArgs(TableViewFilterDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    /// <summary>
    /// Gets the filter being applied: the column, the operator and the value(s).
    /// </summary>
    public TableViewFilterDescriptor Descriptor { get; }

    /// <summary>
    /// Gets the column being filtered.
    /// </summary>
    public TableViewColumn Column => Descriptor.Column;
}
