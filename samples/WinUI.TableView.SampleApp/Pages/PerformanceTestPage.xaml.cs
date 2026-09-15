using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace WinUI.TableView.SampleApp.Pages;

/// <summary>
/// A blotter-shaped grid — 70 columns, a million rows, a feed mutating the visible cells — that exists to be
/// scrolled while the numbers move, and to compare the grid's performance knobs on the machine that matters.
/// </summary>
public sealed partial class PerformanceTestPage : Page
{
    private const int RowCount = 1_000_000;
    private const int TickIntervalMs = 16; // ~60 batches a second; the requested rate is spread across them

    private readonly Random _random = new(0x5EED);
    private DispatcherQueueTimer? _ticker;
    private DispatcherQueueTimer? _statusTimer;
    private List<PerfRow>? _rows;
    private long _appliedThisSecond;
    private double _loadMs;

    public PerformanceTestPage()
    {
        InitializeComponent();

        // A row-number column, then the 69 value columns: 70 in all. Generated, because seventy column
        // declarations are not worth reading in XAML. Built into a list and added in one call: each individual
        // Add re-runs the frozen-column pass and drops every cached column projection, so adding seventy columns
        // one at a time is quadratic in the column count.
        var columns = new List<TableViewColumn>(PerfRow.ValueCount + 1)
        {
            new TableViewNumberColumn
            {
                Header = "Row",
                Width = new GridLength(80),
                Binding = new Binding { Path = new PropertyPath(nameof(PerfRow.Index)) },
            },
        };

        for (var i = 0; i < PerfRow.ValueCount; i++)
        {
            columns.Add(new TableViewNumberColumn
            {
                Header = PerfRow.Names[i],
                Width = new GridLength(90),
                Binding = new Binding { Path = new PropertyPath(PerfRow.Names[i]) },
            });
        }

        grid.Columns.AddRange(columns);

        tickingCheckBox.Checked += (_, _) => StartTicking();
        tickingCheckBox.Unchecked += (_, _) => StopTicking();
        virtualizationToggle.Toggled += (_, _) => grid.IsColumnVirtualizationEnabled = virtualizationToggle.IsOn;
        prefetchToggle.Toggled += (_, _) => grid.ColumnPrefetchLength = prefetchToggle.IsOn ? 1 : 0;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (_rows is not null)
        {
            return;
        }

        App.Current.MainWindow.SetLoading(true);

        var stopwatch = Stopwatch.StartNew();

        // A million small objects: each holds its index and, only once the feed has touched it, its values.
        _rows = await Task.Run(() =>
        {
            var rows = new List<PerfRow>(RowCount);

            for (var i = 0; i < RowCount; i++)
            {
                rows.Add(new PerfRow(i));
            }

            return rows;
        });

        grid.ItemsSource = _rows;
        _loadMs = stopwatch.Elapsed.TotalMilliseconds;

        App.Current.MainWindow.SetLoading(false);

        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(1);
        _statusTimer.IsRepeating = true;
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();

        UpdateStatus();

        if (tickingCheckBox.IsChecked is true)
        {
            StartTicking();
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        StopTicking();
        _statusTimer?.Stop();

        grid.ItemsSource = null;
        _rows = null; // let a million rows go
    }

    private void StartTicking()
    {
        if (_rows is null)
        {
            return; // OnPageLoaded starts it once the rows exist
        }

        if (_ticker is null)
        {
            _ticker = DispatcherQueue.CreateTimer();
            _ticker.Interval = TimeSpan.FromMilliseconds(TickIntervalMs);
            _ticker.IsRepeating = true;
            _ticker.Tick += (_, _) => ApplyTicks();
        }

        _ticker.Start();
    }

    private void StopTicking() => _ticker?.Stop();

    /// <summary>
    /// One batch of the feed: a share of the requested rate, spread over random cells of the rows on screen.
    /// </summary>
    /// <remarks>
    /// Only visible rows, the way a real blotter's channel gates updates. Rows are uniform, so the visible index
    /// range falls straight out of the vertical offset and the row height — no visual-tree query needed.
    /// </remarks>
    private void ApplyTicks()
    {
        if (_rows is null || grid.RowHeight <= 0 || double.IsNaN(rateBox.Value))
        {
            return;
        }

        var first = Math.Clamp((int)(grid.VerticalOffset / grid.RowHeight), 0, _rows.Count - 1);
        var last = Math.Min(_rows.Count - 1, first + (int)(grid.ActualHeight / grid.RowHeight) + 2);
        var perBatch = (int)Math.Round(rateBox.Value * TickIntervalMs / 1000.0);

        for (var n = 0; n < perBatch; n++)
        {
            var row = _rows[_random.Next(first, last + 1)];
            row.Tick(_random.Next(PerfRow.ValueCount), _random.NextDouble() * 2 - 1);
        }

        _appliedThisSecond += perBatch;
    }

    private void UpdateStatus()
    {
        statusText.Text = $"{RowCount:N0} rows × {grid.Columns.Count} columns, loaded in {_loadMs:N0} ms. " +
                          $"Feed: {_appliedThisSecond:N0} updates applied in the last second.";
        _appliedThisSecond = 0;
    }
}

/// <summary>
/// One row of the feed. Holds its index and nothing else until the feed touches it — a million of these must
/// stay small — and computes its values from the index until then. Mutation is in place, through
/// <see cref="INotifyPropertyChanged"/>, exactly as the consuming app's items behave.
/// </summary>
public sealed class PerfRow : INotifyPropertyChanged
{
    public const int ValueCount = 69;

