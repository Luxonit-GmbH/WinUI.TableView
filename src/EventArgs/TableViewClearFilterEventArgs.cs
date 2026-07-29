using System.ComponentModel;

namespace WinUI.TableView;

/// <summary>
/// Provides data for the event raised when a column filter is being cleared in a TableView — the filtering
/// counterpart of <see cref="TableViewClearSortingEventArgs"/>.
/// </summary>
/// <remarks>
/// Set <see cref="HandledEventArgs.Handled"/> to take over (the built-in filter state is then left untouched) and
/// clear the filter in the application layer.
/// </remarks>
public partial class TableViewClearFilterEventArgs : HandledEventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TableViewClearFilterEventArgs"/> class.
    /// </summary>
    /// <param name="column">The column whose filter is being cleared, or <see langword="null"/> to clear all.</param>
    public TableViewClearFilterEventArgs(TableViewColumn? column)
    {
        Column = column;
    }

    /// <summary>
    /// Gets the column whose filter is being cleared, or <see langword="null"/> when every filter is cleared.
    /// </summary>
    public TableViewColumn? Column { get; }
}
