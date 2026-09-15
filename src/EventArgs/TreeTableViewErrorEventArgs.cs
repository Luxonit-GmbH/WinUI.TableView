using System;

namespace WinUI.TableView;

/// <summary>
/// Provides data for the <see cref="TreeTableView.Error"/> event, raised when the control's own expand or
/// collapse of a <see cref="TreeTableViewSource"/> fails — malformed data such as a repeated item instance, a
/// cycle, or a chain past <see cref="TreeTableViewSource.MaxDepth"/>.
/// </summary>
/// <remarks>
/// The adapter validates before it mutates, so a failure leaves the tree exactly as it was: handling the error
/// is safe, and the grid stays usable. Leave <see cref="Handled"/> alone and the exception is rethrown, which is
/// the default so a bug cannot pass silently.
/// </remarks>
public partial class TreeTableViewErrorEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the TreeTableViewErrorEventArgs class.
    /// </summary>
    /// <param name="exception">The exception the operation failed with.</param>
    /// <param name="item">The tree item the operation was for.</param>
    /// <param name="expanding"><see langword="true"/> when the failed operation was an expand.</param>
    public TreeTableViewErrorEventArgs(Exception exception, ITableViewTreeItem item, bool expanding)
    {
        Exception = exception;
        Item = item;
        Expanding = expanding;
    }

    /// <summary>
    /// Gets the exception the operation failed with. Its message names the offending item and what was wrong.
    /// </summary>
    public Exception Exception { get; }

    /// <summary>
    /// Gets the tree item the operation was for.
    /// </summary>
    public ITableViewTreeItem Item { get; }

    /// <summary>
    /// Gets whether the failed operation was an expand (otherwise a collapse).
    /// </summary>
    public bool Expanding { get; }

    /// <summary>
    /// Gets or sets whether the error has been dealt with. Set to <see langword="true"/> to swallow it — log it,
    /// show a message, drop the bad branch — and keep the app running. Left <see langword="false"/> the exception
    /// is rethrown, so an unhandled data bug still surfaces rather than disappearing.
    /// </summary>
    public bool Handled { get; set; }
}
