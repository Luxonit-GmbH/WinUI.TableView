# Performance guidance

`TableView` is built on `ListView`, which provides UI virtualization out of the box. This means only the rows currently visible on screen are instantiated. The following guidance helps you get the best performance when working with large datasets.

## Row virtualization

Row virtualization is always active. `TableView` does not render all items at once; only the rows in the visible viewport (plus a small buffer) are created. When the user scrolls, rows are recycled.

You do not need to do anything to enable virtualization — it is the default behavior inherited from `ListView`.

## Collection type

For the best performance with large collections:

- Use `List<T>` or `ObservableCollection<T>` as the source type.
- Avoid `IQueryable<T>` sources that trigger database queries for every property access.
- If your collection has tens of thousands of items, consider loading data in pages and using a virtualized source.

## INotifyPropertyChanged

Implement `INotifyPropertyChanged` on your model to ensure only cells whose data has changed are re-rendered. Without it, the control cannot detect property changes and may not update cell values.

Mark the item type `partial` and add `[WinRT.GeneratedBindableCustomProperty]` to it. A `{Binding}` to a plain .NET class evaluates through reflection and boxing on every update; the attribute has CsWinRT generate the property provider at build time instead. Measured on a 4K grid fed 8,000 updates a second, it took about 15% off the UI thread's cost per update, and it applies to bindings set in code as much as in XAML. The remaining cost of an update is the platform laying out and drawing the cell's text, which no binding change reaches.

## Live shaping

Live shaping re-evaluates sort and filter criteria when item properties change. This is convenient but has a cost on large collections:

```csharp
// Disable unless you need items to resort/refilter automatically (AllowLiveShaping is enabled by default)
tableView.AllowLiveShaping = false;
```

Enable it only when users expect items to move or disappear in real time after edits.

## Auto-generated columns

[`AutoGenerateColumns`](xref:WinUI.TableView.TableView.AutoGenerateColumns) uses reflection to inspect the item type. For types with many properties, or in hot-path scenarios, prefer explicit columns to avoid reflection overhead:

```xml
<tv:TableView AutoGenerateColumns="False">
    <tv:TableView.Columns>
        <!-- Explicit columns -->
    </tv:TableView.Columns>
</tv:TableView>
```

## Conditional cell styles

Conditional style predicates are called for every rendered cell during layout passes. Keep predicates fast:

```csharp
// Good: simple property check
ctx.DataItem is Product p && p.Stock < 10

// Avoid: LINQ or string operations inside the predicate on a hot path
ctx.DataItem is Product p && p.Tags.Any(t => t.StartsWith("clearance"))
```

## Column auto-width

[`ColumnAutoWidthMode`](xref:WinUI.TableView.TableView.ColumnAutoWidthMode) measures cell content to determine the column width. On large virtualized lists, only visible cells are measured. This means the initial auto-width may be narrower than the actual maximum value width. If accuracy matters, consider using a fixed or star width instead.

## Filtering and sorting

Filtering and sorting operate on the internal collection view. These run on the UI thread. For very large collections (100,000+ items), consider pre-filtering in your ViewModel before setting [`ItemsSource`](xref:WinUI.TableView.TableView.ItemsSource).

## Refreshing the view after bulk data changes

When you modify items in your source collection in-place (e.g., changing a property without `INotifyPropertyChanged`, or replacing items in a `List<T>`) the view may not update automatically. Use the refresh methods to force the control to re-evaluate:

| Method | Description |
|---|---|
| [`RefreshView()`](xref:WinUI.TableView.TableView.RefreshView) | Re-renders the items view; use after bulk data changes |
| [`RefreshSorting()`](xref:WinUI.TableView.TableView.RefreshSorting) | Re-applies active sort descriptions without user interaction |
| [`RefreshFilter()`](xref:WinUI.TableView.TableView.RefreshFilter) | Re-evaluates active filter descriptions without user interaction |

