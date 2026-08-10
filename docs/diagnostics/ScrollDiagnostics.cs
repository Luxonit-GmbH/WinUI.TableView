// Drop-in scroll diagnostic. Not part of the library — copy into the consuming app, call Start() before the
// gesture and Stop() after, and read the returned line.
//
// It answers ONE question that a benchmark on a developer machine cannot: on THIS session, when scrolling feels
// laggy, is the UI thread busy, or are frames simply not being produced?
//
//   Start();  ... scroll horizontally for a few seconds ...  var report = Stop();
//
// Read it like this:
//
//   BLOCKED high (say >500ms over a 5s gesture), FRAMES near the display interval
//       -> the UI thread is the bottleneck. Managed work is starving it: the data feed, cell content creation
//          during column realization, or layout. Look at what runs per tick.
//
//   BLOCKED low, FRAME INTERVAL high (say p95 > 40ms against a 60Hz display)
//       -> the thread is fine and the frames are not arriving. The cost is rendering, compositing or remoting.
//          On a session with no GPU this is the expected shape, and no amount of UI-thread optimisation moves it.
//          The lever there is reducing how many pixels change per frame, not how much code runs.
//
//   BOTH low
//       -> whatever you are feeling is not in this gesture. Measure a different one.
//
// Always capture the VERTICAL scroll numbers too, in the same session. "Vertical is fine" is the premise the
// whole investigation rests on; if vertical shows the same shape, the problem is not specific to the axis and
// the diagnosis has to change.

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace Diagnostics;

public static class ScrollDiagnostics
{
    private const double FrameMs = 16.7; // a gap longer than a frame is a dropped frame, not scheduling noise

    private static readonly List<double> _dispatcherGaps = [];
    private static readonly List<double> _frameIntervals = [];

    private static Stopwatch? _stopwatch;
    private static DispatcherQueue? _queue;
    private static double _lastBeat;
    private static double _lastFrame;
    private static bool _running;

    public static void Start()
    {
        if (_running)
        {
            return;
        }

        _dispatcherGaps.Clear();
        _frameIntervals.Clear();

        _queue = DispatcherQueue.GetForCurrentThread();
        _stopwatch = Stopwatch.StartNew();
        _lastBeat = 0;
        _lastFrame = 0;
        _running = true;

        // Lowest priority: it runs only when nothing else wants the thread, so the gap between two runs is how
        // long the thread refused to answer.
        _queue.TryEnqueue(DispatcherQueuePriority.Low, Beat);

        // Frame cadence, independent of the thread: this ticks per composed frame, so a large interval means
        // frames are not being produced regardless of how idle the thread is.
        CompositionTarget.Rendering += OnRendering;
    }

    public static string Stop()
    {
        if (!_running)
        {
            return "ScrollDiagnostics was not running.";
        }

        _running = false;
        CompositionTarget.Rendering -= OnRendering;

        var elapsed = _stopwatch?.Elapsed.TotalMilliseconds ?? 0;
        var blocked = _dispatcherGaps.Where(gap => gap > FrameMs).Sum();

        var frames = _frameIntervals.OrderBy(interval => interval).ToList();
        var frameMedian = Percentile(frames, 0.50);
        var frameP95 = Percentile(frames, 0.95);

        return string.Create(CultureInfo.InvariantCulture,
            $"over {elapsed:F0}ms: BLOCKED {blocked:F0}ms ({blocked / Math.Max(elapsed, 1) * 100:F0}% of the gesture, " +
            $"{_dispatcherGaps.Count} pickups) | FRAMES {frames.Count}, interval median {frameMedian:F1}ms, p95 {frameP95:F1}ms");
    }

    private static void Beat()
    {
        if (!_running || _stopwatch is null || _queue is null)
        {
            return;
        }

        var now = _stopwatch.Elapsed.TotalMilliseconds;
        _dispatcherGaps.Add(now - _lastBeat);
        _lastBeat = now;

        _queue.TryEnqueue(DispatcherQueuePriority.Low, Beat);
    }

    private static void OnRendering(object? sender, object e)
    {
        if (_stopwatch is null)
        {
            return;
        }

        var now = _stopwatch.Elapsed.TotalMilliseconds;

        if (_lastFrame > 0)
        {
            _frameIntervals.Add(now - _lastFrame);
        }

        _lastFrame = now;
    }

    private static double Percentile(List<double> sorted, double fraction)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling(fraction * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }
}
