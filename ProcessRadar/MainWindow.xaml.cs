using System.Windows;
using System.Windows.Threading;
using ProcessRadar.Analysis;
using ProcessRadar.Capture;
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
/// </summary>
public partial class MainWindow : Window
{
    private const int MaxAnomalyLogEntries = 200;

    private readonly ProcessEnumerationService _service = new();
    private readonly ProcessGraph _graph = new();
    private readonly RadialTreeLayout _layout = new();
    private readonly AnomalyDetector _detector = new();
    private readonly DispatcherTimer _housekeepingTimer;
    private readonly DispatcherTimer _renderTimer;
    private int _count;
    private int _injectionCount;
    private bool _frozen;

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

    public MainWindow()
    {
        InitializeComponent();
        _service.ProcessStarted += OnProcessStarted;
        _service.ProcessStopped += OnProcessStopped;
        _service.InjectionDetected += OnInjectionDetected;
        Closed += (_, _) => _service.Dispose();

        _housekeepingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _housekeepingTimer.Tick += (_, _) =>
        {
            _graph.Purge(DateTime.Now);
            GraphText.Text = $"{_graph.Nodes.Count} nodes";
        };
        _housekeepingTimer.Start();
        Closed += (_, _) => _housekeepingTimer.Stop();

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _renderTimer.Tick += (_, _) => GraphCanvas.InvalidateVisual();
        _renderTimer.Start();
        Closed += (_, _) => _renderTimer.Stop();
    }

    private void SnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        ProcessList.Items.Clear();
        var snapshot = ProcessEnumerationService.Snapshot();
        _graph.LoadSnapshot(snapshot);
        _count = snapshot.Count;
        CountText.Text = $"{_count} processes";

        foreach (var p in snapshot.OrderBy(p => p.Pid))
            ProcessList.Items.Add(Format(p));
    }

    private void LiveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _service.StartLiveTrace();
            LiveButton.IsEnabled = false;
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "Process Radar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void GraphCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _layout.Width = Math.Max(e.NewSize.Width, 1);
        _layout.Height = Math.Max(e.NewSize.Height, 1);
    }

    private void GraphCanvas_PaintSurface(object? sender, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
    {
        IReadOnlyList<RadialPosition> positions;
        IReadOnlyDictionary<int, ProcessNode> nodesByPid;
        IReadOnlyList<InjectionEdge> injectionEdges;

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
        }

        ProcessGraphRenderer.Draw(e.Surface.Canvas, positions, nodesByPid, injectionEdges, DateTime.Now);

        _lastPositions = positions;
        _lastNodesByPid = nodesByPid;
        _lastInjectionEdges = injectionEdges;
        if (_selectedPid is not null)
            UpdateInspectorPanel();
    }

    private void OnInjectionDetected(object? sender, InjectionSignal signal)
    {
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
        var anomalies = _detector.EvaluateNewProcess(p, _graph);
        _graph.Upsert(p);
        foreach (var anomaly in anomalies)
            _graph.MarkAnomaly(anomaly.Pid, anomaly.Timestamp);

        Dispatcher.Invoke(() =>
        {
            _count++;
            CountText.Text = $"{_count} processes";
            ProcessList.Items.Insert(0, $"+ {Format(p)}");
            foreach (var anomaly in anomalies)
                LogAnomaly(anomaly);
        });

        Task.Run(() =>
        {
            _graph.EnrichModules(p.Pid);
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
        });
    }

    private void LogAnomaly(AnomalyEvent anomaly)
    {
        AnomalyList.Items.Insert(0, $"{anomaly.Timestamp:HH:mm:ss}  [{anomaly.Kind}]  {anomaly.Description}");
        while (AnomalyList.Items.Count > MaxAnomalyLogEntries)
            AnomalyList.Items.RemoveAt(AnomalyList.Items.Count - 1);
    }

    private void OnProcessStopped(object? sender, int pid)
    {
        _graph.MarkStopped(pid);
        Dispatcher.Invoke(() => ProcessList.Items.Insert(0, $"- pid {pid} exited"));
    }

    private static string Format(ProcessInfo p) =>
        $"{p.Pid,-6} parent={p.ParentPid,-6} {p.Name}";

    // --- Phase 6: click-to-inspect -----------------------------------------------------------

    private const double ClickHitRadius = 14.0;

    private void GraphCanvas_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var click = e.GetPosition(GraphCanvas);
        int? hitPid = null;
        var bestDist = double.MaxValue;

        foreach (var p in _lastPositions)
        {
            var dx = click.X - p.X;
            var dy = click.Y - p.Y;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist <= ClickHitRadius && dist < bestDist)
            {
                bestDist = dist;
                hitPid = p.Pid;
            }
        }

        if (hitPid is not int pid)
        {
            _selectedPid = null;
            InspectorPanel.Visibility = Visibility.Collapsed;
            return;
        }

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

    private void FilterBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        _graph.NameFilter = FilterBox.Text;

    // --- Phase 6: freeze frame -----------------------------------------------------------------

    /// <summary>Stops the render/housekeeping timers only - the ETW capture and graph keep
    /// running underneath, so a short-lived process that would normally flash-and-fade before
    /// you can read it just sits on screen (housekeeping is what purges/fades dead nodes, so
    /// freezing it is what actually freezes the picture). Unfreezing jumps straight to whatever
    /// state accumulated while frozen, rather than replaying it.</summary>
    private void FreezeButton_Click(object sender, RoutedEventArgs e)
    {
        _frozen = !_frozen;
        FreezeButton.Content = _frozen ? "Resume" : "Freeze frame";

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
