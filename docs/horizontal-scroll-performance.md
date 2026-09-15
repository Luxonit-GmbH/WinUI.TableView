# Horizontal scroll performance

Reported 2026-08-10: with ~80 columns, horizontal scrolling is very laggy; vertical scrolling is fine. Fixed the
same day. This records what it actually was, because four plausible explanations were measured and discarded
first, and each of them is the kind of thing that gets proposed again.

## The differential

The X axis had no compositor owner. `Themes/TableView.xaml` sets `win:HorizontalScrollMode="Disabled"` on the
template ScrollViewer unconditionally on Windows, and the control synthesises the pan itself: `HorizontalOffset`
is a DP, two-way bound to `HorizontalScrollBar2`, and every change ran a loop over every realized row writing a
`RenderTransform` and rewriting a clip. Vertical scrolling is the real ScrollViewer, where DirectManipulation
moves one content visual on the compositor and the UI thread is not involved.

So the two axes were never comparable: vertical is compositor work, horizontal was UI-thread work.

## What it cost

Release, 80 x 100px columns over 10k rows, 1200x800 viewport, `RowHeight` 32, column virtualization on, 25
realized rows. "Rendered" awaits one composition frame per tick; subtract the idle baseline of ~6ms/frame.

| | before | after | vertical |
|---|---|---|---|
| Rendered pan | 20.8 ms/frame | 3.7 ms/frame | 4.8 ms/frame |
| Dispatcher blocked time | — | 1413 ms | 1770 ms |
| p95 dispatcher gap | 2298 ms | 0.03 ms | 0.03 ms |

The UI thread was unavailable for **hundreds to thousands of milliseconds at a stretch**. That is the number that
matters for this app: the blotter's 8000 updates/sec are posted to the same thread, so a horizontal drag starved
the data feed and prices stopped moving until the drag ended.

## Four things it was NOT

Each was measured, not argued.

- **Not managed code.** The whole per-row loop cost 0.063 ms/tick; a pan plus a full layout pass cost 0.16 ms/tick.
  Meanwhile the frame cost 20 ms. Everything expensive was below the managed layer, invisible to any benchmark
  that does not wait for a frame — which is why `Grid_HorizontalPan_100Ticks` had reported a flat number for
  months while users called the control unusable.
- **Not the clip.** Assigning the clip once instead of rewriting its rect every tick: 18.9 -> 16.8 ms/frame,
  inside this machine's noise floor.
- **Not the clipped area.** Bounding the clip to the viewport instead of the panel's full ~8000px extent: no
  measurable change, and it added a per-row loop over frozen columns. Reverted.
