using System.ComponentModel;
using System.Diagnostics;
using ProcessRadar.Models;

namespace ProcessRadar.Graph;

/// <summary>
/// Phase 2: parent/child tree model fed by <see cref="Capture.ProcessEnumerationService"/>.
/// A snapshot seeds full state; ETW start/stop events land on the trace session's background
/// thread and keep it live, so all access is locked. Loaded-module enrichment uses the managed
/// <see cref="Process.Modules"/> API rather than NtQuerySystemInformation - it needs no P/Invoke
/// and works for any process the caller has query rights to; protected/exited processes just
/// fail the enrichment call and are skipped, since that's expected, not exceptional.
/// </summary>
public sealed class ProcessGraph
{
    private static readonly TimeSpan DeadNodeRetention = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InjectionEdgeRetention = TimeSpan.FromSeconds(30);
    private const int MaxModulesPerProcess = 50;

    private readonly object _gate = new();
    private readonly Dictionary<int, ProcessNode> _nodes = new();
    private readonly Dictionary<(int Source, int Target), InjectionEdge> _injectionEdges = new();
    private string _nameFilter = "";

    /// <summary>Phase 6: substring name filter (case-insensitive) applied to <see cref="Nodes"/>.
    /// Matching nodes keep their ancestor chain up to the root so the tree the filter shows stays
    /// connected instead of a scatter of orphaned dots - <see cref="TryGetNode"/> and the mutation
    /// methods below deliberately ignore this filter, since anomaly/injection tracking and module
    /// enrichment need to keep working on filtered-out processes too.</summary>
    public string NameFilter
    {
        get { lock (_gate) return _nameFilter; }
        set { lock (_gate) _nameFilter = value ?? ""; }
    }

    public IReadOnlyList<ProcessNode> Nodes
    {
        get { lock (_gate) return FilteredLocked().ToList(); }
    }

    public IReadOnlyList<InjectionEdge> InjectionEdges
    {
        get { lock (_gate) return _injectionEdges.Values.ToList(); }
    }

    /// <summary>Roots = processes whose parent isn't in the map (System, services.exe, orphans).</summary>
    public IReadOnlyList<ProcessNode> Roots
    {
        get { lock (_gate) return _nodes.Values.Where(n => !_nodes.ContainsKey(n.ParentPid) || n.ParentPid == n.Pid).ToList(); }
    }

    public IReadOnlyList<ProcessNode> ChildrenOf(int pid)
    {
        lock (_gate) return _nodes.Values.Where(n => n.ParentPid == pid).ToList();
    }

    public ProcessNode? TryGetNode(int pid)
    {
        lock (_gate)
        {
            _nodes.TryGetValue(pid, out var node);
            return node;
        }
    }

    /// <summary>Marks a node as anomalous so the renderer can flash it; a no-op if the node has
    /// since been purged (the anomaly is still logged separately by the caller either way).</summary>
    public void MarkAnomaly(int pid, DateTime at)
    {
        lock (_gate)
        {
            if (_nodes.TryGetValue(pid, out var node))
                node.LastAnomalyAt = at;
        }
    }

    public void LoadSnapshot(IReadOnlyList<ProcessInfo> snapshot)
    {
        lock (_gate)
        {
            var now = DateTime.Now;
            foreach (var p in snapshot)
                UpsertLocked(p, now);

            var seenPids = snapshot.Select(p => p.Pid).ToHashSet();
            foreach (var node in _nodes.Values.Where(n => !seenPids.Contains(n.Pid)))
                node.Alive = false;
        }
    }

    public void Upsert(ProcessInfo info)
    {
        lock (_gate) UpsertLocked(info, DateTime.Now);
    }

    public void MarkStopped(int pid)
    {
        lock (_gate)
        {
            if (_nodes.TryGetValue(pid, out var node))
            {
                node.Alive = false;
                node.LastSeen = DateTime.Now;
            }
        }
    }

    /// <summary>Marks (or refreshes) a cross-process VM allocation edge and flags both ends as
    /// anomalous so the renderer flashes them, same as <see cref="MarkAnomaly"/> - a no-op past
    /// the point either pid has been purged (the anomaly is still logged separately either way).</summary>
    public void MarkInjection(int sourcePid, int targetPid, DateTime at)
    {
        lock (_gate)
        {
            var key = (sourcePid, targetPid);
            if (!_injectionEdges.TryGetValue(key, out var edge))
            {
                edge = new InjectionEdge { SourcePid = sourcePid, TargetPid = targetPid };
                _injectionEdges[key] = edge;
            }
            edge.LastSeen = at;

            if (_nodes.TryGetValue(sourcePid, out var src))
                src.LastAnomalyAt = at;
            if (_nodes.TryGetValue(targetPid, out var dst))
                dst.LastAnomalyAt = at;
        }
    }

    /// <summary>Drops dead nodes past their retention window (long enough for a flash-and-fade UI),
    /// and injection edges older than <see cref="InjectionEdgeRetention"/> so the buffer doesn't
    /// grow unbounded over a long-running trace.</summary>
    public void Purge(DateTime now)
    {
        lock (_gate)
        {
            var dead = _nodes.Values
                .Where(n => !n.Alive && now - n.LastSeen > DeadNodeRetention)
                .Select(n => n.Pid)
                .ToList();
            foreach (var pid in dead)
                _nodes.Remove(pid);

            var deadEdges = _injectionEdges.Values
                .Where(e => now - e.LastSeen > InjectionEdgeRetention)
                .Select(e => (e.SourcePid, e.TargetPid))
                .ToList();
            foreach (var key in deadEdges)
                _injectionEdges.Remove(key);
        }
    }

    /// <summary>Best-effort loaded-module list for one process; call off the UI thread, it's a syscall per module.</summary>
    public void EnrichModules(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var modules = process.Modules
                .Cast<ProcessModule>()
                .Take(MaxModulesPerProcess)
                .Select(m => new LoadedModule(m.ModuleName, m.FileName))
                .ToList();

            lock (_gate)
            {
                if (_nodes.TryGetValue(pid, out var node))
                    node.Modules = modules;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
        {
            // Process exited between snapshot and enrichment, or access denied
            // (protected/elevated process) - both are routine, not failures.
        }
    }

    private IEnumerable<ProcessNode> FilteredLocked()
    {
        if (string.IsNullOrWhiteSpace(_nameFilter))
            return _nodes.Values;

        var keep = _nodes.Values
            .Where(n => n.Name.Contains(_nameFilter, StringComparison.OrdinalIgnoreCase))
            .ToHashSet();
        if (keep.Count == 0)
            return keep;

        foreach (var match in keep.ToList())
        {
            var current = match;
            while (_nodes.TryGetValue(current.ParentPid, out var parent) && parent.Pid != current.Pid && keep.Add(parent))
                current = parent;
        }

        return _nodes.Values.Where(keep.Contains);
    }

    private void UpsertLocked(ProcessInfo info, DateTime now)
    {
        if (!_nodes.TryGetValue(info.Pid, out var node))
        {
            node = new ProcessNode { Pid = info.Pid, Name = info.Name, FirstSeen = now };
            _nodes[info.Pid] = node;
        }

        node.ParentPid = info.ParentPid;
        node.Name = info.Name;
        node.Path = info.Path;
        node.StartTime = info.StartTime;
        node.LastSeen = now;
        node.Alive = true;
    }
}
