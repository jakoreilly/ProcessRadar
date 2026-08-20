using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using ProcessRadar.Analysis;
using ProcessRadar.Capture;
using ProcessRadar.Diagnostics;
using ProcessRadar.Graph;
using ProcessRadar.Models;
using ProcessRadar.Render;

namespace ProcessRadar;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// Phase 1-4: on-demand snapshot + optional live ETW stream feed a parent/child process graph,
/// radial layout, an SkiaSharp render (spawn pop / exit fade), and an anomaly heuristics layer
/// (unsigned-interpreter-from-trusted-app / non-standard module path / orphaned process) that
/// flashes flagged nodes and logs to the Anomalies panel.
/// Phase 5: injection highlighting - a distinct red animated-dash edge (see
/// <see cref="ProcessGraphRenderer"/>) between processes involved in a cross-process VM
/// allocation (see <see cref="ProcessEnumerationService"/>'s doc comment for what signal this
/// uses and why), ahead of Phase 6 polish.
/// Phase 6: click-to-inspect, name filter, freeze frame, zoom/pan (see <see cref="ViewTransform"/>),
/// and the diagnostics under <see cref="ProcessRadar.Diagnostics"/> - the graph is laid out from
/// scratch ten times a second on the dispatcher while ETW events land on a background thread, so
/// "it froze" and "it looked wrong" both need numbers rather than guesses.
/// </summary>
public partial class MainWindow : Window
{
    private const int MaxAnomalyLogEntries = 200;

    /// <summary>Full-session anomaly history kept for Export Logs, separate from the 200-entry UI
    /// list - that cap exists to keep the ListBox fast, not to decide what's worth exporting.</summary>
    private const int MaxAnomalyHistoryEntries = 5000;

    /// <summary>The process list gets an item per ETW event and a WPF ListBox degrades badly past
    /// a few thousand of them, so it is capped the way the anomaly log already was - an unbounded
    /// list here is one of the ways this window ends up wedged during a long live trace.</summary>
    private const int MaxProcessLogEntries = 1000;

    /// <summary>Mouse travel (DIPs) after button-down that turns a click into a pan. Below it the
    /// gesture is still a select, so panning doesn't cost you click-to-inspect.</summary>
    private const double DragThreshold = 4.0;

    /// <summary>Click hit radius in screen DIPs; converted to world units at the current zoom so
    /// the target stays the same size under the cursor however far in you are.</summary>
    private const double ClickHitRadius = 14.0;

    private readonly ProcessEnumerationService _service = new();
    private readonly ProcessGraph _graph = new();
    private readonly RadialTreeLayout _layout = new();
    private readonly AnomalyDetector _detector = new();
    private readonly DispatcherTimer _housekeepingTimer;
    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _watchdogTimer;
    private int _count;
    private int _injectionCount;
    private bool _frozen;

    // --- Phase 6: view + diagnostics ----------------------------------------------------------

    private readonly ViewTransform _view = new();
    private readonly FrameStats _frameStats = new();
    private readonly LayoutDiagnostics _layoutDiagnostics = new();
    private readonly RateCounter _startRate = new("process starts", warnPerSecond: 40);
    private readonly RateCounter _stopRate = new("process stops", warnPerSecond: 40);
    private readonly RateCounter _injectionRate = new("injection signals", warnPerSecond: 10);
    private readonly Stopwatch _watchdog = Stopwatch.StartNew();
    private TimeSpan _lastWatchdogTick;
    private double _deviceScale = 1.0;
    private int _enrichmentsInFlight;
    private ProcessGraphRenderer.DrawStats _lastDrawStats;
    private Point? _dragOrigin;
    private Point _dragLast;
    private bool _dragging;