- **Not the transform mechanism.** Swapping the per-row `RenderTransform` for a per-row composition `Translation`:
  20.4 vs 20.8 ms/frame. This one had a whole theory behind it ("a RenderTransform change makes the render walk
  revisit every child visual"). The theory was wrong.

## What it was

The cost tracks **how many visuals move**, not how many pixels move and not how they are moved. Panning one
shared ancestor holding exactly the same content cost 3.7 ms/frame against 20.8 for panning 25 row panels — and
took the thread from single-digit dispatcher pickups to tens of thousands.

## The fix

`TableView` owns one `CompositionPropertySet` with a single `Offset` scalar. `ItemsPanelRoot` binds an expression
animation to `-Offset`, so every row moves as one visual. Each row binds the chrome that must not scroll — the
row-header group, the vertical grid line, the frozen cells panel — to `+Offset`, cancelling the ancestor's motion.
The header row binds the same way. Bindings are made once, when the template is applied, so **a scroll tick is one
scalar write** and the compositor does the rest.

The per-row transforms and clips are gone. Scrolled-away cells now pass *under* the pinned chrome instead of being
clipped away from it, so the pinned chrome carries `Canvas.ZIndex="1"` — which also fixes hit-test ordering, since
z-order governs both.

Two things that made this workable, both verified by probe rather than assumed:

- **XAML hit-testing follows composition `Translation`**, including when an `ExpressionAnimation` over a shared
  `CompositionPropertySet` drives it. The widely-repeated caveat that it does not is wrong for this WinUI version.
  Without this the whole approach would have needed hit-testing rebuilt from column offsets.
- **`TransformToVisual` also reports it — but only once the compositor has committed it.** That one is a hazard,
  not a help, and it cost a regression: `CellsHorizontalOffset` (where the cells start, which sizes the header's
  corner panel) was computed from the counter-translated grid line's transform minus `HorizontalOffset`. In the
  synchronous layout pass right after a scroll the commit has not happened, the subtraction went negative,
  clamped to 0, and every header slid 16px (the row header width) left of its cells — intermittently, because a
  later re-arrange that ran after the commit corrected it. The rule: **layout boundaries come from layout
  positions (`ActualOffset`), never from `TransformToVisual` across the pinned/panned boundary.** Positions that
  must track the screen (drag-selection hit testing) are the opposite case, and read `TransformToVisual` on
  demand at the moment they are needed.

## Measuring it

`PerformanceBenchmarks.cs`, `TestCategory=Benchmark`. Two families matter:

- **`*_100Frames_Rendered`** — awaits a real composition frame per tick. Subtract `Grid_Idle_100Frames_RenderBaseline`
  and divide by 100 for the added cost per frame. Anything that does not wait for a frame cannot see this problem
  at all.
- **`*_DispatcherGap`** — a low-priority heartbeat re-enqueues itself; the gap between runs is how long the
  DispatcherQueue refused to pick up work. **Gate on `_BlockedMs`** (total time in gaps longer than a frame). The
  pickup count and the percentiles are only meaningful once the thread is responsive: while it is saturated the
  heartbeat runs a handful of times, and statistics over 3-5 samples swing wildly — 3, 0 and 39 pickups were all
  observed from identical code. An improvement was once reported from that count that was pure noise.

Always compare against the vertical twin. "Vertical is fine" is the whole premise, so if a change does not move
horizontal toward vertical, it has not addressed the reported problem.

## Not the column count

The rendered column sweep with virtualization on is flat: 12.3 / 16.3 / 15.2 / 17.4 ms per frame at 20 / 50 / 80 /
120 columns, with 50 scoring worse than 80. Horizontal panning always cost ~3x vertical. What 80 columns changed is
that people now *have* to scroll horizontally, and further, to reach their data.

## The first scroll: idle prefetch

Panning is free now, but the *first* scroll into columns that have never been shown still creates their content
— an element per revealed column per realized row — on the scroll that reveals them. "Lags on the first scroll,
smooth on the second" is that, and for a cell that does real work in its constructor (a UserControl with
`InitializeComponent`) it is the whole cost.

`ColumnPrefetchLength` (viewports, default 1; 0 disables) sizes a margin beside the realized band. A low-priority
pump creates that margin's content while the grid is idle, applies templates explicitly, measures it once under the
constraint the cell will use, and pins its DataContext — but leaves the cells collapsed, so nothing is measured or
drawn until it actually scrolls in, and a recycle underneath costs nothing. The pump yields whenever there has
been a horizontal tick, a recycle or a request in the last 150ms, works in 2ms increments (mid-row if need be), and
resumes on its own after a quiet window.

Measured, each arm in its own test host, constructor-built cells, 100 ticks of 20px on a fresh grid:

| | median | worst single tick |
|---|---|---|
| prefetch off | 1577–1967 ms | 1.0–1.1 s in half the iterations |
| prefetch on (456 cells prefetched, 0 pump increments during the pan) | 1420–1424 ms | 0.17–0.30 s |

The median gain is modest; the tail is the point — those one-second hitches are what a user calls lag, and the
proxy cell here is lighter than a real one.

### What this measurement got wrong first, so nobody repeats it

