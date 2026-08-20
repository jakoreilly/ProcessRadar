namespace ProcessRadar.Diagnostics;

/// <summary>
/// Per-frame timing for the graph canvas. Separates the three things that look identical to the
/// user when the app "freezes": a slow layout pass, a slow draw pass, and a frame that never got
/// scheduled at all because something else owned the UI thread (the gap measurement below).
/// Only ever touched from the UI thread, so no locking.
/// </summary>
public sealed class FrameStats
{
    /// <summary>A single frame taking longer than this is worth a line in the log on its own.</summary>
    private static readonly TimeSpan SlowFrameThreshold = TimeSpan.FromMilliseconds(50);

    /// <summary>Render timer runs at 100ms; a gap past this means the UI thread was busy elsewhere,
    /// i.e. what the user perceives as the freeze, and the frame cost below will *not* explain it.</summary>
    private static readonly TimeSpan StallThreshold = TimeSpan.FromMilliseconds(400);

    private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(5);

    private DateTime _lastFrameAt = DateTime.MinValue;
    private DateTime _lastSummaryAt = DateTime.MinValue;

    private int _frames;
    private double _layoutMsTotal;
    private double _drawMsTotal;
    private double _frameMsMax;
    private double _worstGapMs;
    private int _slowFrames;
    private int _stalls;

    /// <summary>Most recent frame's numbers, for the on-canvas HUD.</summary>
    public double LastLayoutMs { get; private set; }
    public double LastDrawMs { get; private set; }
    public double LastGapMs { get; private set; }
    public double AverageFps { get; private set; }
    public int LastNodeCount { get; private set; }

    public void Record(DateTime now, double layoutMs, double drawMs, int nodeCount, int edgeCount, int injectionEdgeCount)
    {
        var gapMs = _lastFrameAt == DateTime.MinValue ? 0 : (now - _lastFrameAt).TotalMilliseconds;
        _lastFrameAt = now;

        LastLayoutMs = layoutMs;
        LastDrawMs = drawMs;
        LastGapMs = gapMs;
        LastNodeCount = nodeCount;

        _frames++;
        _layoutMsTotal += layoutMs;
        _drawMsTotal += drawMs;
        _frameMsMax = Math.Max(_frameMsMax, layoutMs + drawMs);
        _worstGapMs = Math.Max(_worstGapMs, gapMs);

        if (layoutMs + drawMs > SlowFrameThreshold.TotalMilliseconds)
        {
            _slowFrames++;
            DebugLog.Throttled("slow-frame", TimeSpan.FromSeconds(1), "Render",
                () => $"SLOW FRAME {layoutMs + drawMs:F1}ms (layout {layoutMs:F1}ms + draw {drawMs:F1}ms) " +
                      $"for {nodeCount} nodes / {edgeCount} edges / {injectionEdgeCount} injection edges");
        }

        if (gapMs > StallThreshold.TotalMilliseconds)
        {
            _stalls++;
            DebugLog.Warn("Render",
                $"FRAME GAP {gapMs:F0}ms since previous paint (render timer is 100ms) - " +
                $"the UI thread was blocked by something other than this frame; last frame cost only {layoutMs + drawMs:F1}ms");
        }

        if (_lastSummaryAt == DateTime.MinValue)
            _lastSummaryAt = now;

        var sinceSummary = now - _lastSummaryAt;
        if (sinceSummary >= SummaryInterval)
        {
            AverageFps = _frames / sinceSummary.TotalSeconds;
            DebugLog.Info("Render",
                $"{_frames} frames in {sinceSummary.TotalSeconds:F1}s ({AverageFps:F1} fps) | " +
                $"layout avg {_layoutMsTotal / _frames:F1}ms, draw avg {_drawMsTotal / _frames:F1}ms, " +
                $"worst frame {_frameMsMax:F1}ms, worst gap {_worstGapMs:F0}ms | " +
                $"{_slowFrames} slow frames, {_stalls} stalls | {nodeCount} nodes, {edgeCount} edges");

            _lastSummaryAt = now;
            _frames = 0;
            _layoutMsTotal = 0;
            _drawMsTotal = 0;
            _frameMsMax = 0;
            _worstGapMs = 0;
            _slowFrames = 0;
            _stalls = 0;
        }
    }
}

/// <summary>
/// Counts events over a rolling window so an ETW storm shows up in the log as a rate rather than
/// as thousands of individual lines. Called from the ETW capture thread, so it locks.
/// </summary>
public sealed class RateCounter(string name, int warnPerSecond)
{
    private readonly object _gate = new();
    private DateTime _windowStart = DateTime.UtcNow;
    private int _count;
    private long _total;

    public long Total { get { lock (_gate) return _total; } }

    public void Tick()
    {
        int count;
        double seconds;
        long total;

        lock (_gate)
        {
            _count++;
            _total++;
            var elapsed = DateTime.UtcNow - _windowStart;
            if (elapsed < TimeSpan.FromSeconds(2))
                return;

            count = _count;
            total = _total;
            seconds = elapsed.TotalSeconds;
            _count = 0;
            _windowStart = DateTime.UtcNow;
        }

        var perSecond = count / seconds;
        if (perSecond >= warnPerSecond)
            DebugLog.Warn("Capture", $"{name}: {perSecond:F0}/s over the last {seconds:F1}s ({total} total) - " +
                                     $"each one marshals to the UI thread, so this rate is a likely freeze source");
        else
            DebugLog.Info("Capture", $"{name}: {perSecond:F1}/s over the last {seconds:F1}s ({total} total)");
    }
}