```csharp
// After modifying items in bulk outside of ObservableCollection:
foreach (var item in products)
{
    item.Price *= 0.9; // Apply a discount
}
tableView.RefreshView();

// Re-apply sort after external data update:
tableView.RefreshSorting();

// Re-run filter after external data update:
tableView.RefreshFilter();
```

> **Tip**: Prefer `ObservableCollection<T>` with `INotifyPropertyChanged` models over manual refresh calls whenever possible, as it is more efficient and requires less code.

## Column resize drag performance

By default, dragging a column divider ([`ColumnResizeMode="Live"`](xref:WinUI.TableView.TableView.ColumnResizeMode)) relayouts every visible row's cells on every pointer-move frame. On grids with many visible rows this can make the drag itself feel less smooth, even though the final committed width is unaffected. Set `ColumnResizeMode="Preview"` to use a lightweight visual preview during the drag instead — no row layout runs until the pointer is released, so the drag stays smooth regardless of row count. See [Column sizing](column-sizing.md#columnresizemode).

## Row height

Set `RowHeight` on a grid that scrolls a lot. With a fixed row height the grid finds the rows on screen arithmetically instead of by walking the visual tree, and each cell's content constraint stays the same from one scroll to the next, so the cells are not re-measured.

`RowHeight` wins over `RowMinHeight`. The default minimum is 40, and a `RowHeight` below it, say 28, gives 28px rows: the explicit height is the more specific setting, so it caps the minimum. Leave `RowHeight` unset and the minimum is the row height.

## Fast vertical scrolling

A scroll that moves the view by more than a viewport in one step — a scrollbar throw, a jump to the end — recycles every row on screen at once, and binding every cell of every one of those rows is far more than a frame of work on a wide grid. Rows recycled by such a scroll are held: their cells stay on the item they showed and are hidden, and the platform's phased rendering releases them as it finds budget, the way most blotters and spreadsheets behave. On a machine that keeps up that is the next frame or two, so little or no blanking shows; on one that does not, rows fill in as the budget allows, and a row recycled again before its turn is never bound for nothing. Ordinary scrolling, by wheel, keyboard or a slow drag, binds rows at once as before.

So that the user can still tell where they are, some columns stay live through such a scroll: frozen columns always, any column with `KeepLiveDuringFastScroll` set (an identifier or a name), and the leftmost `FastScrollLiveColumnCount` visible scrollable columns (default 1; set 0 to rely on the flagged and frozen ones). Each live column costs one cell binding per recycled row per frame.

## Horizontal scrolling and column count

Set `IsColumnVirtualizationEnabled` to `true` on a grid with many columns. Cells outside a band around the visible columns are collapsed, so they are never measured, and cells further out have their content released entirely, so they stop evaluating their bindings. Without it every column of every realized row is built, measured and kept live — which on a grid with a frequently updating source means the columns you cannot see cost as much as the ones you can.

Two properties tune the band, both multiples of the viewport width and both capped internally in columns so that a grid of many narrow columns does not end up realizing most of itself:

- `ColumnCacheLength` (default `0.5`) sizes the realized band either side of the visible columns. Raise it to avoid blank columns during a fast drag; lower it to make each band change cheaper.
- `ColumnPrefetchLength` (default `1`) sizes a margin beyond the band whose content is built during idle time and left collapsed, so the first scroll into it reveals content rather than generating it. Set it to `0` to turn idle prefetch off.

Column headers are not virtualized: one is instantiated per column whether or not it is visible. That is a fixed cost at load, not a per-scroll one.

## Uno Platform

On Uno Platform targets, data binding and layout passes may have slightly different performance characteristics than on the Windows target. Test performance on each target platform before shipping.

## Notes

- `TableView` supports incremental loading when the items source implements `ISupportIncrementalLoading`. See [Incremental loading](incremental-loading.md).
- The `CellsHorizontalOffset` property (default `16`) adds padding to the left of the cells area. This is separate from column widths.

## Related articles

- [Binding data](binding-data.md)
- [Filtering](filtering.md)
- [Sorting](sorting.md)
- [Conditional cell styling](conditional-styling.md)