- **Arms measured in one test host cannot be compared.** Whichever arm ran first was fastest, every time, by up
  to 3x; swapping two arms moved the gap with them. A collection between iterations does not clear it — it is
  composition and DirectX state the collector cannot see. One vstest invocation per arm, with an exact-name
  filter (`/TestCaseFilter:"Name=..."`; `/Tests:` matches substrings and quietly ran two arms in one host).
  A "3x faster first scroll" was reported from a single-host run before this was understood, and retracted.
- **Count what the pump did, not that it ran.** An increment is a time budget; 5-8 increments turned out to be
  the whole margin when cells were cheap, and looked like starvation. The pump counts cells too.
- **The proxy cell has to pay where the real one pays.** A Button pays in `ApplyTemplate` on first Measure,
  which a Measure under a collapsed ancestor never triggers — with Button cells the prefetched arm equalled
  "off" exactly. A `TextBlock` pays nothing. The benchmark's heavy cell builds its tree in the constructor, like
  the consuming app's, and prefetch now applies templates explicitly as well.
- **"Idle" is not "no request lately."** A pan that stays inside the cached band raises no realize request for
  thirty ticks; a pump that watched requests read that as quiet and ran mid-drag. Every horizontal tick stamps
  activity now. And the yield must not stamp activity itself, or it perpetuates.

## Left open

- The pinned chrome hides the cells sliding under it by z-order alone. If frozen cells or row headers are ever
  given transparent backgrounds, content will bleed through — there is no clip behind it any more.
- Nothing stops the expression animations or disposes the property set. Row visuals are collected with their
  elements, so this is believed fine, but one intermittent test-host crash was seen during this work and never
  reproduced; if the host starts dying under long runs, look here first.
- Phase 3 (giving the axis back to the ScrollViewer, frozen columns hoisted into an overlay) is **dropped**. It
  existed as the fallback for a hit-testing problem that turned out not to exist, and the current numbers already
  beat vertical.

---

# Column virtualization was one-way (2026-09-14)

Reported: with about seventy columns the grid is slow both ways, "like there is no column virtualization".
Reproduced in the sample app's Performance Test page and in the consuming blotter.

## What was actually wrong

Virtualization only ever decided which cells were **visible**. A cell's content was generated once and kept for
the life of the cell: `_contentPending` is set true when the column is assigned and false when the content is
built, and nothing set it back. Worse, both `ColumnCacheLength` and `ColumnPrefetchLength` are multiples of the
**viewport**, which is a pixel measure — over seventy 90px columns in a 1180px viewport, the one-viewport prefetch
margin reached about fifty-two of them. So the idle pump deliberately built three quarters of the grid, and one
sideways sweep built the rest.

Collapsing a cell stops it being measured. It does not stop its bindings. A pinned DataContext freezes
inheritance, not source-driven updates, so the property change, the converter and the target write all still run.
On a blotter at 8000 updates a second, the grid was ticking every column of every realized row while showing
thirteen of them.

## The fix

Three nested column ranges instead of two, each a viewport multiple **capped in columns** so the realized set is
bounded by the viewport rather than by how many columns the consumer defined:

| range | cap each side | what it means |
|---|---|---|
| band | 4 columns | visible and measured |
| prefetch | 8 columns | content built, still collapsed |
| keep | 12 columns | content retained; beyond it, released |

`TableViewCell.ReleaseContent` is the inverse of `EnsureContent`. The gap between the prefetch edge and the keep
edge is the hysteresis that stops a cell being built and dropped on alternate wheel notches.

Each row now remembers the band its cells are flagged for, so a band change touches only the columns that crossed
an edge — two, usually — instead of every column of every row, and a recycled row whose band has not moved does
nothing at all. The chunked walk is ordered by row index rather than `HashSet` order, so the rows on screen are
realized first rather than scattered across seven dispatcher turns.

## Measured

Release x64, this dev box, each arm in its own test host, against a build of the immediately preceding commit run
the same way. The suite's noise floor is about 30%, so only the first row is a real result.

