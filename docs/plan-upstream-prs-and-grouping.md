# Plan: upstream PRs 341 / 340 / 251, and grouping

Assessed 2026-08-03 against `lux-fork-changes` (0 behind upstream/main, 83 ahead).

## Verdicts

| PR | Subject | State upstream | Verdict |
|---|---|---|---|
| [#341](https://github.com/w-ahmad/WinUI.TableView/pull/341) | Hierarchy (tree) support | Open, conflicting | **Do not merge** — harvest ideas |
| [#340](https://github.com/w-ahmad/WinUI.TableView/pull/340) | Data grouping | Open, conflicting, architecture rejected | **Do not merge** — harvest ideas |
| [#251](https://github.com/w-ahmad/WinUI.TableView/pull/251) | ListView-like hotkeys | Open, mergeable | **Reimplement**, do not cherry-pick |

None are merged upstream. Merging any of them means owning an unreviewed divergence.

### #341 — hierarchy

Architecturally the inverse of ours: it re-flattens the entire tree into an `ObservableCollection` on every
mutation and resolves children by reflection over property paths. That is a non-starter at 8000 mutations/sec,
and the reflection is AOT-hostile. Its base blob is 44 upstream commits stale — roughly 1,070 lines behind
upstream main before our own divergence counts.

Ours is better where it matters. Cycles (`P -> C -> P`) throw a diagnostic exception with full rollback and no
recursion blow-up, verified by `CircularChildren_ThrowCleanly_RatherThanRecursingForever`. Their path guard
instead lets a shared DAG node render twice at the wrong indent, silently.

Four things they have that we do not — see the backlog below.

### #340 — grouping

Two facts decide it:

1. **It is row grouping only.** No spanner headers, no multi-level column headers, no column-group model. None
   of the header-layout files are touched. It is also single-level: `GroupByPath` is one string, so no nested
   groups. Roughly half of what we need, and not the half that is hard.
2. **Upstream rejected the architecture.** The maintainer asked why it did not use `CollectionViewSource`, then
   built grouping himself on `CollectionView.CollectionGroups` with SemanticZoom support; the author agreed to
   abandon her approach. Taking it guarantees a second, worse conflict when the real feature lands.

It would also be inert for us: all of it reads `_collectionView`, so grouping is silently unavailable whenever
`UseCollectionView` is false — every `TreeTableView` and the direct-binding mode the blotter runs on. Its display
list is a second materialized collection rebuilt `Clear`+`Add` on every change, with a full O(n) rebuild and a
scroll reset per group toggle.

Note for later: the maintainer's preferred `CollectionGroups` route is itself blocked on a WinUI platform bug
([microsoft-ui-xaml#11085](https://github.com/microsoft/microsoft-ui-xaml/issues/11085)) — do not assume
upstream's replacement drops in either.

### #251 — hotkeys

Mechanically cheap: measured with `git apply --check`, four of five hunks apply with line offsets only, one
mechanical conflict because we moved `HandleNavigations`, plus a missing `using Microsoft.UI.Xaml.Media;` that
breaks the build. Roughly ten minutes of merge work.

The cost is behavioural, and three items are disqualifying as-is:

- It calls `base.OnKeyDown` — the first such call in this control — putting ListViewBase's selection engine
  alongside our `MakeSelection` / `SelectRows` / `LastSelectionUnit` state machine and bypassing all of it.
- It never sets `e.Handled` on the navigation path, so unconsumed keys escape to the host page.
- Home/End flip from column semantics to row semantics. In a 50-column blotter that costs "End = last column"
  and makes End scroll to the last row instead. The PR does not mention this.

Worth keeping from it: `ToggleCurrentRowSelection` works through `SelectedRanges` / `SelectRange`, never
`SelectedItems` — exactly the `ISelectionInfo` discipline our tree needs.

## Backlog

Ordered by risk-adjusted value.

### 1. Bound recursion depth in the tree adapter — **DONE 2026-08-03**

Shipped as `TreeTableViewSource.MaxDepth` (default 256). Exceeding it throws a diagnostic
`InvalidOperationException` before anything is spliced, consistent with the duplicate-item guard.

Depth is measured **from the root**, not from the subtree being inserted. That distinction is the whole point:
a tree deepened one `Expand` at a time only ever walks one level per operation, so a per-subtree guard would
never fire — and the eventual *collapse*, which recurses the whole expanded chain, is what overflows the stack.
Root-relative depth is computed by walking parent pointers (bounded by `MaxDepth`, once per structural
operation) rather than caching a field that would have to be maintained on the streaming hot path.

Because nothing can be inserted past the cap, teardown depth is bounded by the same number for free — so
`DropBranchesRecursive` needed no change.

Verified by A/B: replacing root-relative depth with a naive per-subtree count makes
`DepthIsMeasuredFromTheROOT_NotFromTheInsertedSubtree` fail while the other depth tests still pass. Benchmarks
unchanged (streaming 2000 inserts 5.1ms, 500k-branch 70.7ms).

**Follow-up, also done:** `TreeTableView.Error` closes the crash path. When the control's own expand or collapse
fails on malformed data it raises `TreeTableViewErrorEventArgs` (Exception, Item, Expanding, Handled) instead of
letting the throw escape a chevron click. Set `Handled` to log and carry on; leave it and the exception still
propagates, so an unhandled data bug cannot pass silently. Safe to handle because the adapter validates before
it mutates — the tree is untouched and the grid stays usable. Not raised for mutations the app makes to its own
children collections; those throw back to the caller, where the app can catch them directly.

### 2. Row grouping over `TreeTableViewSource` — 3–5 days

Do not port #340. Build grouping as a projection over the adapter we already have: a group is a parent node
whose children are its members. This reuses the treap index math, the bulk-change coalescing, `ISelectionInfo`
and `IItemsRangeInfo`, works in direct-binding mode, and composes with trees rather than competing with them.

Design points to settle before coding:

- **Re-keying.** Items mutate in place at 8000/sec, so an item's group key can change. Live re-keying needs
  per-item property subscriptions — the cost `AllowLiveShaping=false` exists to avoid. Recommend snapshot
  semantics plus an explicit `Refresh(item)`, matching `TreeTableViewChildrenView`.
- **One group per row.** The adapter rejects duplicate instances by design, so an item cannot appear in two
  groups at once. Confirm that is acceptable.
- **Non-data rows as a first-class concept.** Group headers occupy an index but are not data. #340 enforces this
  with scattered early-returns and still leaks into `SelectAll`, copy and export. We should enforce it once, via
  a single `IsSelectableItem` predicate consulted by `SelectAll`, `SelectAllCells`, the `SelectedRanges` →
  slots expansion, paste and export — plus our additions: the drag rectangle and
  `ApplyContextRequestSelection`.

#### Banner rows ("spanner rows")

**Clarified 2026-08-03:** a full-width row whose content spans every column, as in #340's group headers. This is
the *rendering half* of this item, not a separate feature — so it lands here rather than with the spanner
headers, which are column-side work sharing no code with it.

Two parts, in this order:

1. **The capability** — a row that renders one full-width piece of content instead of cells, is not selectable,
   not editable, and is skipped by keyboard navigation. Useful on its own (empty-state and section rows), and it
   is where the `IsSelectableItem` predicate above gets enforced.
2. **Grouping produces them** — the projection over `TreeTableViewSource` emits a banner row per group, with the
   count and the expand/collapse chevron.

`TableViewRowPresenter.ArrangeOverride` is the thing to watch: it arranges `_rootPanel` at a hardcoded y=0 and
the cells/details panels at explicit rects, so a banner has to replace that layout for the row rather than being
added alongside it. #340 hit exactly this and its band would have been drawn over.

### 3. Collapsible spanner headers — 4–6 days

**Decided 2026-08-03:** a second header level banding columns together ("Prices" over Bid/Ask/Mid), which the
user can collapse and expand. Not a group-by box.

Nothing upstream to borrow — no PR touches header layout.

#### Why this is cheaper than first estimated

`TableViewColumnsCollection.VisibleColumns` is a single chokepoint:

```csharp
public IList<TableViewColumn> VisibleColumns =>
    _visibleColumnsCached ??= [.. this.OfType<TableViewColumn>()
        .Where(x => x.Visibility == Visibility.Visible)
        .OrderBy(x => x.Order ?? 0)];
```

Everything downstream derives from it — `VisibleFrozenColumns`, `VisibleScrollableColumns`,
`VisibleScrollableColumnOffsets`, the horizontal virtualization band, and the cells each row builds. So
**collapsing a group is just setting its members' `Visibility`**; the body needs no new code, and the existing
cache-invalidation path already handles it. The work is concentrated in the header.

#### Model

```csharp
public class TableViewColumnGroup
{
    public string Name { get; set; }              // matched by TableViewColumn.GroupName
    public object? Header { get; set; }
    public DataTemplate? HeaderTemplate { get; set; }
    public bool IsCollapsible { get; set; } = true;
    public bool IsCollapsed { get; set; }
    public TableViewColumn? CollapsedColumn { get; set; }  // shown when collapsed; defaults to first member
}
```

- `TableViewColumn.GroupName` (string) assigns membership; `TableView.ColumnGroups` holds the presentation.
- Members must be **contiguous by `Order`**. Validate on build and report loudly rather than rendering a split
  banner.
- Ungrouped columns simply have no spanner above them and span both header rows.

#### Collapse semantics

Collapsing hides every member except `CollapsedColumn` (defaulting to the first member), so the group keeps a
visible anchor and the spanner keeps a non-zero width. Hiding *all* members would leave a zero-width banner with
nowhere to click to expand again. Expanding restores each member's pre-collapse `Visibility` — store it on
collapse rather than assuming everything was visible, or an already-hidden column reappears.

#### Layout

`Themes/TableViewHeaderRow.xaml` currently has `RowDefinitions` of `*` (headers) and `Auto` (gridline), with
`FrozenHeadersPanel` and `ScrollableHeadersPanel` as StackPanels. Add a spanner row above:
`Auto` (spanners) / `*` (headers) / `Auto` (gridline), with its own frozen and scrollable panels.

The scrollable spanner panel must pan in lockstep with `ScrollableHeadersPanel` — reuse the existing
`ApplyHorizontalScroll` transform rather than inventing a second mechanism. Each spanner's width is the sum of
its **visible** members' `ActualWidth`, which `VisibleScrollableColumnOffsets` already gives us as a running sum,
so a spanner's extent is one subtraction of two offsets.

#### Known hazards

- **Reorder must become group-aware.** Drag-reorder currently assigns an `Order` value freely; drops have to be
  constrained so a group's members stay contiguous, and dragging a spanner should move the whole block. This is
  interaction logic we have already modified, and it is easy to underestimate next to the layout work.
- **Frozen boundary.** A group straddling frozen/scrollable is ill-defined: the frozen panel does not pan, the
  scrollable one does. Recommend forcing a group to follow its first member's `IsFrozen` and reporting the
  conflict, rather than splitting one logical spanner into two independently panning visuals.
- **Column virtualization.** Spanners are few and cheap; render them all rather than banding them, but position
  them from the cached offsets so scrolling stays allocation-free.

#### Phasing

1. Model, contiguity validation, static spanner rendering — no collapse.
2. Collapse/expand, including `Visibility` save/restore and the collapsed anchor column.
3. Reorder constraints and frozen-boundary handling.
4. Accessibility (UIA header-group relationships) and keyboard collapse/expand.

Phases 1 and 2 are independently shippable.

#### Progress

**Model layer done 2026-08-03** (15 tests in `TableViewColumnGroupTests`), ahead of the rendering:

- `TableViewColumnGroup` (Name, Header, HeaderTemplate, IsCollapsible, IsCollapsed, CollapsedColumn),
  `TableViewColumn.GroupName`, `TableView.ColumnGroups`.
- `TableViewColumnsCollection.GetColumnGroupSpans` resolves banners to runs of *visible* columns, walking the
  frozen and scrollable sets separately because only one of them pans. Ungrouped columns each stand alone rather
  than merging into one empty banner, and a group split by a foreign column yields two spans rather than
  swallowing the intruder.
- `TableView.ValidateColumnGroups()` reports non-contiguous groups, frozen-boundary straddling, duplicate and
  missing names, and columns naming a group that does not exist. It checks *all* columns, not just visible ones,
  so a group split by a currently hidden column is reported before that column is shown.
- `TableView.SetColumnGroupCollapsed(group, collapse)` — collapse keeps the anchor visible, expand restores each
  member's saved visibility rather than showing everything.

**Rendering done 2026-08-03** (19 tests total), which completes phases 1 and 2:

- `Themes/TableViewHeaderRow.xaml` gained a banner row above the headers (`Auto` / `*` / `Auto`), with
  `FrozenSpannersPanel` and `ScrollableSpannersPanel`. Existing content moved to row 1, the gridline to row 2.
  With no groups defined both panels stay empty and the Auto row measures to zero, so an ungrouped grid looks
  and costs exactly what it did before.
- `TableViewColumnGroupHeader` renders one banner and toggles its group on tap.
- `TableViewHeaderRow.EnsureSpanners` rebuilds the banner visuals only when the span *shape* changes, and
  re-measures on every width pass, so dragging a column border does not churn the header.
- The scrollable banner panel pans with its own `TranslateTransform` and clip, driven from the same
  `ApplyHorizontalScroll`. Separate instances are mandatory — WinUI throws `E_BOUNDS` if one transform or
  geometry is attached to two elements.

Two things worth remembering, both found by tests rather than review: a `{ThemeResource}` key that does not
exist throws `COMException` at load, not at build (`TableViewGridLinesBrush` did not exist — the real key is
`TableViewVerticalGridLineStroke`); and a column `Visibility` change takes the `AddHeaders`/`RemoveHeaders` path
rather than a wholesale rebuild, so `EnsureSpanners` has to be driven from `OnColumnPropertyChanged` directly or
a collapse moves the columns and leaves the banner at its old width.

**Not yet done:** phase 3 (reorder constraints, frozen-boundary enforcement) and phase 4 (UIA, keyboard).
`ValidateColumnGroups()` reports a straddling group but nothing prevents one.

### 4. ListView-style hotkeys, reimplemented — **DONE 2026-08-03**

Shipped as `TableView.UseListViewHotkeys` (bool, default false). Claims exactly three keys, and only for row
interaction in Multiple/Extended outside editing:

- Up/Down move the current row **without** changing the selection — travel first, decide after.
- Enter toggles the current row.
- Shift+Up/Down extend from the anchor, and shrink when the direction reverses, leaving selections made
  elsewhere untouched.

Everything else falls through to the grid deliberately: Home/End stay column navigation, Ctrl+Up/Down still jump
to the first/last row, and Left/Right are untouched so `TreeTableView` keeps expand/collapse. `base.OnKeyDown` is
never called — all selection goes through our own range primitives, so an `ISelectionInfo` source keeps owning
its bookkeeping. 11 tests in `TableViewListViewHotkeysTests`, half of them asserting what the flag does *not*
claim.

### 5. Filter-aware ancestor expansion — 1–2 days

From #341: when a column filter matches a descendant, force-expand its ancestors so the match stays visible.
We have no equivalent, and it is a real gap for filtered trees.

### Also from #341, lower value

- Per-level sorting driven by the grid's own `SortDescriptions`.
- A `ChildrenSelector` delegate so consumers can avoid implementing `ITableViewTreeItem`.

## Decisions taken

| Date | Decision |
|---|---|
| 2026-08-03 | Merge none of #341 / #340 / #251. Harvest ideas; reimplement the hotkeys. |
| 2026-08-03 | "Group columns" means collapsible spanner headers, not a group-by box. |
| 2026-08-03 | Item 3 API agreed: `TableViewColumn.GroupName` + `TableView.ColumnGroups`, collapse keeping one anchor column visible. Phase 1 cleared to start. |

Still to settle, at the point each item starts rather than now: item 2's re-keying semantics and the
one-group-per-row constraint (see that section).

## Appendix: right-click selection

Right-click selection follows `SelectionUnit`, exactly like a left click:

- `SelectionUnit="Row"` — right-clicking a cell selects the owning row.
- `SelectionUnit="CellOrRow"` (default) — right-clicking a cell selects the **cell**, and no row. The cell
  claims the bubbling `ContextRequested` before its row sees it, and `MakeSelection` routes by unit.

Both are covered by tests in `TableViewContextSelectionTests`. The selection is applied *before* the context
flyout event is raised (`TableViewRow.cs:80` then `:82`), so a `RowContextFlyoutOpening` handler always observes
the post-click selection state.
