# Custom columns without the performance bottlenecks

How to write a column type whose cells stay cheap at 80 columns, tens of thousands of rows and thousands of
property updates a second — while still showing a placeholder for null, offering a different editor per row, and
keeping read-only cells rich. Everything here follows from what a cell actually costs in this grid, so that comes
first.

## What a cell costs, and when

A `TableViewCell` hosts one element, produced by your column's `GenerateElement`. Five moments cost something:

| moment | what happens | who pays |
|---|---|---|
| **creation** | `GenerateElement` runs, once per cell, when the column first scrolls into the realized band (or at idle, via prefetch) | your constructor |
| **first measure** | a `Control` applies its template and builds its visual tree here — `ApplyTemplate` is *on the first Measure*, not in the constructor | the template |
| **recycle** | the row is reused for another item: the row's `DataContext` changes and every binding on every cell re-evaluates. Bound columns pay only that. `TableViewTemplateColumn.RefreshElement` **regenerates the whole `ContentControl`** — creation and first measure again, per recycle | the column type |
| **data tick** | the item raises `PropertyChanged`; only the bindings on that property re-evaluate; if a bound property affects size, the cell re-measures | your binding fan-out |
| **layout pass** | the cell's `MeasureOverride` constrains the content to the column width and row height. This is cached by (element, column width, row height); an unchanged cell costs nothing. Cells outside the horizontal band are collapsed and skipped entirely | mostly nothing, unless `Width="Auto"` |

Two mechanisms hold that together and shape the rules below. Cells outside the visible band have their content's
`DataContext` **pinned** so a recycle underneath them re-evaluates nothing, and an idle **prefetch** creates and
measures the next band's content before a scroll reveals it. Both assume the standard shape: a bound element that
inherits `DataContext` from the row and binds through the column's `Binding`.

## The rules

**1. Derive from `TableViewBoundColumn`, not `TableViewTemplateColumn`, for any column that is hot.** A bound
column's element is created once and survives recycles; a template column's is rebuilt on every recycle. Templates
are fine for a column that is rarely on screen or never scrolls. For the price and risk columns of a blotter they
are the single largest cost you can remove.

**2. The display element is a lightweight tree built in code — not a `UserControl`, not a templated `Control`.**
`InitializeComponent` per instance is what the app's profiler kept finding, and it runs per cell: 80 columns × every
realized row. Build the tree directly in `GenerateElement`: a `TextBlock` where one will do; a `Grid` or `Border`
with a few `TextBlock`s when it will not. No `Style`-driven `Control` with a `ControlTemplate` for a cell unless you
have measured it. If you need a reusable "look", make it a static factory method that builds the tree, not a
control that applies a template.

**3. Bind through the column's `Binding`; let `DataContext` inherit.** Do not set `DataContext` on the element and
do not hand the item to the constructor. Inheritance is what makes recycling free, and what the pinning and
prefetch machinery relies on. (A `Binding` set on `DataContextProperty` itself is respected by the pinning code,
but avoid it unless the column genuinely rebases its context, as the ComboBox column does.)

**4. Null and "special" display strings belong in the binding, not in an extra element.**

```csharp
textBlock.SetBinding(TextBlock.TextProperty, new Binding
{
    Path = Binding.Path,
    Mode = BindingMode.OneWay,
    TargetNullValue = "—",          // shown when the value is null
    FallbackValue = "n/a",          // shown when the path cannot resolve
    Converter = PriceFormatter,     // one shared, allocation-free instance
});
```

That costs nothing beyond the binding you already have. What you must NOT do is let the display string leak into
operations: set `OperationContentBinding` (sort, filter, export) and `ClipboardContentBinding` to the *raw* value,
so "—" is never sorted as text and `null` copies as empty. The bound column falls back to `Binding` for
`OperationContentBinding` when you set nothing — fine when the display binding has no converter, wrong once it does.

**5. Editors are separate elements, created only when an edit begins.** Override `GenerateEditingElement` to
return the editor; the cell swaps it in on edit and discards it afterwards. The display element stays a `TextBlock`;
the `NumberBox`, `ComboBox` or `DatePicker` exists only while someone is typing. Different editors per row are
therefore free — branch on the item inside `GenerateEditingElement`, it runs once per edit, not once per cell.

Bindings on a bound column are forced to `TwoWay` with `UpdateSourceTrigger.Explicit`, so a keystroke never writes
to the item; `EndCellEditing` commits (or restores on cancel). Override it only if your editor is not a plain
bound dependency property.

**6. `UseSingleElement` is for elements that are their own editor.** A `CheckBox` or a `ToggleSwitch` reads and
writes through one element, and the cell then begins editing on pointer press. Do not use it for text: a `TextBox`
as the display element costs a templated `Control` per cell (rule 2) and takes focus and hit-testing paths a
`TextBlock` never enters.