| benchmark | before | after |
|---|---|---|
| `Grid_MutationStorm_8000Updates_VisibleRows` (median of 3 runs) | 836 ms | 339 ms |
| `Grid_VerticalPan_80Cols_DispatcherGap_BlockedMs` | 1511 ms | 1283 ms |
| `Grid_VerticalPan_80Cols_100Frames_Rendered` | 1158 ms | 1100 ms |
| `Grid_HorizontalPan_ColumnSweep_100Frames_Rendered` 20/50/80/120 cols | 625/952/1009/994 ms | 635/930/981/1024 ms |
| `Grid_HorizontalPan_80Cols_FirstScroll_Rendered_PrefetchOn` | 1293 ms | 1366 ms |

The mutation storm is the headline and it is the blotter's own workload: three runs each, 761/858/835 against
298/377/339, no overlap. It is the direct consequence of releasing content — invisible columns stop ticking.
Everything else is inside the noise floor, including first scroll, so the smaller prefetch margin did not cost
what it saved.

## Per-column work removed from the recycle path

Vertical scrolling recycles a container per row, and each of these was a full pass over every column of it:

- **Every cell had a `Loaded` handler** doing `InvalidateMeasure` and a visual-state transition. WinUI raises
  `Loaded` on every descendant when a recycled container is re-attached, so this was an entire O(columns) pass
  that no reading of call sites would find. Selection state moved to `OnApplyTemplate` and to the reveal path.
- **`EnsureLayout`'s two `??=` searches** spanned the whole row subtree and never cached a null result, so they
  re-walked every cell on every call. `??=` is not a cache when null is a legitimate answer.
- `TableViewRow_Loaded` re-ran `EnsureGridLines` over every cell, re-writing values that cannot change because a
  container came back.
- `RefreshElement` was a property read and a virtual dispatch per cell to reach an empty method body; only the
  template and tree columns override it, and they now say so with `NeedsRefreshOnRecycle`.
- The queued selection-state apply ran a transition on every cell even with nothing selected anywhere.
- `UpdatePosition` did a `TransformToVisual` per row per arrange for a value only drag-selection reads; it is now
  seeded when a drag starts and refreshed only during one.

## A latent bug this turned up

`ClearContainerForItemOverride` nulls the row's `TableView`, and `PrepareContainerForItemOverride` restored it
**after** the base call that raises `OnContentChanged`. So on every recycle that handler ran against a null grid
and silently skipped the cell realize, the cell-set self-heal and the alternate-row colouring — alternate row
colours were simply wrong after scrolling for anyone who set them. The back-reference is now restored before the
base call, and `TableViewRecycledRowStateTests` covers both halves.

## Things measured and found innocent, so nobody re-investigates

`OnItemPropertyChanged` is unreachable while `AllowLiveShaping` is false, and it is false here. The transparent
grid-line brush allocation never fires at the default `GridLinesVisibility`. The quadratic `InsertCell` is bounded
to row build. The "widths unknown, realize everything" startup fallback never fires — first paint really is
virtualized. Row width and compositor visual count are not the problem. `VisualStateManager.GoToState` does not
force a template apply, so a collapsed cell stays cheap until something realizes it.

The cells panel measuring all its children per pass is real but minor, and the expensive half is the collection
indexer, not the short-circuited `Measure` — so the fix was to cache the child references, not to add a
`Visibility` check, which would have swapped a cheap projected call for a comparable one. The snapshot is
invalidated from all three mutation sites, because a column **move** removes and re-inserts one cell: the count
returns to where it was while the order is different, and a count-only check would have laid every cell out at
the wrong offset.

---

# Round two: what the cost actually is (2026-09-14)

Round one did not fix scrolling. Reported after it: horizontal scrolling is about 15x faster with column
virtualization **off**, vertical is 2x slower with it off, fast vertical scrolling and scrollbar throws are
very laggy either way, and `MeasureOverride` keeps appearing in traces.

## The benchmarks could not see the problem

