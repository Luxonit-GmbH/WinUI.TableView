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