**7. Read-only stays rich, per column or per row.** `IsReadOnly` on the column blocks editing outright. For
per-row read-only, decide in `GenerateEditingElement`: return the display element (`GenerateElement`) for items
that must not be edited and an editor for the rest. Nothing is swapped, nothing is committed, the rich display stays
exactly as it was. That keeps the decision where it is cheapest — once per edit attempt — instead of in a per-cell
state.

**8. Conditional styling runs per cell per recycle.** `ConditionalCellStyles` predicates are evaluated for every
cell each time its row is reused. Keep them O(1) and allocation-free. For anything that changes on a data tick —
a colour that follows the sign of a change, a flash on update — bind the visual property with a shared converter
instead; a `Style` swap re-applies setters, a bound `Foreground` changes one brush.

**9. Fix the widths.** `Width="Auto"` measures every cell's content unconstrained on every pass to find the
column's desired width. Use pixel or star widths; use `AutoSizeMinWidth` if the first render should size the
column and then leave it alone. Set `RowHeight` on the grid: with it the cell's constraint cache is fully
effective, and the cells panel takes the row height as known instead of reading every child's desired height on
every measure pass — at 80 columns that is 80 interop reads per row per pass it no longer does.

**10. Keep the tick path narrow.** At thousands of updates a second per visible row, each bound property is a
binding re-evaluation. Bind the two or three properties the cell shows; do not bind the whole item and format it in
a converter that touches ten properties, and do not change anything that affects layout — text length changes are
unavoidable, animations and size changes are not. `RenderTransform` and `Opacity` do not trigger layout;
`Margin`, `FontSize` and `Visibility` do.

## A reference column

A price column that shows a dash for null, colours by sign on every tick without touching layout, sorts and
copies the raw number, edits in a `NumberBox` for items that allow it and stays read-only otherwise.

```csharp
public sealed partial class PriceColumn : TableViewBoundColumn
{
    private static readonly SignBrushConverter SignBrush = new();      // shared: converters must not allocate
    private static readonly PriceConverter PriceText = new();

    public PriceColumn()
    {
        // Sort, filter and export on the number, never on the "—" the display shows for null.
        OperationContentBinding = new Binding { Path = Binding.Path };
    }

    public override FrameworkElement GenerateElement(TableViewCell cell, object? dataItem)
    {
        // A TextBlock is the whole cell. No UserControl, no template, no DataContext of its own.
        var text = new TextBlock { Margin = new Thickness(12, 0, 12, 0), TextAlignment = TextAlignment.Right };

        text.SetBinding(TextBlock.TextProperty, new Binding
        {
            Path = Binding.Path, Mode = BindingMode.OneWay, Converter = PriceText, TargetNullValue = "—",
        });

        // Colour follows the value on every tick: one brush swap, no layout.
        text.SetBinding(TextBlock.ForegroundProperty, new Binding
        {
            Path = Binding.Path, Mode = BindingMode.OneWay, Converter = SignBrush,
        });

        return text;
    }

    public override FrameworkElement GenerateEditingElement(TableViewCell cell, object? dataItem)
    {
        // Per-row read-only: hand back the display and nothing is swapped or committed.
        if (dataItem is IEditable { CanEditPrice: false })
        {
            return GenerateElement(cell, dataItem);
        }

        // Created only now, for this edit, and thrown away afterwards. Binding is TwoWay + Explicit already, so
        // typing never writes through; EndCellEditing commits.
        var box = new NumberBox { SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        box.SetBinding(NumberBox.ValueProperty, Binding);
        return box;
    }

    protected internal override object? PrepareCellForEdit(TableViewCell cell, RoutedEventArgs routedEvent)
        => cell.Content is NumberBox box ? box.Value : base.PrepareCellForEdit(cell, routedEvent);
}
```

Copy, paste and export take the number because `OperationContentBinding` says so; the grid never sees the dash
as a value. The display element is exactly one `TextBlock` with two bindings, which is what the prefetch pump
creates at idle and the pinning holds across recycles.

## What to measure, not assume

`tests/PerformanceBenchmarks.cs` has the harness. `Grid_MutationStorm_8000Updates_VisibleRows` is the tick path;
`Grid_HorizontalPan_80Cols_FirstScroll_HeavyCells_*` is the first-reveal cost with a constructor-built cell —
swap in your own column type there before trusting any of the above for it. Run each benchmark in its own test
host (`/TestCaseFilter:"Name=..."`); arms measured in sequence in one host are not comparable, whichever runs
first wins.