Every pan benchmark here steps 20px a tick. Over a hundred ticks that moves the realized band about five
times, so the virtualization machinery barely runs. Dragging a scrollbar sweeps the whole extent in the
same number of frames and moves the band every tick. That is a different regime, and it is the one users
complain about.

`Grid_HorizontalScrollbarSweep_80Cols_Rendered` and `Grid_VerticalScrollbarThrow_80Cols_Rendered`, with
their `_NoColumnVirtualization` twins, sweep the full extent and throw a hundred rows a tick. They
reproduce both complaints. The first sweep is the warm-up, so what is measured is the steady state.

## Three plausible causes, all disproved by measurement

Each was argued from the code, implemented, and measured. None moved the benchmark beyond noise.

| hypothesis | horizontal sweep |
|---|---|
| content released on the scroll path, so a drag rebuilds elements forever | 1698 ms with, 1626-1733 ms without |
| the per-cell recycle loops (width resync, style resolution) | no change |
| the cells panel measuring and arranging all seventy children | 1816 to 1626 ms, inside noise |

The create-and-destroy treadmill was real and is worth removing on its own merits, but it is not what
makes scrolling slow. Neither is any of the managed per-cell work. **The cost is the layout passes that
revealing a column forces, and the measure of the cells that are actually visible.**

With virtualization off, nothing ever changes visibility, so a horizontal tick dirties nothing and
`UpdateLayout` is a no-op; the compositor pans one visual and the UI thread is idle. With it on, every
band change makes cells visible, and that forces a real layout pass over the rows.

## Where it ended up

| benchmark, ms per 100 ticks | before round two | after | virtualization off |
|---|---|---|---|
| horizontal scrollbar sweep | 1816 | 1370 (median of 3) | 599 |
| vertical scrollbar throw | 9879 | 9892 | 30357 |

Horizontal went from 3.0x the cost of running without virtualization to 2.3x. The change that produced
it was restricting the in-motion reveal to the rows actually on screen: a grid realizes about twice its
viewport, and dirtying the cached rows made them take part in a layout pass for a band nobody is looking
at them against. The settle pass reconciles them a moment later.

**Vertical scrolling is not a column-virtualization problem.** With virtualization on, a scrollbar throw
is three times *faster* than with it off, and the cost tracks the number of cells measured, which is what
virtualization already bounds. At about 99 ms a frame it is still far too slow, but the levers are
`CacheLength` and `ColumnCacheLength`, which decide how many cells get measured, not the realize machinery.

## What would close the remaining horizontal gap, and what it costs

Matching the no-virtualization number means never changing visibility while the user scrolls: reveal
columns as they come into view and collapse them only after the grid has been idle for a while, rather
than on the 50 ms settle. Horizontal scrolling would then converge on the no-virtualization speed as the
user explores, because a column that is already visible costs nothing to scroll over again.

The price is that vertical scrolling degrades toward the no-virtualization number for as long as the
columns stay expanded, which the table above puts at three times worse. That is a real trade between the
two axes and not a free win, which is why it is written down here rather than simply done.

## Also in this round

- Selection visual state is applied idempotently: the cell remembers what it last handed to the visual
  state manager, so the wholesale per-recycle apply becomes a field compare instead of thousands of
  state transitions and as many array allocations a frame.
- The reveal path only asserts selection state on a grid that has, or has had, a cell selection. The flag
  must be sticky: a live-count guard would stop scrubbing a selected cell at exactly the moment the
  selection is cleared while that cell is scrolled away.
- The prefetch timer is armed only when it is not already running. It is a poll, not a debounce, so the
  stop-and-start it was doing once per recycled container was pure waste.
- A column-layout version lets a recycled row skip the width resync entirely when no column has moved.
  It is bumped beside the cache invalidation, not where the change event is raised, because batched
  column changes suppress the event while really changing widths.
- `ReleaseContent` now refuses a cell that is visible, not merely one whose flag says it is out of band.
  The two can disagree after virtualization is toggled, and the consequence was a blanked cell.

