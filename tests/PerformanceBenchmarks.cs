using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace WinUI.TableView.Tests;

/// <summary>
/// Stopwatch-based benchmarks for the TableView hot paths (blotter profile: many columns, many rows, frequent
/// mutations). These are NOT pass/fail tests — they report timings so a change can be compared against the previous
/// run and catch performance regressions.
///
/// How to run:
///   - Visual Studio: switch to RELEASE x64, then in Test Explorer group by Traits and run the "Benchmark" category.
///   - CLI: dotnet test tests/WinUI.TableView.Tests.csproj -c Release -p:Platform=x64 --filter TestCategory=Benchmark
///
/// IMPORTANT: only Release numbers are meaningful. Debug builds inflate per-cell costs by an order of magnitude and
/// distort exactly the paths these benchmarks exist to guard. Debug runs are marked as such in the output.
///
/// Every result is also appended to %TEMP%\WinUI.TableView.Benchmarks.csv (timestamp, config, benchmark, ms) so runs
/// can be diffed over time without copying numbers around.
/// </summary>
[TestClass]
public class PerformanceBenchmarks
{
    private const int ColumnCount = 50;
    private const int RowCount = 10_000;

    public TestContext TestContext { get; set; } = null!;

    // ---------------------------------------------------------------------------------------------------------
    // Columns collection
    // ---------------------------------------------------------------------------------------------------------

    [UITestMethod]
    [TestCategory("Benchmark")]
    public void Columns_Add_50_OneByOne()
    {
        Report(Measure(() =>
        {
            var tableView = new TableView { AutoGenerateColumns = false };

            foreach (var column in CreateColumns(ColumnCount))
            {
                tableView.Columns.Add(column);
            }
        }, warmup: 2, iterations: 10));
    }