    // Click-to-inspect state, refreshed from the most recently painted frame. Also what
    // "freeze frame" actually freezes (see GraphCanvas_PaintSurface) - the render/housekeeping
    // timers being stopped isn't enough on its own, since any *other* trigger for a repaint
    // (window resize, DPI change, an unrelated InvalidateVisual) would otherwise still pull
    // fresh data from the live graph and defeat the freeze.
    private IReadOnlyList<RadialPosition> _lastPositions = Array.Empty<RadialPosition>();
    private IReadOnlyDictionary<int, ProcessNode> _lastNodesByPid = new Dictionary<int, ProcessNode>();
    private IReadOnlyList<InjectionEdge> _lastInjectionEdges = Array.Empty<InjectionEdge>();
    private int? _selectedPid;
    private ProcessInspectionDetail? _selectedDetail;

    // --- Export logs ---------------------------------------------------------------------------

    /// <summary>Oldest-first; only ever touched from the UI thread, same as <see cref="LogAnomaly"/>
    /// which populates it.</summary>
    private readonly List<AnomalyEvent> _anomalyHistory = new();

    /// <summary>Set once the window has closed. Both the export callback and the trace-fault
    /// callback arrive on background threads and can land after teardown, where touching a control
    /// or showing a dialog throws rather than doing anything useful.</summary>
    private bool _closed;

    public MainWindow()
    {
        InitializeComponent();

        DebugLog.Info("Startup", $"Process Radar starting - {Environment.OSVersion}, " +
                                 $"{Environment.ProcessorCount} cores, .NET {Environment.Version}");
        ShowLogPath();

        _service.ProcessStarted += OnProcessStarted;
        _service.ProcessStopped += OnProcessStopped;
        _service.InjectionDetected += OnInjectionDetected;
        _service.TraceFaulted += OnTraceFaulted;
        Closed += (_, _) =>
        {
            _closed = true;
            _service.Dispose();
        };

        _housekeepingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _housekeepingTimer.Tick += (_, _) =>
        {
            var sw = Stopwatch.StartNew();
            var (purgedNodes, purgedEdges) = _graph.Purge(DateTime.Now);
            var nodeCount = _graph.Nodes.Count;
            var elapsedMs = sw.Elapsed.TotalMilliseconds;
            GraphText.Text = $"{nodeCount} nodes";

            if (purgedNodes > 0 || purgedEdges > 0)
                DebugLog.Throttled("purge", TimeSpan.FromSeconds(5), "Graph",
                    () => $"purged {purgedNodes} dead nodes / {purgedEdges} stale injection edges in " +
                          $"{elapsedMs:F1}ms, {nodeCount} nodes remain");
            else if (elapsedMs > 20)
                DebugLog.Warn("Graph", $"housekeeping pass took {elapsedMs:F1}ms for {nodeCount} nodes");
        };
        _housekeepingTimer.Start();
        Closed += (_, _) => _housekeepingTimer.Stop();

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _renderTimer.Tick += (_, _) => GraphCanvas.InvalidateVisual();
        _renderTimer.Start();
        Closed += (_, _) => _renderTimer.Stop();

        // Independent of the render timer, and deliberately still running while frozen: a
        // dispatcher timer that fires late can only mean the UI thread was busy, which is the one
        // measurement that separates "that frame was slow" from "the app was wedged".
        _lastWatchdogTick = _watchdog.Elapsed;
        _watchdogTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(250) };
        _watchdogTimer.Tick += (_, _) =>
        {
            var now = _watchdog.Elapsed;
            var lateBy = (now - _lastWatchdogTick).TotalMilliseconds - _watchdogTimer.Interval.TotalMilliseconds;
            _lastWatchdogTick = now;
            if (lateBy > 250)
                DebugLog.Warn("UiThread", $"dispatcher ran {lateBy:F0}ms late - UI thread blocked " +
                                          $"({_enrichmentsInFlight} module enrichments in flight, " +
                                          $"{ProcessList.Items.Count} process rows, {AnomalyList.Items.Count} anomaly rows)");
        };
        _watchdogTimer.Start();

