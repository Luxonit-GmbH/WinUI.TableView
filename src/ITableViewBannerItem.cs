namespace WinUI.TableView;

/// <summary>
/// Marks an item that occupies a row but is not a data row: it renders as a single full-width banner instead of
/// cells — a group header, a section divider, an empty-state message.
/// </summary>
/// <remarks>
/// <para>A banner row cannot be selected or edited, is skipped by keyboard navigation, and is excluded from
/// select-all, copy and export. That exclusion is enforced in one place
/// (<see cref="TableView.IsSelectableItem"/>) rather than scattered through each entry point.</para>
/// <para>Row grouping produces these for its group headers, but nothing here is specific to grouping — any item
/// may implement it.</para>
/// </remarks>
public interface ITableViewBannerItem
{
    /// <summary>
    /// Gets the content shown across the full width of the row. When <see langword="null"/> the item itself is
    /// used, so a model with a good ToString or a matching template needs nothing here.
    /// </summary>
    object? BannerContent { get; }
}