    /// <summary>
    /// Grouping 10k rows into 50 groups: the projection cost the consumer pays on a GroupByPath change.
    /// </summary>
    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Grouping_Project_10kRows_50Groups()
    {
        var tableView = await LoadGroupingGridAsync();

        Report(Measure(
            () =>
            {
                tableView.GroupByPath = null;
                tableView.GroupByPath = "Bucket"; // 50 groups over 10k rows
                tableView.UpdateLayout();
            },
            warmup: 1,
            iterations: 3),
            "Grouping_Project_10kRows_50Groups");

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);
    }

    /// <summary>
    /// Collapsing and re-expanding one 200-row group on a live grid — the gesture a user repeats most.
    /// </summary>
    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Grouping_CollapseExpandOneGroup_10kRows()
    {
        var tableView = await LoadGroupingGridAsync();
        tableView.GroupByPath = "Bucket";
        tableView.UpdateLayout();

        var group = tableView.Groups[0];

        Report(Measure(
            () =>
            {
                tableView.SetGroupExpanded(group, false);
                tableView.UpdateLayout();
                tableView.SetGroupExpanded(group, true);
                tableView.UpdateLayout();
            },
            warmup: 1,
            iterations: 5),
            "Grouping_CollapseExpandOneGroup_10kRows");

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);
    }

    /// <summary>
    /// The cost grouping adds to an ordinary scroll: the same horizontal pan, but over a grouped source.
    /// </summary>
    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Grouping_HorizontalPan_100Ticks_Grouped()
    {
        var tableView = await LoadGroupingGridAsync();
        tableView.GroupByPath = "Bucket";
        tableView.UpdateLayout();

        Report(Measure(
            () =>
            {
                for (var i = 0; i < 100; i++)
                {
                    tableView.SetValue(TableView.HorizontalOffsetProperty, (double)(i * 20));
                }
            },
            warmup: 2,
            iterations: 10),
            "Grouping_HorizontalPan_100Ticks_Grouped");

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);
    }

    /// <summary>
    /// Column grouping's cost on the header: banners must be resolved and re-measured on every width pass.
    /// </summary>
    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task ColumnGrouping_HeaderWidthPass_50Cols_x20()
    {
        var tableView = await LoadGridAsync();

        for (var i = 0; i < 10; i++)
        {
            tableView.ColumnGroups.Add(new TableViewColumnGroup { Name = $"G{i}", Header = $"G{i}" });
        }

        for (var i = 0; i < tableView.Columns.Count; i++)
        {
            tableView.Columns[i].GroupName = $"G{i / 5}"; // 5 columns per banner, contiguous
        }

        tableView.UpdateLayout();

        Report(Measure(
            () =>
            {
                for (var i = 0; i < 20; i++)
                {
                    tableView.HeaderRow?.InvalidateHeaderWidths();
                    tableView.UpdateLayout();
                }
            },
            warmup: 1,
            iterations: 5),
            "ColumnGrouping_HeaderWidthPass_50Cols_x20");

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);
    }

    private static async Task<TableView> LoadGroupingGridAsync()
    {
        var items = new ObservableCollection<BenchItem>(Enumerable.Range(0, RowCount).Select(i => new BenchItem { Name = $"Item {i}", Value = i }));

        var tableView = new TableView
        {
            AutoGenerateColumns = false,
            RowHeight = 32,
            Width = 1200,
            Height = 600,
        };

        foreach (var column in CreateColumns(ColumnCount))
        {
            tableView.Columns.Add(column);
        }

        tableView.ItemsSource = items;

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(tableView);
        tableView.UpdateLayout();

        return tableView;
    }

    [UITestMethod]
    [TestCategory("Benchmark")]
    public void Columns_AddRange_50()
    {
        Report(Measure(() =>
        {
            var tableView = new TableView { AutoGenerateColumns = false };
            tableView.Columns.AddRange(CreateColumns(ColumnCount));
        }, warmup: 2, iterations: 10));
    }

    [UITestMethod]
    [TestCategory("Benchmark")]
    public void Columns_Reset_50_With_50()
    {
        var tableView = new TableView { AutoGenerateColumns = false };
        tableView.Columns.AddRange(CreateColumns(ColumnCount));

        Report(Measure(() => tableView.Columns.Reset(CreateColumns(ColumnCount)), warmup: 2, iterations: 10));
    }

    [UITestMethod]
    [TestCategory("Benchmark")]
    public void Columns_VisibleColumns_RebuildAfterVisibilityToggle_x200()
    {
        var tableView = new TableView { AutoGenerateColumns = false };
        tableView.Columns.AddRange(CreateColumns(ColumnCount));
        var column = tableView.Columns[ColumnCount / 2];

        Report(Measure(() =>
        {
            // Each toggle invalidates every visible-column cache; the read rebuilds them. This is the cost of a
            // runtime show/hide of a column.
            for (var i = 0; i < 200; i++)
            {
                column.Visibility = i % 2 == 0 ? Visibility.Collapsed : Visibility.Visible;
                _ = tableView.Columns.VisibleColumns.Count;
            }
        }, warmup: 2, iterations: 5));
    }

    [UITestMethod]
    [TestCategory("Benchmark")]
    public void Columns_VisibleColumnIndex_CachedLookup_x100k()
    {
        var tableView = new TableView { AutoGenerateColumns = false };
        tableView.Columns.AddRange(CreateColumns(ColumnCount));
        var columns = tableView.Columns.VisibleColumns;

        Report(Measure(() =>
        {
            for (var i = 0; i < 100_000; i++)
            {
                _ = tableView.Columns.VisibleColumnIndex(columns[i % columns.Count]);
            }
        }, warmup: 2, iterations: 5));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Full grid — load, scroll, layout, mutations (blotter profile)
    // ---------------------------------------------------------------------------------------------------------

    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Grid_InitialLoad_10kRows_50Cols()
    {
        var result = await MeasureAsync(async () =>
        {
            var tableView = await LoadGridAsync();
            await UnloadAsync(tableView);
        }, warmup: 1, iterations: 3);

        Report(result);
    }

    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Grid_InitialLoad_10kRows_50Cols_NoCollectionView()
    {
        var result = await MeasureAsync(async () =>
        {
            var tableView = await LoadGridAsync(useCollectionView: false);
            await UnloadAsync(tableView);
        }, warmup: 1, iterations: 3);

        Report(result);
    }

    /// <summary>
    /// The dependency-property half of a horizontal scroll tick, and ONLY that half: the OnHorizontalOffsetChanged
    /// callback (shared clip recompute, header pan, the per-row transform/clip loop, realize scheduling).
    ///
    /// Read this number as an isolation, never as "horizontal scrolling costs X". It EXCLUDES everything the real
    /// gesture pays between ticks: no measure, no arrange, no render, and — because each tick restarts the 50ms
    /// settle timer — no column realization either. A tight loop of 100 property writes returns before the first
    /// frame would have been drawn, which is why this benchmark stayed flat while users reported an unusable
    /// 80-column pan. The benchmarks in the "Horizontal scroll at blotter width" section below add those layers
    /// back one at a time.
    /// </summary>
    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Grid_HorizontalPan_100Ticks()
    {
        var tableView = await LoadGridAsync();

        Report(Measure(() =>
        {
            // The per-tick cost of a horizontal scroll: transform pan + shared clip + realize scheduling. This is
            // the path that must stay flat for smooth scrollbar drags.
            for (var i = 1; i <= 100; i++)
            {
                tableView.SetValue(TableView.HorizontalOffsetProperty, (double)(i * 20));
            }

            tableView.SetValue(TableView.HorizontalOffsetProperty, 0d);
        }, warmup: 2, iterations: 5));

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Grid_VisibleScrollableRange_BinarySearch_x100k()
    {
        var tableView = await LoadGridAsync();

        Report(Measure(() =>
        {
            // Runs on every realize pass; must stay O(log columns).
            for (var i = 0; i < 100_000; i++)
            {
                _ = tableView.GetVisibleScrollableRange(0.5);
            }
        }, warmup: 2, iterations: 5));

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Grid_FullMeasurePass_RealizedRows_x20()
    {
        var tableView = await LoadGridAsync();

        Report(Measure(() =>
        {
            // Invalidate + synchronous layout of everything realized: the cell MeasureOverride hot path
            // (ConstrainContent caching, viewport gating). This is what runs on every layout-affecting change.
            for (var pass = 0; pass < 20; pass++)
            {
                foreach (var row in tableView.Rows)
                {
                    row.InvalidateMeasure();
                }

                tableView.UpdateLayout();
            }
        }, warmup: 2, iterations: 5));

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Grid_MutationStorm_8000Updates_VisibleRows()
    {
        var tableView = await LoadGridAsync(out var items);
        var visible = Math.Min(40, items.Count);

        Report(Measure(() =>
        {
            // The trading tick: 8000 INPC updates hitting bound, visible cells, then one layout pass — the
            // steady-state cost the blotter pays every second.
            for (var i = 0; i < 8_000; i++)
            {
                items[i % visible].Value = i;
            }

            tableView.UpdateLayout();
        }, warmup: 2, iterations: 5));

        await UnloadAsync(tableView);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Horizontal scroll at blotter width — the "80 columns pans badly, vertical is fine" report
    //
    // The two axes do not share a code path, which is the whole reason the report is lopsided. The control
    // template pins the ScrollViewer's HorizontalScrollMode to Disabled and TableView pans the cells itself from
    // its own HorizontalOffset dependency property; vertical scrolling is the real platform ScrollViewer with its
    // ItemsStackPanel recycling underneath. So a cost that both axes pay cannot explain the asymmetry, and only
    // the differential between a horizontal and a vertical benchmark over the SAME grid is evidence.
    //
    // Each benchmark drives the same 100 offset ticks. They differ in how much of a real frame a tick pays for,
    // which is what makes them bisectable:
    //   *_100Ticks   dependency-property write only          (see Grid_HorizontalPan_100Ticks)
    //   *_WithLayout + a synchronous measure/arrange pass
    //   *_Rendered   + a real composition frame — the pixels the user, and a Citrix session, actually waits for
    //
    // Two further benchmarks split the tick's own work into its two suspects: the per-row transform/clip loop
    // (Grid_HorizontalPan_80Cols_RowTransformClipOnly_100Ticks) and the settle-time column realization
    // (Grid_ColumnRealizeBand_AllRows_80Cols_x20). Nothing here asserts; they report so the shape can be read.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>The reported-slow blotter width. Kept separate from <see cref="ColumnCount"/> on purpose.</summary>
    private const int WideColumnCount = 80;

    /// <summary>Offset ticks per pan — about the number of moves in one unhurried scrollbar drag.</summary>
    private const int PanTicks = 100;

    /// <summary>Pixels per tick. 100 x 20px = 2000px of travel, well inside the range at 80 x 100px columns.</summary>
    private const double PanStep = 20;

    /// <summary>
    /// Pixels per tick for the column sweep. Smaller than <see cref="PanStep"/> because the sweep's narrowest grid
    /// (20 x 100px columns against a 1200px viewport) only has ~800px of scroll range, and every point on the curve
    /// must perform the identical gesture or the curve measures travel distance instead of column count.
    /// </summary>
    private const double SweepPanStep = 8;

    /// <summary>
    /// Long enough for the 50ms realize settle timer to fire and for its 8-rows-per-dispatcher-turn chunking to
    /// drain. Only ever awaited during setup, never inside a stopwatch.
    /// </summary>
    private const int RealizeSettleWaitMs = 300;

    /// <summary>
    /// The reported gesture, with layout in the number: a horizontal drag across 80 columns where every tick is
    /// followed by a synchronous measure/arrange. Compare against <see cref="Grid_HorizontalPan_80Cols_100Ticks"/>
    /// (same grid, property write only) to see what layout adds, and against
    /// <see cref="Grid_VerticalPan_80Cols_100Ticks_WithLayout"/> to see whether the axes really differ.
    ///
    /// The 50ms settle timer is restarted by every tick, so a tight drag never realizes columns mid-pan. That is
    /// the control's design, and it means this number is the cost of the drag itself; the realization it defers is
    /// measured by <see cref="Grid_ColumnRealizeBand_AllRows_80Cols_x20"/>.
    /// </summary>
    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Grid_HorizontalPan_80Cols_100Ticks_WithLayout()
        => await HorizontalPanWithLayoutAsync(WideColumnCount, columnVirtualization: true, "Grid_HorizontalPan_80Cols_100Ticks_WithLayout");

    /// <summary>
    /// The same drag with column virtualization OFF — which is the control's DEFAULT
    /// (<see cref="TableView.IsColumnVirtualizationEnabled"/> is false), so this is what a consumer that never
    /// opted in is living with. Off, every cell of every realized row stays Visible and inside the panned+clipped
    /// panel, so the visual the clip change dirties each tick contains 80 columns of live content rather than the
    /// ~24 the band would leave. If this is far worse than the virtualization-on run, the fix is a default change,
    /// not a scrolling rewrite.
    /// </summary>
    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Grid_HorizontalPan_80Cols_100Ticks_WithLayout_NoColumnVirtualization()
        => await HorizontalPanWithLayoutAsync(WideColumnCount, columnVirtualization: false, "Grid_HorizontalPan_80Cols_100Ticks_WithLayout_NoColumnVirtualization");

    /// <summary>
    /// The property-write-only rung of the ladder at 80 columns, so the WithLayout and Rendered numbers above and
    /// below can be attributed. Everything <see cref="Grid_HorizontalPan_100Ticks"/> excludes, this excludes too —
    /// it exists only to be subtracted.
    /// </summary>
    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Grid_HorizontalPan_80Cols_100Ticks()
    {
        var tableView = await LoadPanGridAsync(WideColumnCount, columnVirtualization: true);

        Report(Measure(
            () =>
            {
                for (var i = 1; i <= PanTicks; i++)
                {
                    tableView.SetValue(TableView.HorizontalOffsetProperty, i * PanStep);
                }
            },
            warmup: 2,
            iterations: 5,
            reset: () => tableView.SetValue(TableView.HorizontalOffsetProperty, 0d)));

        await UnloadAsync(tableView);
    }

    /// <summary>
    /// The control for the whole section. Same grid, same 80 columns, same 100 ticks of the same 20px — but down
    /// the platform ScrollViewer instead of TableView's own offset property. The report is "vertical is fine", so
    /// this number must come out well below its horizontal twin. If it does not, the problem is the sheer cell
    /// count at 80 columns and our model of a horizontal-specific defect is wrong.
    /// </summary>
    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Grid_VerticalPan_80Cols_100Ticks_WithLayout()
        => await VerticalPanWithLayoutAsync(WideColumnCount, columnVirtualization: true, "Grid_VerticalPan_80Cols_100Ticks_WithLayout");

    /// <summary>
    /// The vertical control in the default (virtualization off) world, so the horizontal/vertical asymmetry can be
    /// read in both worlds. Vertical scrolling realizes and measures whole rows, so if turning virtualization off
    /// hurts vertical as much as it hurts horizontal, the cost is per-cell measure and not the pan path.
    /// </summary>
    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Grid_VerticalPan_80Cols_100Ticks_WithLayout_NoColumnVirtualization()
        => await VerticalPanWithLayoutAsync(WideColumnCount, columnVirtualization: false, "Grid_VerticalPan_80Cols_100Ticks_WithLayout_NoColumnVirtualization");

    /// <summary>
    /// The horizontal drag with a real composition frame awaited per tick. This is the closest thing here to what
    /// the user experiences, because a scroll is judged in frames, and on Citrix the frame — not the UI-thread
    /// callback — is what has to cross the wire. Subtract
    /// <see cref="Grid_Idle_100Frames_RenderBaseline"/> and divide by 100 for the added cost per frame.
    ///
    /// This is also the only benchmark that can catch the pathological case: if one tick's work exceeds the 50ms
    /// settle window, the realize timer fires DURING the pan, which realizes a band, which makes the next frame
    /// slower still. A tight loop can never reproduce that feedback; a frame-paced one can.
    /// </summary>
    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Grid_HorizontalPan_80Cols_100Frames_Rendered()
        => await RenderedPanAsync(WideColumnCount, columnVirtualization: true, horizontal: true, "Grid_HorizontalPan_80Cols_100Frames_Rendered");

    /// <summary>
    /// The frame-paced drag at 20 / 50 / 80 / 120 columns. The layout-only sweep is flat in column count, so this
    /// is what decides whether "it got slow when we went to 80 columns" is really about the column count at all.
    /// With column virtualization on, the live cells per row are bounded by the viewport band rather than by the
    /// total, so a flat curve here means the lag is inherent to horizontal panning and more columns merely force
    /// more of it — a materially different conclusion, and a different fix.
    /// </summary>
    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Grid_HorizontalPan_ColumnSweep_100Frames_Rendered()
    {
        foreach (var columnCount in (int[])[20, 50, 80, 120])
        {
            await RenderedPanAsync(columnCount, columnVirtualization: true, horizontal: true,
                $"Grid_HorizontalPan_Sweep_{columnCount}Cols_100Frames_Rendered");
        }
    }

    /// <summary>
    /// The frame-paced drag in the default (virtualization off) world. With every cell visible, each tick's clip
    /// and transform change dirties a visual holding 80 columns x every realized row of live content, and the
    /// render cost of that is invisible to any benchmark that does not wait for a frame.
    /// </summary>
    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Grid_HorizontalPan_80Cols_100Frames_Rendered_NoColumnVirtualization()
        => await RenderedPanAsync(WideColumnCount, columnVirtualization: false, horizontal: true, "Grid_HorizontalPan_80Cols_100Frames_Rendered_NoColumnVirtualization");

    /// <summary>
    /// The frame-paced vertical control. Vertical scrolling hands the pan to the ScrollViewer, so the per-frame
    /// delta over the idle baseline should be close to the cost of recycling the rows that crossed the viewport
    /// edge and nothing else.
    /// </summary>
    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Grid_VerticalPan_80Cols_100Frames_Rendered()
        => await RenderedPanAsync(WideColumnCount, columnVirtualization: true, horizontal: false, "Grid_VerticalPan_80Cols_100Frames_Rendered");

    /// <summary>
    /// The floor the *_Rendered benchmarks stand on: 100 composition frames with the grid loaded and nothing
    /// touching it. Without this number a rendered pan is unreadable, because ~100 x the frame interval of it is
    /// just the display cadence.
    /// </summary>
    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Grid_Idle_100Frames_RenderBaseline()
    {
        var tableView = await LoadPanGridAsync(WideColumnCount, columnVirtualization: true);

        var result = await MeasureAsync(
            async () =>
            {
                for (var i = 0; i < PanTicks; i++)
                {
                    await WaitForRenderAsync();
                }
            },
            warmup: 1,
            iterations: 3);

        Report(result);
        await UnloadAsync(tableView);
    }

    /// <summary>
    /// Suspect (a) in isolation: the per-row pan bookkeeping. OnHorizontalOffsetChanged loops every realized row
    /// and calls ApplyHorizontalScroll(useCachedClip: true), which per row writes TranslateTransform.X, writes
    /// RectangleGeometry.Rect and assigns UIElement.Clip — three dependency-property sets across the XAML interop
    /// boundary, each also dirtying that row's visual for the next render, plus a HorizontalOffset read and a
    /// details-panel Visibility read. At 80 columns and ~30 realized rows that is roughly 150 property operations
    /// and 30 dirtied visuals per tick, none of which the vertical path performs.
    ///
    /// Calling the loop directly excludes the header pan, the shared clip recompute and the realize scheduling, so
    /// what remains is only this. Divide by (100 x the realized row count written to the test output) for the
    /// per-row-per-tick cost, then compare with <see cref="Grid_ColumnRealizeBand_AllRows_80Cols_x20"/> to see
    /// which of the two suspects is actually large.
    /// </summary>
    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Grid_HorizontalPan_80Cols_RowTransformClipOnly_100Ticks()
    {
        var tableView = await LoadPanGridAsync(WideColumnCount, columnVirtualization: true);

        // A non-zero offset is mandatory: at h <= 0 ApplyHorizontalScroll takes the "Clip = null" branch and would
        // measure a state the user is only ever in before they start scrolling.
        tableView.SetValue(TableView.HorizontalOffsetProperty, 1_000d);
        tableView.UpdateLayout();
        await Task.Delay(RealizeSettleWaitMs);

        // Snapshotted outside the stopwatch: TableView.Rows allocates and sorts on every read, which the real loop
        // (over the raw row list) does not do.
        var rows = tableView.Rows;
        TestContext.WriteLine($"realized rows: {rows.Count}, columns: {WideColumnCount}");

        Report(Measure(
            () =>
            {
                for (var tick = 0; tick < PanTicks; tick++)
                {
                    foreach (var row in rows)
                    {
                        row.RowPresenter?.ApplyHorizontalScroll(useCachedClip: true);
                    }
                }
            },
            warmup: 2,
            iterations: 5));

        await UnloadAsync(tableView);
    }

    /// <summary>
    /// Suspect (b) in isolation: what the settle timer runs 50ms after the drag stops. RealizeRowCells walks EVERY
    /// visible scrollable column of a row — not just the band — doing a dictionary lookup plus a SetInViewport per
    /// cell, and SetInViewport reads (and sometimes writes) the cell's Visibility. At 80 columns and ~30 realized
    /// rows that is ~2400 lookups and ~2400 Visibility reads per pass, and the control runs one pass per settled
    /// scroll, chunked 8 rows to a dispatcher turn.
    ///
    /// Content generation happens once per cell, so the steady-state number here is the flag sweep alone. A large
    /// value points at making the sweep band-relative instead of all-columns; a small one exonerates realization
    /// and leaves the per-row transform/clip loop and the render cost as the remaining explanations.
    /// </summary>
    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Grid_ColumnRealizeBand_AllRows_80Cols_x20()
    {
        var tableView = await LoadPanGridAsync(WideColumnCount, columnVirtualization: true);
        var rows = tableView.Rows;
        TestContext.WriteLine($"realized rows: {rows.Count}, columns: {WideColumnCount}");

        Report(Measure(
            () =>
            {
                for (var pass = 0; pass < 20; pass++)
                {
                    foreach (var row in rows)
                    {
                        tableView.RealizeRowCells(row);
                    }
                }
            },
            warmup: 2,
            iterations: 5));

        await UnloadAsync(tableView);
    }

    /// <summary>
    /// The single most diagnostic shape in this file: the same drag at 20 / 50 / 80 / 120 columns over the same
    /// 10k rows. Linear growth means a per-column constant that more columns simply multiply, and the answer is to
    /// shrink the constant. Super-linear growth means something in the tick is touching all columns for all rows,
    /// and the answer is to stop doing that — a very different fix. Reported as four separate rows so the CSV
    /// carries the curve.
    /// </summary>
    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Grid_HorizontalPan_ColumnSweep_WithLayout()
        => await ColumnSweepAsync(columnVirtualization: true, nameSuffix: "");

    /// <summary>
    /// The same curve in the control's default world. Virtualization on is meant to make the curve flat past the
    /// point where the band stops growing (the band is viewport-sized, not column-count-sized); off, there is
    /// nothing to flatten it. The gap between the two curves is the value of the virtualization opt-in.
    /// </summary>
    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Grid_HorizontalPan_ColumnSweep_WithLayout_NoColumnVirtualization()
        => await ColumnSweepAsync(columnVirtualization: false, nameSuffix: "_NoColumnVirtualization");

    private async Task ColumnSweepAsync(bool columnVirtualization, string nameSuffix)
    {
        int[] columnCounts = [20, 50, 80, 120];

        foreach (var columnCount in columnCounts)
        {
            await HorizontalPanWithLayoutAsync(
                columnCount,
                columnVirtualization,
                $"Grid_HorizontalPan_Sweep_{columnCount}Cols_100Ticks_WithLayout{nameSuffix}",
                step: SweepPanStep);
        }
    }

    /// <summary>
    /// Drives <see cref="PanTicks"/> horizontal offset changes, forcing a synchronous layout pass between each so
    /// measure and arrange land inside the measurement.
    /// </summary>
    private async Task HorizontalPanWithLayoutAsync(int columnCount, bool columnVirtualization, string benchmarkName, double step = PanStep)
    {
        var tableView = await LoadPanGridAsync(columnCount, columnVirtualization);

        Report(Measure(
            () =>
            {
                for (var i = 1; i <= PanTicks; i++)
                {
                    tableView.SetValue(TableView.HorizontalOffsetProperty, i * step);
                    tableView.UpdateLayout();
                }
            },
            warmup: 2,
            iterations: 5,
            reset: () =>
            {
                tableView.SetValue(TableView.HorizontalOffsetProperty, 0d);
                tableView.UpdateLayout();
            }),
            benchmarkName);

        await UnloadAsync(tableView);
    }

    /// <summary>
    /// The vertical mirror of <see cref="HorizontalPanWithLayoutAsync"/>: identical tick count and identical pixel
    /// step, but moved through the ScrollViewer so the platform's own virtualization path runs.
    /// </summary>
    private async Task VerticalPanWithLayoutAsync(int columnCount, bool columnVirtualization, string benchmarkName)
    {
        var tableView = await LoadPanGridAsync(columnCount, columnVirtualization);
        var scrollViewer = GetScrollViewer(tableView);

        Report(Measure(
            () =>
            {
                for (var i = 1; i <= PanTicks; i++)
                {
                    scrollViewer.ChangeView(null, i * PanStep, null, true);
                    tableView.UpdateLayout();
                }
            },
            warmup: 2,
            iterations: 5,
            reset: () =>
            {
                scrollViewer.ChangeView(null, 0d, null, true);
                tableView.UpdateLayout();
            }),
            benchmarkName);

        await UnloadAsync(tableView);
    }

    /// <summary>
    /// Drives <see cref="PanTicks"/> ticks along one axis, waiting for a real composition frame after each. Must
    /// use <see cref="MeasureAsync"/>, never <see cref="Measure"/>: blocking the UI thread on a frame it is itself
    /// responsible for producing would deadlock.
    /// </summary>
    private async Task RenderedPanAsync(int columnCount, bool columnVirtualization, bool horizontal, string benchmarkName)
    {
        var tableView = await LoadPanGridAsync(columnCount, columnVirtualization);
        ScrollViewer? scrollViewer = horizontal ? null : GetScrollViewer(tableView);

        var result = await MeasureAsync(
            async () =>
            {
                for (var i = 1; i <= PanTicks; i++)
                {
                    if (horizontal)
                    {
                        tableView.SetValue(TableView.HorizontalOffsetProperty, i * PanStep);
                    }
                    else
                    {
                        scrollViewer!.ChangeView(null, i * PanStep, null, true);
                    }

                    tableView.UpdateLayout();
                    await WaitForRenderAsync();
                }
            },
            warmup: 1,
            iterations: 3,
            reset: () =>
            {
                tableView.SetValue(TableView.HorizontalOffsetProperty, 0d);
                scrollViewer?.ChangeView(null, 0d, null, true);
                tableView.UpdateLayout();
            });

        Report(result, benchmarkName);
        await UnloadAsync(tableView);
    }

    /// <summary>
    /// A blotter-shaped grid: <paramref name="columnCount"/> fixed 100px columns over <see cref="RowCount"/> rows
    /// in a 1200x800 viewport. Column virtualization is a parameter rather than a constant because it is the
    /// biggest fork in the horizontal path, and because the control ships with it off.
    /// </summary>
    private static Task<TableView> LoadPanGridAsync(int columnCount, bool columnVirtualization, int frozenColumns = 0)
    {
        var items = new ObservableCollection<BenchItem>(
            Enumerable.Range(0, RowCount).Select(i => new BenchItem { Name = $"Item {i}", Value = i }));

        var tableView = new TableView
        {
            AutoGenerateColumns = false,
            IsColumnVirtualizationEnabled = columnVirtualization,
            RowHeight = 32,
            Width = 1200,
            Height = 800,
            SelectionMode = ListViewSelectionMode.Extended,
            FrozenColumnCount = frozenColumns,
            RowHeaderWidth = frozenColumns > 0 ? 40 : double.NaN,
        };

        tableView.Columns.AddRange(CreateColumns(columnCount));
        tableView.ItemsSource = items;

        return LoadAsync(tableView);

        static async Task<TableView> LoadAsync(TableView tableView)
        {
            await UnitTestApp.Current.MainWindow.LoadTestContentAsync(tableView);
            tableView.UpdateLayout();

            // A freshly loaded grid is still generating cell content: realization is debounced and then chunked
            // across dispatcher turns. Draining it here keeps that one-off out of every stopwatch below, so the
            // pan benchmarks measure the steady state a user scrolls in rather than first-render.
            await Task.Delay(RealizeSettleWaitMs);
            tableView.UpdateLayout();

            return tableView;
        }
    }

    /// <summary>
    /// The pan with FROZEN COLUMNS and a row header — the shape a real blotter has, and the one the other pan
    /// benchmarks miss entirely by leaving FrozenColumnCount at its default of 0.
    ///
    /// It matters because the two configurations exercise different halves of the fix. With nothing frozen the
    /// grid pans as a single visual and there is nothing to hold back. With frozen columns every realized row
    /// carries counter-translated chrome — the row-header group, the grid line, the frozen cells panel — so the
    /// per-row work the fix was meant to remove partly returns, as compositor expression evaluations rather than
    /// UI-thread writes. If this number is materially worse than its unfrozen twin, that is where it went.
    /// </summary>
    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Grid_HorizontalPan_80Cols_Frozen_100Frames_Rendered()
    {
        var tableView = await LoadPanGridAsync(WideColumnCount, columnVirtualization: true, frozenColumns: 3);

        var result = await MeasureAsync(
            async () =>
            {
                for (var i = 1; i <= PanTicks; i++)
                {
                    tableView.SetValue(TableView.HorizontalOffsetProperty, i * PanStep);
                    tableView.UpdateLayout();
                    await WaitForRenderAsync();
                }
            },
            warmup: 1,
            iterations: 3,
            reset: () =>
            {
                tableView.SetValue(TableView.HorizontalOffsetProperty, 0d);
                tableView.UpdateLayout();
            });

        Report(result, "Grid_HorizontalPan_80Cols_Frozen_100Frames_Rendered");
        await UnloadAsync(tableView);
    }

    /// <summary>
    /// The TableView's own template ScrollViewer — the element that owns VERTICAL scrolling, and the one the
    /// horizontal path deliberately bypasses (the template sets HorizontalScrollMode to Disabled).
    /// </summary>
    private static ScrollViewer GetScrollViewer(TableView tableView)
    {
        var scrollViewer = FindByName(tableView);

        // A structural precondition, not a timing one: if the template stops exposing it, a vertical benchmark
        // that silently measured nothing would be worse than a failing one.
        Assert.IsNotNull(scrollViewer, "The TableView template no longer contains a ScrollViewer named \"ScrollViewer\".");

        return scrollViewer!;

        static ScrollViewer? FindByName(DependencyObject element)
        {
            var count = VisualTreeHelper.GetChildrenCount(element);

            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(element, i);

                if (child is ScrollViewer { Name: "ScrollViewer" } found)
                {
                    return found;
                }

                if (FindByName(child) is { } descendant)
                {
                    return descendant;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Waits for one composition frame. UpdateLayout forces measure and arrange but NOT a render, and a scroll is
    /// judged in frames — on a remote session the rendered pixels are the expensive part and the only part that
    /// crosses the wire. A benchmark that never waits for a frame cannot see any of it.
    /// </summary>
    private static async Task WaitForRenderAsync()
    {
        var taskCompletionSource = new TaskCompletionSource<object?>();

        void Callback(object? sender, object args)
        {
            CompositionTarget.Rendering -= Callback;
            taskCompletionSource.SetResult(null);
        }

        CompositionTarget.Rendering += Callback;

        await taskCompletionSource.Task;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Dispatcher latency — what "laggy" actually is
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Elapsed time over a pan says what the gesture cost in total. It does not say whether the UI thread ever
    /// stopped answering, and that is what a user calls lag. It is also quantised by the display interval: a 7ms
    /// block and a 12ms block both round up to the same wait, so the frame-paced benchmarks can only see cost in
    /// whole frames.
    ///
    /// This measures the thing directly. A low-priority heartbeat re-enqueues itself continuously; the GAP
    /// between consecutive runs is how long the DispatcherQueue refused to pick up the next work item — i.e. how
    /// long input would have sat unhandled. On an idle thread that gap is a frame or less.
    ///
    /// The tail is the number that matters: one 40ms gap is a visible hitch, forty 1ms gaps are not. Median, p95
    /// and max are all reported, and the p95/max rows are the ones to watch for a regression.
    /// </summary>
    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Grid_HorizontalPan_80Cols_DispatcherGap()
        => await DispatcherGapAsync(horizontal: true, "Grid_HorizontalPan_80Cols_DispatcherGap");

    /// <summary>
    /// The control. Same grid, same tick count, down the platform ScrollViewer. "Vertical is fine" should mean
    /// the thread keeps answering while it scrolls; if this tail is as bad as the horizontal one, lag is not what
    /// distinguishes the two axes and the diagnosis needs rethinking.
    /// </summary>
    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Grid_VerticalPan_80Cols_DispatcherGap()
        => await DispatcherGapAsync(horizontal: false, "Grid_VerticalPan_80Cols_DispatcherGap");

    /// <summary>
    /// The floor: the same heartbeat with nothing scrolling. Everything above is only meaningful against this.
    /// </summary>
    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Grid_Idle_DispatcherGap()
        => await DispatcherGapAsync(horizontal: true, "Grid_Idle_DispatcherGap", pan: false);

    /// <summary>A gap longer than one frame is a dropped frame; anything under it is just scheduling.</summary>
    private const double FrameMs = 16.7;

    private async Task DispatcherGapAsync(bool horizontal, string benchmarkName, bool pan = true)
    {
        var tableView = await LoadPanGridAsync(WideColumnCount, columnVirtualization: true);
        var scrollViewer = horizontal ? null : GetScrollViewer(tableView);
        var queue = tableView.DispatcherQueue;
        var gaps = new List<double>();

        for (var iteration = 0; iteration < 4; iteration++)
        {
            var stopwatch = Stopwatch.StartNew();
            var last = 0d;
            var beating = true;
            var collected = iteration > 0 ? gaps : []; // iteration 0 is warmup; its gaps are discarded

            void Beat()
            {
                var now = stopwatch.Elapsed.TotalMilliseconds;
                collected.Add(now - last);
                last = now;

                if (beating)
                {
                    queue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Beat);
                }
            }

            queue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Beat);

            // Ticks are applied at frame cadence, the way a drag delivers them, rather than in a tight loop that
            // would never give the heartbeat a chance to run at all.
            for (var i = 1; i <= PanTicks; i++)
            {
                if (pan)
                {
                    if (horizontal)
                    {
                        tableView.SetValue(TableView.HorizontalOffsetProperty, i * PanStep);
                    }
                    else
                    {
                        scrollViewer!.ChangeView(null, i * PanStep, null, true);
                    }
                }

                await WaitForRenderAsync();
            }

            beating = false;

            tableView.SetValue(TableView.HorizontalOffsetProperty, 0d);
            scrollViewer?.ChangeView(null, 0d, null, true);
            tableView.UpdateLayout();
        }

        // Total time the thread was unavailable, counting only gaps longer than a frame. THIS is the number to
        // gate on. The pickup count and the percentiles are only meaningful once the thread is responsive enough
        // to be scheduled often: while it is saturated the heartbeat runs a handful of times, so those statistics
        // are computed over 3-5 samples and swing wildly between identical runs (3, 0, 39 have all been observed).
        // Blocked time does not care how many samples there were — it measures the thing the user feels.
        var blockedMs = gaps.Where(gap => gap > FrameMs).Sum();

        gaps.Sort();

        var median = Percentile(gaps, 0.50);
        var p95 = Percentile(gaps, 0.95);
        var max = gaps.Count > 0 ? gaps[^1] : 0d;

        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{benchmarkName}: BLOCKED {blockedMs:F0} ms total | {gaps.Count} gaps, median {median:F2} ms, p95 {p95:F2} ms, max {max:F2} ms"));

        Report(new BenchResult(blockedMs, blockedMs, blockedMs, gaps.Count), $"{benchmarkName}_BlockedMs");
        Report(new BenchResult(median, Percentile(gaps, 0.05), max, gaps.Count), benchmarkName);
        Report(new BenchResult(p95, p95, p95, gaps.Count), $"{benchmarkName}_P95");

        await UnloadAsync(tableView);
    }

    /// <summary>
    /// Nearest-rank percentile over an already-sorted list.
    /// </summary>
    private static double Percentile(List<double> sorted, double fraction)
    {
        if (sorted.Count == 0)
        {
            return 0d;
        }

        var index = (int)Math.Ceiling(fraction * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    // ---------------------------------------------------------------------------------------------------------
    // Selection
    // ---------------------------------------------------------------------------------------------------------

    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Selection_ScrollRowIntoView_RealizedRow_x50()
    {
        var tableView = await LoadGridAsync();

        var result = await MeasureAsync(async () =>
        {
            // Runs on every single-row selection. With the row already realized this must do zero source scans.
            for (var i = 0; i < 50; i++)
            {
                _ = await tableView.ScrollRowIntoView(i % 10);
            }
        }, warmup: 2, iterations: 5);

        Report(result);
        await UnloadAsync(tableView);
    }

    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Selection_SelectAll_Rows_10k()
    {
        var tableView = await LoadGridAsync();

        // Deselect between iterations, outside the stopwatch, so this measures the select side alone.
        Report(Measure(() => tableView.SelectAll(), warmup: 2, iterations: 5, reset: tableView.DeselectAll));

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Selection_DeselectAll_Rows_10k()
    {
        var tableView = await LoadGridAsync();

        Report(Measure(() => tableView.DeselectAll(), warmup: 2, iterations: 5, reset: tableView.SelectAll));

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Selection_SelectAll_Rows_10k_RawListView()
    {
        // Platform floor: a plain WinUI ListView with the same 10k source. If TableView's number matches this,
        // the cost is ListViewBase.SelectRange internals, not TableView code.
        var listView = new ListView
        {
            Width = 1200,
            Height = 800,
            SelectionMode = ListViewSelectionMode.Extended,
            ItemsSource = Enumerable.Range(0, RowCount).Select(i => new BenchItem { Name = $"Item {i}", Value = i }).ToList(),
        };

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(listView);

        Report(Measure(
            () => listView.SelectRange(new ItemIndexRange(0, (uint)RowCount)),
            warmup: 2,
            iterations: 5,
            reset: () => listView.DeselectRange(new ItemIndexRange(0, (uint)RowCount))));

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(listView);
    }

    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Selection_DeselectAll_Rows_10k_RawListView()
    {
        // Platform floor for clearing a full selection via DeselectRange.
        var listView = new ListView
        {
            Width = 1200,
            Height = 800,
            SelectionMode = ListViewSelectionMode.Extended,
            ItemsSource = Enumerable.Range(0, RowCount).Select(i => new BenchItem { Name = $"Item {i}", Value = i }).ToList(),
        };

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(listView);

        Report(Measure(
            () => listView.DeselectRange(new ItemIndexRange(0, (uint)RowCount)),
            warmup: 2,
            iterations: 5,
            reset: () => listView.SelectRange(new ItemIndexRange(0, (uint)RowCount))));

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(listView);
    }

    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Selection_DeselectAll_ModeFlip_RawListView()
    {
        // Candidate optimization: flipping SelectionMode resets the platform selection state wholesale instead of
        // tearing it down item-by-item via DeselectRange.
        var listView = new ListView
        {
            Width = 1200,
            Height = 800,
            SelectionMode = ListViewSelectionMode.Extended,
            ItemsSource = Enumerable.Range(0, RowCount).Select(i => new BenchItem { Name = $"Item {i}", Value = i }).ToList(),
        };

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(listView);

        Report(Measure(
            () =>
            {
                listView.SelectionMode = ListViewSelectionMode.None;
                listView.SelectionMode = ListViewSelectionMode.Extended;
            },
            warmup: 2,
            iterations: 5,
            reset: () => listView.SelectRange(new ItemIndexRange(0, (uint)RowCount))));

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(listView);
    }

    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Selection_DeselectAll_SelectedItemsClear_RawListView()
    {
        // Candidate optimization: clearing the SelectedItems vector directly.
        var listView = new ListView
        {
            Width = 1200,
            Height = 800,
            SelectionMode = ListViewSelectionMode.Extended,
            ItemsSource = Enumerable.Range(0, RowCount).Select(i => new BenchItem { Name = $"Item {i}", Value = i }).ToList(),
        };

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(listView);

        Report(Measure(
            () => listView.SelectedItems.Clear(),
            warmup: 2,
            iterations: 5,
            reset: () => listView.SelectRange(new ItemIndexRange(0, (uint)RowCount))));

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(listView);
    }

    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Selection_DeselectAll_SelectedIndexMinusOne_RawListView()
    {
        // Candidate optimization: SelectedIndex = -1 as the clear primitive.
        var listView = new ListView
        {
            Width = 1200,
            Height = 800,
            SelectionMode = ListViewSelectionMode.Extended,
            ItemsSource = Enumerable.Range(0, RowCount).Select(i => new BenchItem { Name = $"Item {i}", Value = i }).ToList(),
        };

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(listView);

        Report(Measure(
            () => listView.SelectedIndex = -1,
            warmup: 2,
            iterations: 5,
            reset: () => listView.SelectRange(new ItemIndexRange(0, (uint)RowCount))));

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(listView);
    }

    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Selection_SelectAll_Rows_10k_SelectionInfoSource()
    {
        // The escape from the ~700ms platform teardown: a direct-mode (UseCollectionView=false) source that
        // implements ISelectionInfo, so ListViewBase delegates selection bookkeeping to the source.
        var (tableView, _) = await LoadSelectionInfoGridAsync();

        Report(Measure(() => tableView.SelectAll(), warmup: 2, iterations: 5, reset: tableView.DeselectAll));

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Selection_DeselectAll_Rows_10k_SelectionInfoSource()
    {
        var (tableView, _) = await LoadSelectionInfoGridAsync();

        Report(Measure(() => tableView.DeselectAll(), warmup: 2, iterations: 5, reset: tableView.SelectAll));

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Probe_SelectionInfoSource_EndToEnd()
    {
        // Verifies the ISelectionInfo integration facts the app needs before adopting it:
        // delegation, SelectedRanges/SelectedValues, container visuals, SelectedIndex/SelectionChanged behavior.
        var (tableView, source) = await LoadSelectionInfoGridAsync();

        var selectionChangedCount = 0;
        tableView.SelectionChanged += (_, _) => selectionChangedCount++;

        tableView.SelectAll();
        await Task.Yield();

        var afterSelectAll =
            $"selectAll: delegated={source.SelectRangeCalls == 1}, ranges={tableView.SelectedRanges.Count}, " +
            $"len={tableView.SelectedRanges.Sum(r => (long)r.Length)}, firstValue={(tableView.SelectedValues.FirstOrDefault() as BenchItem)?.Name}, " +
            $"row0Selected={(tableView.ContainerFromIndex(0) as TableViewRow)?.IsSelected}, events={selectionChangedCount}";

        Assert.AreEqual(1, tableView.SelectedRanges.Count);
        Assert.AreEqual(RowCount, (int)tableView.SelectedRanges[0].Length);
        Assert.IsTrue(source.IsSelected(0) && source.IsSelected(RowCount - 1));

        tableView.DeselectAll();
        await Task.Yield();

        Assert.AreEqual(0, tableView.SelectedRanges.Count);
        Assert.IsFalse(source.IsSelected(0));

        // Single-click row selection goes through SelectedIndex — verify it routes into the source too.
        tableView.SelectedIndex = 5;
        await Task.Yield();

        var afterSingle =
            $"singleSelect: selectedIndexWorks={source.IsSelected(5)}, ranges={tableView.SelectedRanges.Count}, " +
            $"value={(tableView.SelectedValues.FirstOrDefault() as BenchItem)?.Name}, events={selectionChangedCount}";

        var findings = afterSelectAll + Environment.NewLine + afterSingle;
        TestContext.WriteLine(findings);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "WinUI.TableView.Probe.txt"), findings);

        await UnloadAsync(tableView);
    }

    /// <summary>
    /// Loads a direct-mode grid whose source implements <see cref="ISelectionInfo"/>.
    /// </summary>
    private static async Task<(TableView TableView, SelectionInfoList Source)> LoadSelectionInfoGridAsync()
    {
        var source = new SelectionInfoList(Enumerable.Range(0, RowCount).Select(i => new BenchItem { Name = $"Item {i}", Value = i }));

        var tableView = new TableView
        {
            AutoGenerateColumns = false,
            IsColumnVirtualizationEnabled = true,
            RowHeight = 32,
            Width = 1200,
            Height = 800,
            SelectionMode = ListViewSelectionMode.Extended,
            UseCollectionView = false,
        };

        tableView.Columns.AddRange(CreateColumns(ColumnCount));
        tableView.ItemsSource = source;

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(tableView);
        tableView.UpdateLayout();

        return (tableView, source);
    }

    /// <summary>
    /// Reference ISelectionInfo source for direct mode: a read-only list that owns its selection state as sorted,
    /// disjoint, inclusive index intervals. SelectRange/DeselectRange are O(ranges) interval merges/splits instead
    /// of the platform's per-item teardown; IsSelected is a binary search. This mirrors what an app-side view over
    /// dynamic data needs to implement (plus shifting the intervals on insert/remove, and re-mapping or clearing
    /// them on resort/refilter, which static benchmark data doesn't exercise).
    /// </summary>
    private sealed class SelectionInfoList : IList, INotifyCollectionChanged, ISelectionInfo, IItemsRangeInfo
    {
        private readonly List<BenchItem> _items;
        private readonly List<(int First, int Last)> _ranges = [];

        public SelectionInfoList(IEnumerable<BenchItem> items) => _items = [.. items];

        public event NotifyCollectionChangedEventHandler? CollectionChanged;

        public int SelectRangeCalls { get; private set; }

        // ---- IItemsRangeInfo: the platform reports which rows are visible/buffered on every viewport change.
        // A real source would feed this straight into its update limiter / data-virtualization pager; keep the
        // handler trivial — it runs on the UI thread during scrolling.

        public int RangesChangedCalls { get; private set; }
        public ItemIndexRange? LastVisibleRange { get; private set; }
        public IReadOnlyList<ItemIndexRange> LastTrackedItems { get; private set; } = [];

        public void RangesChanged(ItemIndexRange visibleRange, IReadOnlyList<ItemIndexRange> trackedItems)
        {
            RangesChangedCalls++;
            LastVisibleRange = visibleRange;
            LastTrackedItems = trackedItems;
        }

        public void Dispose()
        {
        }

        // ---- ISelectionInfo ----

        public void SelectRange(ItemIndexRange itemIndexRange)
        {
            SelectRangeCalls++;
            var first = itemIndexRange.FirstIndex;
            var last = itemIndexRange.LastIndex;
            var merged = new List<(int First, int Last)>();
            var i = 0;

            while (i < _ranges.Count && _ranges[i].Last < first - 1) merged.Add(_ranges[i++]);
            while (i < _ranges.Count && _ranges[i].First <= last + 1)
            {
                first = Math.Min(first, _ranges[i].First);
                last = Math.Max(last, _ranges[i].Last);
                i++;
            }
            merged.Add((first, last));
            while (i < _ranges.Count) merged.Add(_ranges[i++]);

            _ranges.Clear();
            _ranges.AddRange(merged);
        }

        public void DeselectRange(ItemIndexRange itemIndexRange)
        {
            var first = itemIndexRange.FirstIndex;
            var last = itemIndexRange.LastIndex;
            var split = new List<(int First, int Last)>();

            foreach (var range in _ranges)
            {
                if (range.Last < first || range.First > last)
                {
                    split.Add(range);
                    continue;
                }

                if (range.First < first) split.Add((range.First, first - 1));
                if (range.Last > last) split.Add((last + 1, range.Last));
            }

            _ranges.Clear();
            _ranges.AddRange(split);
        }

        public bool IsSelected(int index)
        {
            var lo = 0;
            var hi = _ranges.Count - 1;

            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                if (index < _ranges[mid].First) hi = mid - 1;
                else if (index > _ranges[mid].Last) lo = mid + 1;
                else return true;
            }

            return false;
        }

        public IReadOnlyList<ItemIndexRange> GetSelectedRanges()
            => [.. _ranges.Select(r => new ItemIndexRange(r.First, (uint)(r.Last - r.First + 1)))];

        // ---- IList (read-only view over the items) ----

        public object? this[int index] { get => _items[index]; set => throw new NotSupportedException(); }
        public bool IsFixedSize => true;
        public bool IsReadOnly => true;
        public int Count => _items.Count;
        public bool IsSynchronized => false;
        public object SyncRoot => this;
        public int Add(object? value) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(object? value) => value is BenchItem item && _items.Contains(item);
        public void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
        public IEnumerator GetEnumerator() => _items.GetEnumerator();
        public int IndexOf(object? value) => value is BenchItem item ? _items.IndexOf(item) : -1;
        public void Insert(int index, object? value) => throw new NotSupportedException();
        public void Remove(object? value) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
    }

    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Probe_ItemsRangeInfo_ReportsVisibleRows()
    {
        // Verifies that a direct-mode source implementing IItemsRangeInfo gets authoritative visible/buffered row
        // ranges from the platform — the official feed for "only update visible rows" throttling.
        var (tableView, source) = await LoadSelectionInfoGridAsync();
        await Task.Delay(100);

        var initialCalls = source.RangesChangedCalls;
        var initial = source.LastVisibleRange;

        _ = await tableView.ScrollRowIntoView(5_000);
        await Task.Delay(250);

        var afterScroll = source.LastVisibleRange;
        var tracked = string.Join(";", source.LastTrackedItems.Select(r => $"{r.FirstIndex}-{r.LastIndex}"));
        var findings =
            $"itemsRangeInfo: initialCalls={initialCalls}, initialVisible={initial?.FirstIndex}..{initial?.LastIndex}, " +
            $"callsAfterScroll={source.RangesChangedCalls}, visibleAfterScroll={afterScroll?.FirstIndex}..{afterScroll?.LastIndex}, tracked={tracked}";

        TestContext.WriteLine(findings);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "WinUI.TableView.Probe.txt"), findings);

        // The platform must have reported ranges on load, and again around row 5000 after the scroll.
        Assert.IsTrue(initialCalls > 0, "RangesChanged was never called on load");
        Assert.IsNotNull(afterScroll);
        Assert.IsTrue(source.RangesChangedCalls > initialCalls, "RangesChanged did not fire on scroll");
        Assert.IsTrue(afterScroll!.FirstIndex <= 5_000 && afterScroll.LastIndex >= 5_000,
            $"visible range {afterScroll.FirstIndex}..{afterScroll.LastIndex} does not contain the scrolled-to row");

        await UnloadAsync(tableView);
    }

    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Probe_SelectionModeFlip_Behavior()
    {
        // Not a benchmark: documents whether a SelectionMode flip raises SelectionChanged (and with what
        // RemovedItems), which decides if it can replace DeselectRange without breaking subscribers.
        var listView = new ListView
        {
            Width = 1200,
            Height = 800,
            SelectionMode = ListViewSelectionMode.Extended,
            ItemsSource = Enumerable.Range(0, 100).Select(i => new BenchItem { Name = $"Item {i}", Value = i }).ToList(),
        };

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(listView);

        listView.SelectRange(new ItemIndexRange(0, 100));
        var events = 0;
        var removedCount = -1;
        listView.SelectionChanged += (_, e) => { events++; removedCount = e.RemovedItems.Count; };

        listView.SelectionMode = ListViewSelectionMode.None;
        listView.SelectionMode = ListViewSelectionMode.Extended;
        await Task.Yield();

        var findings =
            $"modeFlip: events={events}, lastRemovedItems={removedCount}, selectedAfter={listView.SelectedItems.Count}, rangesAfter={listView.SelectedRanges.Count}";
        TestContext.WriteLine(findings);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "WinUI.TableView.Probe.txt"), findings);

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(listView);
    }

    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Selection_SelectAllCells_10kRows_50Cols()
    {
        var tableView = await LoadGridAsync();
        tableView.SelectionUnit = TableViewSelectionUnit.Cell;

        Report(Measure(() =>
        {
            // Documents the known cost of the materialized cell-selection model (rows x columns slots). If this
            // is ever reworked to rectangle ranges, this number should collapse to ~0.
            tableView.SelectAll();
            tableView.DeselectAll();
        }, warmup: 1, iterations: 3));

        await UnloadAsync(tableView);
    }

    [TestMethod]
    [TestCategory("Benchmark")]
    public void Tree_StreamingChildInserts_2000_Into50kVisibleRows()
    {
        // The heavy-streaming tree case: 100 expanded branches x 500 children = ~50k visible rows, then 2000
        // children inserted at random branches/positions. Order-statistic backing keeps each insert O(log n);
        // a naive flat list would scan/shift ~50k rows per insert.
        var branches = new List<ObservableCollection<ITableViewTreeItem>>();
        var roots = new ObservableCollection<ITableViewTreeItem>();

        for (var r = 0; r < 100; r++)
        {
            var children = new ObservableCollection<ITableViewTreeItem>(
                Enumerable.Range(0, 500).Select(i => (ITableViewTreeItem)new BenchTreeNode($"R{r}C{i}", 1)));
            branches.Add(children);
            roots.Add(new BenchTreeNode($"R{r}", 0) { Children = children, IsExpanded = true });
        }

        using var source = new TreeTableViewSource(roots);
        var random = new Random(42);
        var inserted = new List<(ObservableCollection<ITableViewTreeItem> Branch, BenchTreeNode Node)>();

        Report(Measure(
            () =>
            {
                for (var i = 0; i < 2_000; i++)
                {
                    var branch = branches[random.Next(branches.Count)];
                    var node = new BenchTreeNode($"S{i}", 1);
                    branch.Insert(random.Next(branch.Count + 1), node);
                    inserted.Add((branch, node));
                }
            },
            warmup: 1,
            iterations: 3,
            reset: () =>
            {
                foreach (var (branch, node) in inserted)
                {
                    branch.Remove(node);
                }

                inserted.Clear();
            }));
    }

    [TestMethod]
    [TestCategory("Benchmark")]
    public void Tree_HugeWideBranch_500kChildren_StreamInserts_1000()
    {
        // Worst case for the branch shadow (a List): one enormous expanded branch. Every streamed insert pays a
        // shadow memmove proportional to the branch size, on top of the O(log n) flat-tree work.
        var children = new ObservableCollection<ITableViewTreeItem>(
            Enumerable.Range(0, 500_000).Select(i => (ITableViewTreeItem)new BenchTreeNode($"C{i}", 1)));
        var roots = new ObservableCollection<ITableViewTreeItem>
        {
            new BenchTreeNode("R", 0) { Children = children, IsExpanded = true },
        };

        using var source = new TreeTableViewSource(roots);
        var random = new Random(42);
        var inserted = new List<BenchTreeNode>();

        Report(Measure(
            () =>
            {
                for (var i = 0; i < 1_000; i++)
                {
                    var node = new BenchTreeNode($"S{i}", 1);
                    children.Insert(random.Next(children.Count + 1), node);
                    inserted.Add(node);
                }
            },
            warmup: 1,
            iterations: 3,
            reset: () =>
            {
                foreach (var node in inserted)
                {
                    children.Remove(node);
                }

                inserted.Clear();
            }));
    }

    [TestMethod]
    [TestCategory("Benchmark")]
    public void Tree_ManyBranches_500x1000_StreamInserts_2000()
    {
        // The app's stated shape: many expanded branches (500 x 1000 = ~500k visible rows), streaming inserts
        // spread across all of them. Shadow memmoves stay small (1k children); flat-tree work is O(log 500k).
        var branches = new List<ObservableCollection<ITableViewTreeItem>>();
        var roots = new ObservableCollection<ITableViewTreeItem>();

        for (var r = 0; r < 500; r++)
        {
            var children = new ObservableCollection<ITableViewTreeItem>(
                Enumerable.Range(0, 1_000).Select(i => (ITableViewTreeItem)new BenchTreeNode($"R{r}C{i}", 1)));
            branches.Add(children);
            roots.Add(new BenchTreeNode($"R{r}", 0) { Children = children, IsExpanded = true });
        }

        using var source = new TreeTableViewSource(roots);
        var random = new Random(42);
        var inserted = new List<(ObservableCollection<ITableViewTreeItem> Branch, BenchTreeNode Node)>();

        Report(Measure(
            () =>
            {
                for (var i = 0; i < 2_000; i++)
                {
                    var branch = branches[random.Next(branches.Count)];
                    var node = new BenchTreeNode($"S{i}", 1);
                    branch.Insert(random.Next(branch.Count + 1), node);
                    inserted.Add((branch, node));
                }
            },
            warmup: 1,
            iterations: 3,
            reset: () =>
            {
                foreach (var (branch, node) in inserted)
                {
                    branch.Remove(node);
                }

                inserted.Clear();
            }));
    }

    [TestMethod]
    [TestCategory("Benchmark")]
    public void Tree_CollapseThenExpand_100kChildBranch()
    {
        // Wholesale open/close of a huge branch: 100k row removals + 100k row insertions with per-row events.
        var children = new ObservableCollection<ITableViewTreeItem>(
            Enumerable.Range(0, 100_000).Select(i => (ITableViewTreeItem)new BenchTreeNode($"C{i}", 1)));
        var rootNode = new BenchTreeNode("R", 0) { Children = children, IsExpanded = true };
        var roots = new ObservableCollection<ITableViewTreeItem> { rootNode };

        using var source = new TreeTableViewSource(roots);

        Report(Measure(
            () =>
            {
                source.Collapse(rootNode);
                source.Expand(rootNode);
            },
            warmup: 1,
            iterations: 3));
    }

    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Tree_IndexOf_AdapterVsPlatformItems_200kRows()
    {
        // Answers "is Items.IndexOf actually fast?": resolve the LAST of ~200k rows 100 times via the adapter's
        // handle map vs via the platform ItemCollection. RequestExpandCollapse(item, expand) uses the adapter path.
        var roots = new ObservableCollection<ITableViewTreeItem>();
        for (var r = 0; r < 200; r++)
        {
            var children = new ObservableCollection<ITableViewTreeItem>(
                Enumerable.Range(0, 1_000).Select(i => (ITableViewTreeItem)new BenchTreeNode($"R{r}C{i}", 1)));
            roots.Add(new BenchTreeNode($"R{r}", 0) { Children = children, IsExpanded = true });
        }

        using var source = new TreeTableViewSource(roots);
        var lastItem = source[source.Count - 1];

        var treeTableView = new TreeTableView
        {
            AutoGenerateColumns = false,
            UseCollectionView = false,
            Width = 1200,
            Height = 800,
        };
        treeTableView.Columns.Add(new TableViewTreeColumn
        {
            Header = "Name",
            Width = new GridLength(200, GridUnitType.Pixel),
            Binding = new Binding { Path = new PropertyPath(nameof(BenchTreeNode.Name)) },
        });
        treeTableView.ItemsSource = source;
        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(treeTableView);

        Report(Measure(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                _ = source.IndexOf(lastItem);
            }
        }, warmup: 2, iterations: 5), "Tree_IndexOf_Adapter_100x_200kRows");

        Report(Measure(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                _ = treeTableView.Items.IndexOf(lastItem);
            }
        }, warmup: 2, iterations: 5), "Tree_IndexOf_PlatformItems_100x_200kRows");

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(treeTableView);
    }

    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Tree_CollapseBoundGrid_5kChildren_PerRowEvents()
        => await CollapseBoundGridAsync(bulkThreshold: int.MaxValue, "Tree_CollapseBoundGrid_5k_PerRowEvents");

    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Tree_CollapseBoundGrid_5kChildren_Coalesced()
        => await CollapseBoundGridAsync(bulkThreshold: 32, "Tree_CollapseBoundGrid_5k_Coalesced");

    /// <summary>
    /// Collapses a large branch while BOUND TO A LIVE GRID — the cost the user actually feels, since every row
    /// notification makes the host run a virtualization + measure pass. The threshold selects per-row vs coalesced.
    /// </summary>
    private async Task CollapseBoundGridAsync(int bulkThreshold, string benchmarkName)
    {
        var children = new ObservableCollection<ITableViewTreeItem>(
            Enumerable.Range(0, 5_000).Select(i => (ITableViewTreeItem)new BenchTreeNode($"C{i}", 1)));
        var rootNode = new BenchTreeNode("R", 0) { Children = children, IsExpanded = true };
        var roots = new ObservableCollection<ITableViewTreeItem> { rootNode };

        var treeView = new TreeTableView
        {
            AutoGenerateColumns = false,
            RowHeight = 32,
            Width = 1000,
            Height = 600,
        };
        treeView.Columns.Add(new TableViewTreeColumn
        {
            Header = "Name",
            Width = new GridLength(300, GridUnitType.Pixel),
            Binding = new Binding { Path = new PropertyPath(nameof(BenchTreeNode.Name)) },
        });
        treeView.TreeItemsSource = roots;
        treeView.TreeSource!.BulkChangeThreshold = bulkThreshold;

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(treeView);
        treeView.UpdateLayout();

        var source = treeView.TreeSource!;

        Report(Measure(
            () =>
            {
                source.Collapse(rootNode);
                treeView.UpdateLayout(); // force the host to process the change synchronously
            },
            warmup: 1,
            iterations: 3,
            reset: () =>
            {
                source.Expand(rootNode);
                treeView.UpdateLayout();
            }),
            benchmarkName);

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(treeView);
    }

    // Only the COALESCED variants run. The per-row-event counterparts measured the pre-fix behaviour by replaying
    // one notification per row into a live 100k grid — seconds per iteration, minutes per run, and nothing to guard
    // against (a regression shows up as the coalesced number exploding). Recorded baselines, 2026-08-03 Release:
    // expand 3004ms, collapse 4140ms, app-driven removal 1462ms.

    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Tree_50Groups_Expand100k_Coalesced()
        => await BigTreeAsync(32, expand: true, "Tree_50Groups_Expand100k_Coalesced");

    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Tree_50Groups_Collapse100k_Coalesced()
        => await BigTreeAsync(32, expand: false, "Tree_50Groups_Collapse100k_Coalesced");

    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Tree_AppRemovesChildrenItself_100k_WithBulkScope()
        => await AppDrivenRemovalAsync(useBulkScope: true, "Tree_AppRemovesChildren_100k_WithBulkScope");

    /// <summary>
    /// Clearing a branch by raising ONE reset from the children collection — no bulk scope. Both the
    /// <see cref="IObservableVector{T}"/> and the INotifyCollectionChanged reset land in the same RebuildBranch,
    /// which coalesces above BulkChangeThreshold on its own; this measures that the caller needs nothing extra.
    /// </summary>
    [UITestMethod]
    [TestCategory("Benchmark")]
    public async Task Tree_ClearBranchViaReset_100k_NoBulkScope()
    {
        var children = new ObservableCollection<ITableViewTreeItem>(
            Enumerable.Range(0, 100_000).Select(i => (ITableViewTreeItem)new BenchTreeNode($"C{i}", 1)));
        var snapshot = children.ToList();
        var group = new BenchTreeNode("G", 0) { Children = children };

        var treeView = new TreeTableView
        {
            AutoGenerateColumns = false,
            RowHeight = 32,
            Width = 1000,
            Height = 600,
        };
        treeView.Columns.Add(new TableViewTreeColumn
        {
            Header = "Name",
            Width = new GridLength(300, GridUnitType.Pixel),
            Binding = new Binding { Path = new PropertyPath(nameof(BenchTreeNode.Name)) },
        });
        treeView.TreeItemsSource = new ObservableCollection<ITableViewTreeItem> { group };

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(treeView);
        treeView.UpdateLayout();

        var source = treeView.TreeSource!;
        source.Expand(group);
        treeView.UpdateLayout();

        Report(Measure(
            () =>
            {
                children.Clear(); // one reset, no scope
                treeView.UpdateLayout();
            },
            warmup: 0,
            iterations: 3,
            reset: () =>
            {
                using (source.BeginBulkUpdate())
                {
                    for (var i = children.Count; i < snapshot.Count; i++)
                    {
                        children.Add(snapshot[i]);
                    }
                }

                treeView.UpdateLayout();
            }),
            "Tree_ClearBranchViaReset_100k_NoBulkScope");

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(treeView);
    }

    /// <summary>
    /// The app's ACTUAL collapse pattern: instead of calling Collapse on the adapter, the view model empties its
    /// own children collection. Expand/Collapse coalescing does not cover this — only a bulk scope does. Drop the
    /// scope to reproduce the unguarded cost (~1.5s at this size); it is not run automatically because replaying
    /// 100k individual notifications into a live grid costs seconds per iteration.
    /// </summary>
    private async Task AppDrivenRemovalAsync(bool useBulkScope, string benchmarkName)
    {
        var children = new ObservableCollection<ITableViewTreeItem>(
            Enumerable.Range(0, 100_000).Select(i => (ITableViewTreeItem)new BenchTreeNode($"C{i}", 1)));
        var snapshot = children.ToList();
        var group = new BenchTreeNode("G", 0) { Children = children };

        var treeView = new TreeTableView
        {
            AutoGenerateColumns = false,
            RowHeight = 32,
            Width = 1000,
            Height = 600,
        };
        treeView.Columns.Add(new TableViewTreeColumn
        {
            Header = "Name",
            Width = new GridLength(300, GridUnitType.Pixel),
            Binding = new Binding { Path = new PropertyPath(nameof(BenchTreeNode.Name)) },
        });
        treeView.TreeItemsSource = new ObservableCollection<ITableViewTreeItem> { group };

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(treeView);
        treeView.UpdateLayout();

        var source = treeView.TreeSource!;
        source.Expand(group);
        treeView.UpdateLayout();

        Report(Measure(
            () =>
            {
                // ObservableCollection has no range API, so the app empties it item by item — exactly what a
                // view model doing its own teardown produces.
                if (useBulkScope)
                {
                    using (source.BeginBulkUpdate())
                    {
                        while (children.Count > 0)
                        {
                            children.RemoveAt(children.Count - 1);
                        }
                    }
                }
                else
                {
                    while (children.Count > 0)
                    {
                        children.RemoveAt(children.Count - 1);
                    }
                }

                treeView.UpdateLayout();
            },
            warmup: 0,
            iterations: 3,
            reset: () =>
            {
                // The action always empties from the end, so what is left is a prefix of the snapshot. Top it back
                // up rather than re-adding blindly (reset also runs before the first iteration).
                using (source.BeginBulkUpdate())
                {
                    for (var i = children.Count; i < snapshot.Count; i++)
                    {
                        children.Add(snapshot[i]);
                    }
                }

                treeView.UpdateLayout();
            }),
            benchmarkName);

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(treeView);
    }

    /// <summary>
    /// The app's shape: 50 top-level groups, one of them holding 100 000 children, bound to a live grid. Measures
    /// either the expand or the collapse of that group, with per-row notifications or coalesced ones.
    /// </summary>
    private async Task BigTreeAsync(int bulkThreshold, bool expand, string benchmarkName)
    {
        var roots = new ObservableCollection<ITableViewTreeItem>();
        BenchTreeNode? bigGroup = null;

        for (var g = 0; g < 50; g++)
        {
            // Only the measured group is populated: 50 x 100k nodes would be 5M objects of pure setup cost.
            var children = new ObservableCollection<ITableViewTreeItem>(
                g == 0
                    ? Enumerable.Range(0, 100_000).Select(i => (ITableViewTreeItem)new BenchTreeNode($"G0C{i}", 1))
                    : []);

            var group = new BenchTreeNode($"G{g}", 0) { Children = children };
            roots.Add(group);
            bigGroup ??= group;
        }

        var treeView = new TreeTableView
        {
            AutoGenerateColumns = false,
            RowHeight = 32,
            Width = 1000,
            Height = 600,
        };
        treeView.Columns.Add(new TableViewTreeColumn
        {
            Header = "Name",
            Width = new GridLength(300, GridUnitType.Pixel),
            Binding = new Binding { Path = new PropertyPath(nameof(BenchTreeNode.Name)) },
        });
        treeView.TreeItemsSource = roots;
        treeView.TreeSource!.BulkChangeThreshold = bulkThreshold;

        await UnitTestApp.Current.MainWindow.LoadTestContentAsync(treeView);
        treeView.UpdateLayout();

        var source = treeView.TreeSource!;
        var target = bigGroup!;

        if (!expand)
        {
            source.Expand(target); // collapse benchmark starts from the expanded state
            treeView.UpdateLayout();
        }

        Report(Measure(
            () =>
            {
                if (expand)
                {
                    source.Expand(target);
                }
                else
                {
                    source.Collapse(target);
                }

                treeView.UpdateLayout(); // make the host process the change inside the measurement
            },
            warmup: 1,
            iterations: 3,
            reset: () =>
            {
                if (expand)
                {
                    source.Collapse(target);
                }
                else
                {
                    source.Expand(target);
                }

                treeView.UpdateLayout();
            }),
            benchmarkName);

        await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(treeView);
    }

    private sealed class BenchTreeNode(string name, int depth) : ITableViewTreeItem
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name { get; } = name;
        public int Depth { get; } = depth;
        public ObservableCollection<ITableViewTreeItem>? Children { get; init; }
        public System.Collections.IEnumerable? ChildrenSource => Children;
        public bool HasChildren => Children is { Count: > 0 };
        public bool IsFinalItem => false;
        public bool IsExpanded { get; set; }
        public bool IsLoading => false;

        private void OnPropertyChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------------------

    private static IEnumerable<TableViewColumn> CreateColumns(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return new TableViewTextColumn
            {
                Header = $"Column {i}",
                Width = new GridLength(100, GridUnitType.Pixel),
                Binding = new Binding { Path = new PropertyPath(i % 2 == 0 ? nameof(BenchItem.Name) : nameof(BenchItem.Value)) },
            };
        }
    }

    private static Task<TableView> LoadGridAsync(bool useCollectionView = true) => LoadGridAsync(out _, useCollectionView);

    private static Task<TableView> LoadGridAsync(out ObservableCollection<BenchItem> items, bool useCollectionView = true)
    {
        items = [.. Enumerable.Range(0, RowCount).Select(i => new BenchItem { Name = $"Item {i}", Value = i })];

        var tableView = new TableView
        {
            AutoGenerateColumns = false,
            IsColumnVirtualizationEnabled = true,
            RowHeight = 32,
            Width = 1200,
            Height = 800,
            SelectionMode = ListViewSelectionMode.Extended,
            UseCollectionView = useCollectionView,
        };

        tableView.Columns.AddRange(CreateColumns(ColumnCount));
        tableView.ItemsSource = items;

        return LoadAsync(tableView);

        static async Task<TableView> LoadAsync(TableView tableView)
        {
            await UnitTestApp.Current.MainWindow.LoadTestContentAsync(tableView);
            tableView.UpdateLayout();
            return tableView;
        }
    }

    private static async Task UnloadAsync(TableView tableView)
        => await UnitTestApp.Current.MainWindow.UnloadTestContentAsync(tableView);

    private static BenchResult Measure(Action action, int warmup, int iterations, Action? reset = null)
        => MeasureAsync(() => { action(); return Task.CompletedTask; }, warmup, iterations, reset).GetAwaiter().GetResult();

    private static async Task<BenchResult> MeasureAsync(Func<Task> action, int warmup, int iterations, Action? reset = null)
    {
        for (var i = 0; i < warmup; i++)
        {
            reset?.Invoke();
            await action();
        }

        var samples = new double[iterations];

        for (var i = 0; i < iterations; i++)
        {
            // Restore the pre-action state outside the stopwatch so only the action itself is measured.
            reset?.Invoke();

            // Isolate iterations from each other's garbage so a collection doesn't land inside one sample.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var stopwatch = Stopwatch.StartNew();
            await action();
            stopwatch.Stop();

            samples[i] = stopwatch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);

        return new BenchResult(
            Median: samples[samples.Length / 2],
            Min: samples[0],
            Max: samples[^1],
            Iterations: iterations);
    }

    private void Report(BenchResult result, string? benchmarkName = null)
    {
        var name = benchmarkName ?? TestContext.TestName ?? "unknown";
#if DEBUG
        const string configuration = "Debug";
        TestContext.WriteLine("WARNING: Debug build — timings are NOT representative. Benchmark in Release.");
#else
        const string configuration = "Release";
#endif
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{name} [{configuration}] median {result.Median:F2} ms (min {result.Min:F2}, max {result.Max:F2}, n={result.Iterations})");
        TestContext.WriteLine(line);

        // Append to a machine-local CSV so runs can be diffed over time.
        var csvPath = Path.Combine(Path.GetTempPath(), "WinUI.TableView.Benchmarks.csv");
        var csvLine = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{configuration},{name},{result.Median:F3},{result.Min:F3},{result.Max:F3},{result.Iterations}");

        try
        {
            if (!File.Exists(csvPath))
            {
                File.AppendAllLines(csvPath, ["timestamp,configuration,benchmark,median_ms,min_ms,max_ms,iterations"]);
            }

            File.AppendAllLines(csvPath, [csvLine]);
        }
        catch (IOException)
        {
            // Concurrent writers or a locked file must not fail a benchmark run; the TestContext output remains.
        }
    }

    private readonly record struct BenchResult(double Median, double Min, double Max, int Iterations);

    private sealed class BenchItem : INotifyPropertyChanged
    {
        private double _value;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name { get; set; } = string.Empty;

        /// <summary>A low-cardinality key, so grouping produces ~50 groups rather than one per row.</summary>
        public string Bucket => $"Bucket {(int)_value % 50:D2}";

        public double Value
        {
            get => _value;
            set
            {
                if (_value != value)
                {
                    _value = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
                }
            }
        }
    }
}