        Closed += (_, _) =>
        {
            _watchdogTimer.Stop();
            DebugLog.Info("Shutdown", $"closing - {_startRate.Total} starts, {_stopRate.Total} stops, " +
                                      $"{_injectionRate.Total} injection signals observed");
            DebugLog.Shutdown();
        };
    }

    private async void SnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        // The WMI query behind Snapshot() routinely takes hundreds of ms to a few seconds, and this
        // is the one path the README promises works instantly without admin - run it off the UI
        // thread so it doesn't freeze the whole window (zoom/pan/filter included) while it waits.
        SnapshotButton.IsEnabled = false;
        try
        {
            var sw = Stopwatch.StartNew();
            var snapshot = await Task.Run(ProcessEnumerationService.Snapshot);
            var enumerated = sw.Elapsed;

            ProcessList.Items.Clear();
            _graph.LoadSnapshot(snapshot);
            _count = snapshot.Count;
            CountText.Text = $"{_count} processes";

            foreach (var p in snapshot.OrderBy(p => p.Pid))
                ProcessList.Items.Add(Format(p));

            DebugLog.Info("Snapshot", $"{snapshot.Count} processes enumerated in {enumerated.TotalMilliseconds:F0}ms; " +
                                      $"graph and list populated in {sw.Elapsed.TotalMilliseconds:F0}ms total");
        }
        catch (Exception ex)
        {
            // Moving the query onto a task made this reachable as an async void escape, which would
            // take the whole app down. WMI can refuse for reasons outside our control (the service
            // being restarted, a repository hiccup), and a dead Snapshot button is not worth a crash.
            DebugLog.Warn("Snapshot", $"process enumeration failed: {ex.Message}");
            MessageBox.Show(this, $"Could not enumerate processes:\n{ex.Message}",
                "Process Radar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SnapshotButton.IsEnabled = true;
        }
    }

    private void LiveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _service.StartLiveTrace();
            LiveButton.IsEnabled = false;
            DebugLog.Info("Capture", "live ETW trace started");
        }
        catch (InvalidOperationException ex)
        {
            DebugLog.Warn("Capture", $"live trace refused: {ex.Message}");
            MessageBox.Show(ex.Message, "Process Radar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Arrives on a thread pool thread. The overwhelmingly likely cause is the window
    /// being closed out from under a live trace, so a fault after teardown is expected and silent -
    /// only a fault while the window is still up is worth interrupting anyone over.</summary>
    private void OnTraceFaulted(object? sender, Exception ex)
    {
        Dispatcher.BeginInvoke(() =>
        {
            DebugLog.Warn("Capture", $"live trace stopped unexpectedly: {ex.Message}");
            if (_closed)
                return;

            // The processing loop is gone but the session object is not; without this, restarting
            // overwrites the field and leaks the old session's native handles.
            _service.StopLiveTrace();
            LiveButton.IsEnabled = true;
            MessageBox.Show(this, $"Live trace stopped unexpectedly and has been turned off:\n{ex.Message}",
                "Process Radar", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    // --- Export logs ---------------------------------------------------------------------------

    /// <summary>Writes the full process table (ignoring the name filter - a narrowed view on screen
    /// shouldn't silently narrow what gets written to disk) and the anomaly history to one CSV file.
    /// The write itself runs off the UI thread since it can cover thousands of rows on a long-running
    /// trace - the same reasoning as moving <see cref="SnapshotButton_Click"/>'s WMI call off it.</summary>
    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export Process Radar logs",
            FileName = $"processradar-export-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var path = dialog.FileName;
        var processes = _graph.AllNodes;
        var anomalies = _anomalyHistory.ToList();

        Task.Run(() =>
        {
            try
            {
                LogExporter.Export(path, processes, anomalies);
                DebugLog.Info("Export", $"exported {processes.Count} processes and {anomalies.Count} anomalies to {path}");
                Report(MessageBoxImage.Information,
                    $"Exported {processes.Count} processes and {anomalies.Count} anomalies to:\n{path}");
            }
            catch (Exception ex)
            {
                // Deliberately broad: this is the top of a background task, so anything not caught
                // here is an unobserved exception and the user is left staring at a button that
                // appeared to do nothing. A path the save dialog accepted can still fail on write
                // for reasons ranging from IO to an encoding refusal.
                DebugLog.Warn("Export", $"export to {path} failed: {ex.Message}");
                Report(MessageBoxImage.Error, $"Export failed: {ex.Message}");
            }
        });

        void Report(MessageBoxImage icon, string message) => Dispatcher.BeginInvoke(() =>
        {
            if (!_closed)
                MessageBox.Show(this, message, "Process Radar", MessageBoxButton.OK, icon);
        });
    }

    private void GraphCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _layout.Width = Math.Max(e.NewSize.Width, 1);
        _layout.Height = Math.Max(e.NewSize.Height, 1);
        DebugLog.Info("Layout", $"canvas resized to {e.NewSize.Width:F0}x{e.NewSize.Height:F0} DIP");
    }

    private void GraphCanvas_PaintSurface(object? sender, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
    {
        var frameStart = DateTime.Now;
        IReadOnlyList<RadialPosition> positions;
        IReadOnlyDictionary<int, ProcessNode> nodesByPid;
        IReadOnlyList<InjectionEdge> injectionEdges;

        // SKElement hands over a surface sized in device pixels while the layout and the mouse
        // both work in DIPs. Recomputed every frame rather than cached, because dragging the
        // window to a differently-scaled monitor changes it without raising SizeChanged.
        var widthDip = Math.Max(GraphCanvas.ActualWidth, 1);
        var heightDip = Math.Max(GraphCanvas.ActualHeight, 1);
        var deviceScale = e.Info.Width / widthDip;
        if (Math.Abs(deviceScale - _deviceScale) > 0.001)
        {
            DebugLog.Info("Render", $"device scale {deviceScale:F3} " +
                                    $"(surface {e.Info.Width}x{e.Info.Height}px vs element {widthDip:F0}x{heightDip:F0}dip) - " +
                                    "the layout is drawn through this, so a mismatch here is visible as distortion");
            _deviceScale = deviceScale;
        }

        var layoutSw = Stopwatch.StartNew();
        if (_frozen)
        {
            // Reuse exactly what was last painted rather than recomputing from the (still-live)
            // graph - stopping the timers only stops the *scheduled* repaints; this is what
            // keeps an unrelated repaint (e.g. a window resize) from showing live data anyway.
            positions = _lastPositions;
            nodesByPid = _lastNodesByPid;
            injectionEdges = _lastInjectionEdges;
        }
        else
        {
            var nodes = _graph.Nodes;
            nodesByPid = nodes.ToDictionary(n => n.Pid);
            positions = _layout.Compute(nodes);
            injectionEdges = _graph.InjectionEdges;
            _layoutDiagnostics.Inspect(positions, nodes.Count, widthDip, heightDip);
        }
        layoutSw.Stop();

        var hud = DebugToggle.IsChecked == true
            ? BuildHudLines(positions.Count, nodesByPid.Count, injectionEdges.Count)
            : null;

        var drawSw = Stopwatch.StartNew();
        _lastDrawStats = ProcessGraphRenderer.Draw(
            e.Surface.Canvas, positions, nodesByPid, injectionEdges, frameStart,
            _view, deviceScale, widthDip, heightDip, hud);
        drawSw.Stop();

        _frameStats.Record(frameStart, layoutSw.Elapsed.TotalMilliseconds, drawSw.Elapsed.TotalMilliseconds,
            nodesByPid.Count, _lastDrawStats.EdgesDrawn, injectionEdges.Count);

        _lastPositions = positions;
        _lastNodesByPid = nodesByPid;
        _lastInjectionEdges = injectionEdges;
        if (_selectedPid is not null)
            UpdateInspectorPanel();
    }

    /// <summary>The on-canvas debug overlay. Built from the *previous* frame's draw stats, since
    /// it has to exist before the draw it describes - a one-frame lag is invisible at 10fps and
    /// beats drawing the overlay twice.</summary>
    private IReadOnlyList<string> BuildHudLines(int placed, int nodeCount, int injectionEdgeCount) =>
    [
        $"frame  {_frameStats.LastLayoutMs + _frameStats.LastDrawMs,6:F1}ms   layout {_frameStats.LastLayoutMs,5:F1}   draw {_frameStats.LastDrawMs,5:F1}   gap {_frameStats.LastGapMs,5:F0}ms   {_frameStats.AverageFps,4:F1} fps",
        $"nodes  {nodeCount,5} tracked   {placed,5} placed   {_lastDrawStats.NodesDrawn,5} drawn   {_lastDrawStats.NodesCulled,5} culled   {_lastDrawStats.LabelsDrawn,5} labels",
        $"layout {_layout.LastRootCount,5} roots   depth {_layout.LastMaxDepth,3}   jumps {_layoutDiagnostics.LastJumpCount,4}   overlap {_layoutDiagnostics.LastOverlapCount,4}   offscreen {_layoutDiagnostics.LastOffscreenCount,4}",
        $"view   zoom {_view.Zoom * 100,5:F0}%   pan {_view.PanX,6:F0},{_view.PanY,-6:F0}   dpi x{_deviceScale:F2}   {(_frozen ? "FROZEN" : "live")}",
        $"feed   {_startRate.Total,6} starts  {_stopRate.Total,6} stops  {_injectionRate.Total,4} inject ({injectionEdgeCount} edges)  {_enrichmentsInFlight,3} enrich in flight",
    ];

    private void OnInjectionDetected(object? sender, InjectionSignal signal)
    {
        _injectionRate.Tick();
        _graph.MarkInjection(signal.SourcePid, signal.TargetPid, signal.Timestamp);

        var anomaly = new AnomalyEvent(AnomalyKind.CrossProcessMemoryWrite, signal.TargetPid,
            $"possible injection: {signal.SourceName} (pid {signal.SourcePid}) allocated memory in {signal.TargetName} (pid {signal.TargetPid})",
            signal.Timestamp);

        Dispatcher.Invoke(() =>
        {
            _injectionCount++;
            InjectionText.Text = $"{_injectionCount} injection signals";
            LogAnomaly(anomaly);
        });
    }

    private void OnProcessStarted(object? sender, ProcessInfo p)
    {
        _startRate.Tick();
        var anomalies = _detector.EvaluateNewProcess(p, _graph);
        _graph.Upsert(p);
        foreach (var anomaly in anomalies)
            _graph.MarkAnomaly(anomaly.Pid, anomaly.Timestamp);

        // Synchronous by design (the ETW callback must not outrun the UI), which also means a
        // burst of process starts blocks the capture thread on the dispatcher queue - time it,
        // because that queue is the prime suspect whenever the window stops responding.
        var marshalSw = Stopwatch.StartNew();
        Dispatcher.Invoke(() =>
        {
            _count++;
            CountText.Text = $"{_count} processes";
            ProcessList.Items.Insert(0, $"+ {Format(p)}");
            TrimProcessList();
            foreach (var anomaly in anomalies)
                LogAnomaly(anomaly);
        });

        var marshalMs = marshalSw.Elapsed.TotalMilliseconds;
        if (marshalMs > 100)
            DebugLog.Throttled("marshal-slow", TimeSpan.FromSeconds(2), "Capture",
                () => $"a process-start waited {marshalMs:F0}ms to reach the UI thread - " +
                      "the dispatcher is the bottleneck here, not the capture");

        var inFlight = Interlocked.Increment(ref _enrichmentsInFlight);
        if (inFlight > 32)
            DebugLog.Throttled("enrich-backlog", TimeSpan.FromSeconds(5), "Enrich",
                () => $"{inFlight} concurrent module enrichments queued - each opens a process handle and " +
                      "walks its module list, so a spawn storm saturates the thread pool here");

        Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                _graph.EnrichModules(p.Pid);
                var enrichMs = sw.Elapsed.TotalMilliseconds;
                if (enrichMs > 500)
                    DebugLog.Throttled("enrich-slow", TimeSpan.FromSeconds(5), "Enrich",
                        () => $"module enrichment for pid {p.Pid} ({p.Name}) took {enrichMs:F0}ms");

                var node = _graph.TryGetNode(p.Pid);
                if (node is null)
                    return;

                var moduleAnomalies = _detector.EvaluateModules(node);
                if (moduleAnomalies.Count == 0)
                    return;

                foreach (var anomaly in moduleAnomalies)
                    _graph.MarkAnomaly(anomaly.Pid, anomaly.Timestamp);

                Dispatcher.Invoke(() =>
                {
                    foreach (var anomaly in moduleAnomalies)
                        LogAnomaly(anomaly);
                });
            }
            finally
            {
                Interlocked.Decrement(ref _enrichmentsInFlight);
            }
        });
    }

    private void LogAnomaly(AnomalyEvent anomaly)
    {
        AnomalyList.Items.Insert(0, $"{anomaly.Timestamp:HH:mm:ss}  [{anomaly.Kind}]  {anomaly.Description}");
        while (AnomalyList.Items.Count > MaxAnomalyLogEntries)
            AnomalyList.Items.RemoveAt(AnomalyList.Items.Count - 1);
        DebugLog.Info("Anomaly", $"[{anomaly.Kind}] pid {anomaly.Pid}: {anomaly.Description}");

        _anomalyHistory.Add(anomaly);
        if (_anomalyHistory.Count > MaxAnomalyHistoryEntries)
            _anomalyHistory.RemoveAt(0);
    }

    private void OnProcessStopped(object? sender, int pid)
    {
        _stopRate.Tick();
        _graph.MarkStopped(pid);
        Dispatcher.Invoke(() =>
        {
            ProcessList.Items.Insert(0, $"- pid {pid} exited");
            TrimProcessList();
        });
    }

    private void TrimProcessList()
    {
        while (ProcessList.Items.Count > MaxProcessLogEntries)
            ProcessList.Items.RemoveAt(ProcessList.Items.Count - 1);
    }

    private static string Format(ProcessInfo p) =>
        $"{p.Pid,-6} parent={p.ParentPid,-6} {p.Name}";

    // --- Phase 6: zoom + pan ---------------------------------------------------------------------

    private void GraphCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var at = e.GetPosition(GraphCanvas);
        var factor = e.Delta > 0 ? ViewTransform.WheelStep : 1 / ViewTransform.WheelStep;
        _view.ZoomAt(at.X, at.Y, factor);
        OnViewChanged();
        e.Handled = true;
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e) =>
        ZoomAtCentre(ViewTransform.WheelStep * ViewTransform.WheelStep);

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e) =>
        ZoomAtCentre(1 / (ViewTransform.WheelStep * ViewTransform.WheelStep));

    private void ZoomAtCentre(double factor)
    {
        _view.ZoomAt(GraphCanvas.ActualWidth / 2, GraphCanvas.ActualHeight / 2, factor);
        OnViewChanged();
    }

    private void ResetViewButton_Click(object sender, RoutedEventArgs e)
    {
        _view.Reset();
        OnViewChanged();
    }

    /// <summary>Repaints explicitly instead of waiting for the render timer, so zoom and pan keep
    /// working while the frame is frozen (the retained frame is redrawn through the new
    /// transform).</summary>
    private void OnViewChanged()
    {
        ZoomText.Text = $"{_view.Zoom * 100:F0}%";
        GraphCanvas.InvalidateVisual();
        DebugLog.Throttled("view", TimeSpan.FromMilliseconds(500), "View",
            () => $"zoom {_view.Zoom:F2}x, pan ({_view.PanX:F0}, {_view.PanY:F0})");
    }

    // --- Phase 6: click-to-inspect ---------------------------------------------------------------

    private void GraphCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _view.Reset();
            OnViewChanged();
            return;
        }

        _dragOrigin = e.GetPosition(GraphCanvas);
        _dragLast = _dragOrigin.Value;
        _dragging = false;
        GraphCanvas.CaptureMouse();
    }

    private void GraphCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragOrigin is not Point origin || e.LeftButton != MouseButtonState.Pressed)
            return;

        var at = e.GetPosition(GraphCanvas);
        if (!_dragging && (Math.Abs(at.X - origin.X) > DragThreshold || Math.Abs(at.Y - origin.Y) > DragThreshold))
            _dragging = true;
        if (!_dragging)
            return;

        _view.PanBy(at.X - _dragLast.X, at.Y - _dragLast.Y);
        _dragLast = at;
        OnViewChanged();
    }

    private void GraphCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        GraphCanvas.ReleaseMouseCapture();
        var wasDragging = _dragging;
        _dragging = false;
        _dragOrigin = null;

        // A pan that happens to finish over a node shouldn't also select it.
        if (!wasDragging)
            SelectAt(e.GetPosition(GraphCanvas));
    }

    private void SelectAt(Point screen)
    {
        var (worldX, worldY) = _view.ToWorld(screen.X, screen.Y);
        var hitRadius = ClickHitRadius / _view.Zoom;

        int? hitPid = null;
        var bestDist = double.MaxValue;

        foreach (var p in _lastPositions)
        {
            var dx = worldX - p.X;
            var dy = worldY - p.Y;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist <= hitRadius && dist < bestDist)
            {
                bestDist = dist;
                hitPid = p.Pid;
            }
        }

        if (hitPid is not int pid)
        {
            DebugLog.Info("Select", $"click at ({screen.X:F0},{screen.Y:F0})dip maps to world ({worldX:F0},{worldY:F0}); " +
                                    $"no node within {hitRadius:F0} world px of {_lastPositions.Count} candidates");
            _selectedPid = null;
            InspectorPanel.Visibility = Visibility.Collapsed;
            return;
        }

        DebugLog.Info("Select", $"selected pid {pid}, {bestDist:F1} world px from the click");
        _selectedPid = pid;
        _selectedDetail = null;
        InspectorPanel.Visibility = Visibility.Visible;
        UpdateInspectorPanel();

        var path = _lastNodesByPid.TryGetValue(pid, out var node) ? node.Path : null;
        Task.Run(() => ProcessInspector.Inspect(pid, path)).ContinueWith(t =>
        {
            if (_selectedPid != pid) return; // selection changed while we were looking this up
            _selectedDetail = t.Result;
            Dispatcher.Invoke(UpdateInspectorPanel);
        });
    }

    private void InspectorCloseButton_Click(object sender, RoutedEventArgs e)
    {
        _selectedPid = null;
        InspectorPanel.Visibility = Visibility.Collapsed;
    }

    private void UpdateInspectorPanel()
    {
        if (_selectedPid is not int pid) return;

        if (!_lastNodesByPid.TryGetValue(pid, out var node))
        {
            InspectorTitle.Text = $"pid {pid}";
            InspectorPath.Text = "(no longer tracked)";
            InspectorTimes.Text = "";
            InspectorModules.Text = "";
            InspectorAnomaly.Text = "";
            return;
        }

        InspectorTitle.Text = $"{node.Name}  (pid {pid})";
        InspectorPath.Text = node.Path ?? "(path unknown)";
        InspectorTimes.Text = node.StartTime is DateTime st
            ? $"Started: {st:HH:mm:ss}   First/last seen: {node.FirstSeen:HH:mm:ss} / {node.LastSeen:HH:mm:ss}"
            : $"First/last seen: {node.FirstSeen:HH:mm:ss} / {node.LastSeen:HH:mm:ss}";
        InspectorModules.Text = $"{node.Modules.Count} loaded modules   {(node.Alive ? "alive" : "exited")}";
        InspectorAnomaly.Text = node.LastAnomalyAt is DateTime at ? $"Anomaly flagged: {at:HH:mm:ss}" : "";

        InspectorHash.Text = _selectedDetail is null
            ? "SHA-256: resolving..."
            : $"SHA-256: {_selectedDetail.Sha256 ?? "n/a"}";
        InspectorSigner.Text = _selectedDetail is null
            ? "Signer: resolving..."
            : $"Signer: {_selectedDetail.Signer ?? "unsigned / unknown"}";
        InspectorCommandLine.Text = _selectedDetail is null
            ? "Command line: resolving..."
            : $"Command line: {_selectedDetail.CommandLine ?? "n/a"}";
    }

    // --- Phase 6: name filter -----------------------------------------------------------------

    private void FilterBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _graph.NameFilter = FilterBox.Text;
        var text = FilterBox.Text;
        DebugLog.Throttled("filter", TimeSpan.FromSeconds(1), "Graph", () => $"name filter set to \"{text}\"");
    }

    // --- Phase 6: debug diagnostics toggle -------------------------------------------------------

    private void DebugToggle_Changed(object sender, RoutedEventArgs e)
    {
        // Kept from when the toggle defaulted to IsChecked="True", which raised Checked while
        // InitializeComponent was still building the tree, before DebugPathText and GraphCanvas
        // existed. The toggle now starts unchecked so no event fires at load, but the guard stays:
        // flipping the default back in XAML should not resurrect a null-reference crash.
        if (!IsInitialized)
            return;

        var enabling = DebugToggle.IsChecked == true;
        if (!enabling)
            DebugLog.Info("Diagnostics", "debug logging disabled");

        DebugLog.Enabled = enabling;
        if (enabling)
            DebugLog.Info("Diagnostics", "debug logging enabled");

        ShowLogPath();
        GraphCanvas.InvalidateVisual();
    }

    private void ShowLogPath()
    {
        if (DebugToggle.IsChecked != true)
        {
            DebugPathText.Text = "";
            return;
        }

        DebugPathText.Text = DebugLog.LogFilePath is { } path ? $"log: {path}" : "log: (file unavailable)";
        DebugPathText.ToolTip = DebugPathText.Text;
    }

    // --- Phase 6: freeze frame -----------------------------------------------------------------

    /// <summary>Stops the render/housekeeping timers only - the ETW capture and graph keep
    /// running underneath, so a short-lived process that would normally flash-and-fade before
    /// you can read it just sits on screen (housekeeping is what purges/fades dead nodes, so
    /// freezing it is what actually freezes the picture). Unfreezing jumps straight to whatever
    /// state accumulated while frozen, rather than replaying it. Zoom and pan keep working while
    /// frozen: they repaint the retained frame through the new transform.</summary>
    private void FreezeButton_Click(object sender, RoutedEventArgs e)
    {
        _frozen = !_frozen;
        FreezeButton.Content = _frozen ? "Resume" : "Freeze frame";
        DebugLog.Info("Render", _frozen
            ? $"frame frozen with {_lastPositions.Count} nodes on screen"
            : "resumed live rendering");

        if (_frozen)
        {
            _renderTimer.Stop();
            _housekeepingTimer.Stop();
        }
        else
        {
            _renderTimer.Start();
            _housekeepingTimer.Start();
        }
    }
}