---

# Round three: profiling instead of reasoning (2026-09-15)

Two more code-derived causes were implemented and measured, one turned out to be a regression, and the
change that actually helped was a bug the profile pointed at rather than any of the planned items.

## What the profile said

`dotnet-trace` attached to the test host during `Grid_HorizontalScrollbarSweep_80Cols_Rendered`, steady
state (grid loaded, warm-up done). This profile is wall-clock per thread and its call counts are
synthesized from one-millisecond samples, so shares are trustworthy and per-call figures are not.

| where the UI thread's time went | share |
|---|---|
| under `TableViewCellsPanel.MeasureOverride` (the dirty rows' measures, cells included) | 25% |
| the settle pass re-creating and re-pinning content (`RealizeRowChunk`) | 9% |
| native layout and render with no library frame on the stack | 27% |
| the benchmark's own render waits and continuations | most of the rest |

So the managed `MeasureOverride` in the traces is real, and it is the cost of the rows a band change
dirties. The reveal path itself did not register.

## Tried, measured, and what happened

**Arrange each row once.** Three ancestors re-arranged their subtree at a rect shifted by the 4px
corner radius after the base pass had already arranged it, and because the base pass resets the rect
every time, the two never converged: every row was arranged twice on every layout pass, for the life of
the control. The shift is now a margin, set once, with the multi-select variant re-applied from the
selection-mode handler. Correct and provably less work, but it did not move either benchmark beyond
noise. Kept, because it removes the work on every layout pass on both axes; the visual check is the row
corners and the selection background.

**Gate the row header's shared write.** Each row header wrote `RowHeaderActualWidth` on the grid from
inside its own measure, on every pass, forced by the presenter above it. It now reads first. No
measurable effect on its own.

**Look ahead in the direction of travel.** Reveal a full viewport ahead while the horizontal axis is
moving, shrink on settle, so a drag reveals once per viewport instead of once per few columns. Measured
as a clear regression: the horizontal sweep went from 1461 ms to 4342 ms and the vertical throw from
9965 ms to 13862 ms, reproducibly, on an idle machine. Reverted. Do not retry it without first
understanding why: the plan's own rule was that if the vertical number moves, the settle is not
shrinking the band, and it moved.

**The supersede churn, which was the actual win.** A scrollbar drag has brief pauses. After one of at
least 50 ms the settle pass starts, chunked across dispatcher turns; when the drag resumes, the reveal
path supersedes it, and the abandoned pass invalidates the settled range as it aborts. The reveal code
read an invalid settled range as "nothing realized yet" and started a synchronous full pass, which the
next tick superseded, and so on: a row sort plus an eight-row, eighty-column walk on every other tick
for the rest of the drag. The check now consults both memos, so once anything has been revealed it takes
the delta path.

| horizontal scrollbar sweep, ms per 100 ticks | |
|---|---|
| round two | 1364 to 1461 |
| with the churn fixed (three runs) | 947, 872, 889 |
| virtualization off | 599 |

Vertical throw unchanged at about 9965 ms, as it should be: the fix is on the horizontal path only.

## Not done, and why

The plan's third item would have keyed the cell's constraint cache on the configured height rather than
the arranged one, to stop a band change re-measuring every cell in a row when `RowHeight` is left
unset. That fallback to the arranged height was itself a deliberate earlier fix, bounding content that
was otherwise measured at infinite height on every data tick, and reverting it would reopen that.
Grids that set `RowHeight`, which includes the sample page and the blotter, are not affected either
way, so it is left alone and noted here.

## Where it stands

Horizontal scrolling with virtualization on now costs about 1.5 times what it costs with it off, down
from 2.3 at the start of this round and about 3 at the start of the previous one. The remaining gap is
the layout pass that revealing a column forces on the visible rows, and the profile puts a quarter of
the thread there. Vertical is unchanged and is not a column-virtualization problem.