    public static readonly string[] Names = [.. Enumerable.Range(0, ValueCount).Select(i => $"C{i:D2}")];

    // One args instance per property: the feed raises thousands of these a second and must not allocate for it.
    private static readonly PropertyChangedEventArgs[] Args = [.. Names.Select(name => new PropertyChangedEventArgs(name))];

    private double[]? _values; // allocated on the first tick; rows that are never on screen never pay for it

    public PerfRow(int index)
    {
        Index = index;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index { get; }

    public void Tick(int column, double delta)
    {
        if (_values is null)
        {
            _values = new double[ValueCount];
            Array.Fill(_values, double.NaN);
        }

        _values[column] = Math.Round(Get(column) + delta, 2);
        PropertyChanged?.Invoke(this, Args[column]);
    }

    private double Get(int column)
        => _values is { } values && !double.IsNaN(values[column]) ? values[column] : Seed(column);

    // Deterministic and free: the same row shows the same numbers every run, without a byte stored per row.
    private double Seed(int column) => 100 + ((Index * 31 + column * 17) % 1000) / 10.0;

    public double C00 => Get(0);
    public double C01 => Get(1);
    public double C02 => Get(2);
    public double C03 => Get(3);
    public double C04 => Get(4);
    public double C05 => Get(5);
    public double C06 => Get(6);
    public double C07 => Get(7);
    public double C08 => Get(8);
    public double C09 => Get(9);
    public double C10 => Get(10);
    public double C11 => Get(11);
    public double C12 => Get(12);
    public double C13 => Get(13);
    public double C14 => Get(14);
    public double C15 => Get(15);
    public double C16 => Get(16);
    public double C17 => Get(17);
    public double C18 => Get(18);
    public double C19 => Get(19);
    public double C20 => Get(20);
    public double C21 => Get(21);
    public double C22 => Get(22);
    public double C23 => Get(23);
    public double C24 => Get(24);
    public double C25 => Get(25);
    public double C26 => Get(26);
    public double C27 => Get(27);
    public double C28 => Get(28);
    public double C29 => Get(29);
    public double C30 => Get(30);
    public double C31 => Get(31);
    public double C32 => Get(32);
    public double C33 => Get(33);
    public double C34 => Get(34);
    public double C35 => Get(35);
    public double C36 => Get(36);
    public double C37 => Get(37);
    public double C38 => Get(38);
    public double C39 => Get(39);
    public double C40 => Get(40);
    public double C41 => Get(41);
    public double C42 => Get(42);
    public double C43 => Get(43);
    public double C44 => Get(44);
    public double C45 => Get(45);
    public double C46 => Get(46);
    public double C47 => Get(47);
    public double C48 => Get(48);
    public double C49 => Get(49);
    public double C50 => Get(50);
    public double C51 => Get(51);
    public double C52 => Get(52);
    public double C53 => Get(53);
    public double C54 => Get(54);
    public double C55 => Get(55);
    public double C56 => Get(56);
    public double C57 => Get(57);
    public double C58 => Get(58);
    public double C59 => Get(59);
    public double C60 => Get(60);
    public double C61 => Get(61);
    public double C62 => Get(62);
    public double C63 => Get(63);
    public double C64 => Get(64);
    public double C65 => Get(65);
    public double C66 => Get(66);
    public double C67 => Get(67);
    public double C68 => Get(68);
}
