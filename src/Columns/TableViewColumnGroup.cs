using Microsoft.UI.Xaml;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WinUI.TableView;

/// <summary>
/// A banner above a run of columns — the second header level. Columns join a group by matching
/// <see cref="TableViewColumn.GroupName"/> to <see cref="Name"/>.
/// </summary>
/// <remarks>
/// <para>A group's columns must be contiguous in <see cref="TableViewColumn.Order"/>, since a banner spans one
/// run of columns and cannot be split.</para>
/// <para>Collapsing hides every member except <see cref="CollapsedColumn"/>, so the banner keeps a visible anchor
/// to click on. Because column visibility is what the whole grid derives its layout from, collapsing needs no
/// special handling anywhere else.</para>
/// </remarks>
public partial class TableViewColumnGroup : DependencyObject, INotifyPropertyChanged
{
    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets or sets the group's key, matched against <see cref="TableViewColumn.GroupName"/>.
    /// </summary>
    public string? Name
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>
    /// Gets or sets the banner content. Falls back to <see cref="Name"/> when unset.
    /// </summary>
    public object? Header
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>
    /// Gets or sets the template for <see cref="Header"/>.
    /// </summary>
    public DataTemplate? HeaderTemplate
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>
    /// Gets or sets whether the user may collapse the group. Defaults to <see langword="true"/>.
    /// </summary>
    public bool IsCollapsible
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>
    /// Gets or sets whether the group is collapsed to its <see cref="CollapsedColumn"/>.
    /// </summary>
    public bool IsCollapsed
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>
    /// Gets or sets the member that stays visible while collapsed. Defaults to the group's first column.
    /// </summary>
    /// <remarks>
    /// Hiding every member would leave a zero-width banner with nothing to click to expand it again, so one
    /// column always survives a collapse.
    /// </remarks>
    public TableViewColumn? CollapsedColumn
    {
        get;
        set => Set(ref field, value);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
