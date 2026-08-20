using ProcessRadar.Diagnostics;
using ProcessRadar.Models;

namespace ProcessRadar.Graph;

/// <summary>
/// Phase 3: radial tree layout - roots (processes whose parent isn't in the map, e.g. System,
/// services.exe, orphans) sit at the center, each generation gets a wider ring, and siblings
/// split their parent's angular slice evenly. Stateless: recomputed fresh from the current
/// node set each call, since process trees restructure (reparenting, exits) far more than a
/// packet graph's IPs do, so there's no persisted-position smoothing to get right yet.
///
/// Being stateless makes *ordering* load-bearing: the node list arrives in Dictionary
/// enumeration order, which shuffles whenever a pid is added or removed, so unordered siblings
/// would swap angular slices between two frames 100ms apart and the whole diagram would appear
/// to churn. Roots and children are therefore sorted by pid - the one property of a node that
/// never changes - so a given tree lays out identically every frame.
/// </summary>
public sealed class RadialTreeLayout
{
    private const double RingSpacing = 70;

    /// <summary>Radius of the innermost ring when there is more than one root. At radius 0 every
    /// root lands on the exact same pixel, which on a live trace (where orphans are routine, so
    /// "root" often means dozens of processes) draws as a single smeared blob of overlapping
    /// dots and labels rather than a tree.</summary>
    private const double RootRingRadius = 34;

    public double Width { get; set; } = 800;
    public double Height { get; set; } = 500;

    /// <summary>Stats from the most recent <see cref="Compute"/>, for the debug HUD/log.</summary>
    public int LastRootCount { get; private set; }
    public int LastMaxDepth { get; private set; }

    public IReadOnlyList<RadialPosition> Compute(IReadOnlyList<ProcessNode> nodes)
    {
        var positions = new List<RadialPosition>();
        if (nodes.Count == 0)
        {
            LastRootCount = 0;
            LastMaxDepth = 0;
            return positions;
        }

        var byPid = nodes.ToDictionary(n => n.Pid);
        var byParent = nodes.ToLookup(n => n.ParentPid);
        var roots = nodes
            .Where(n => n.ParentPid == n.Pid || !byPid.ContainsKey(n.ParentPid))
            .OrderBy(n => n.Pid)
            .ToList();
        if (roots.Count == 0)
            roots = nodes.OrderBy(n => n.Pid).Take(1).ToList();

        var cx = Width / 2;
        var cy = Height / 2;
        var visited = new HashSet<int>();
        var anglePerRoot = 2 * Math.PI / roots.Count;
        var rootRadius = roots.Count > 1 ? RootRingRadius : 0;
        var maxDepth = 0;

        for (var i = 0; i < roots.Count; i++)
        {
            var startAngle = i * anglePerRoot;
            PlaceSubtree(roots[i], 0, startAngle, startAngle + anglePerRoot, cx, cy, rootRadius,
                byParent, positions, visited, ref maxDepth);
        }

        LastRootCount = roots.Count;
        LastMaxDepth = maxDepth;

        // A tree deeper than the canvas can hold pushes its outer rings off-screen; worth saying
        // once in a while rather than leaving the user wondering where half the processes went.
        var outerRadius = rootRadius + maxDepth * RingSpacing;
        if (outerRadius > Math.Min(Width, Height) / 2)
            DebugLog.Throttled("layout-radius", TimeSpan.FromSeconds(10), "Layout",
                () => $"tree depth {maxDepth} needs a {outerRadius:F0}px radius but the canvas only offers " +
                      $"{Math.Min(Width, Height) / 2:F0}px - outer rings fall outside the viewport");

        return positions;
    }

    private static void PlaceSubtree(
        ProcessNode node,
        int depth,
        double startAngle,
        double endAngle,
        double cx,
        double cy,
        double rootRadius,
        ILookup<int, ProcessNode> byParent,
        List<RadialPosition> positions,
        HashSet<int> visited,
        ref int maxDepth)
    {
        if (!visited.Add(node.Pid))
            return;

        var angle = (startAngle + endAngle) / 2;
        var radius = rootRadius + depth * RingSpacing;
        if (depth > maxDepth)
            maxDepth = depth;

        positions.Add(new RadialPosition
        {
            Pid = node.Pid,
            X = cx + radius * Math.Cos(angle),
            Y = cy + radius * Math.Sin(angle),
        });

        var children = byParent[node.Pid]
            .Where(c => c.Pid != node.Pid)
            .OrderBy(c => c.Pid)
            .ToList();
        if (children.Count == 0)
            return;

        var childSpan = (endAngle - startAngle) / children.Count;
        for (var i = 0; i < children.Count; i++)
        {
            var childStart = startAngle + i * childSpan;
            PlaceSubtree(children[i], depth + 1, childStart, childStart + childSpan, cx, cy, rootRadius,
                byParent, positions, visited, ref maxDepth);
        }
    }
}
