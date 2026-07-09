using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
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
    private sealed class SelectionInfoList : IList, INotifyCollectionChanged, ISelectionInfo
    {
        private readonly List<BenchItem> _items;
        private readonly List<(int First, int Last)> _ranges = [];

        public SelectionInfoList(IEnumerable<BenchItem> items) => _items = [.. items];

        public event NotifyCollectionChangedEventHandler? CollectionChanged;

        public int SelectRangeCalls { get; private set; }

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

    private void Report(BenchResult result)
    {
        var name = TestContext.TestName ?? "unknown";
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
