using ProcessRadar.Models;

namespace ProcessRadar.Graph;

/// <summary>
/// Phase 3: radial tree layout - roots (processes whose parent isn't in the map, e.g. System,
/// services.exe, orphans) sit at the center, each generation gets a wider ring, and siblings
/// split their parent's angular slice evenly. Stateless: recomputed fresh from the current
/// node set each call, since process trees restructure (reparenting, exits) far more than a
/// packet graph's IPs do, so there's no persisted-position smoothing to get right yet.
/// </summary>
public sealed class RadialTreeLayout
{
    private const double RingSpacing = 70;

    public double Width { get; set; } = 800;
    public double Height { get; set; } = 500;

    public IReadOnlyList<RadialPosition> Compute(IReadOnlyList<ProcessNode> nodes)
    {
        var positions = new List<RadialPosition>();
        if (nodes.Count == 0)
            return positions;

        var byPid = nodes.ToDictionary(n => n.Pid);
        var byParent = nodes.ToLookup(n => n.ParentPid);
        var roots = nodes.Where(n => n.ParentPid == n.Pid || !byPid.ContainsKey(n.ParentPid)).ToList();
        if (roots.Count == 0)
            roots = nodes.Take(1).ToList();

        var cx = Width / 2;
        var cy = Height / 2;
        var visited = new HashSet<int>();
        var anglePerRoot = 2 * Math.PI / roots.Count;

        for (var i = 0; i < roots.Count; i++)
        {
            var startAngle = i * anglePerRoot;
            PlaceSubtree(roots[i], 0, startAngle, startAngle + anglePerRoot, cx, cy, byParent, positions, visited);
        }

        return positions;
    }

    private static void PlaceSubtree(
        ProcessNode node,
        int depth,
        double startAngle,
        double endAngle,
        double cx,
        double cy,
        ILookup<int, ProcessNode> byParent,
        List<RadialPosition> positions,
        HashSet<int> visited)
    {
        if (!visited.Add(node.Pid))
            return;

        var angle = (startAngle + endAngle) / 2;
        var radius = depth * RingSpacing;
        positions.Add(new RadialPosition
        {
            Pid = node.Pid,
            X = cx + radius * Math.Cos(angle),
            Y = cy + radius * Math.Sin(angle),
        });

        var children = byParent[node.Pid].Where(c => c.Pid != node.Pid).ToList();
        if (children.Count == 0)
            return;

        var childSpan = (endAngle - startAngle) / children.Count;
        for (var i = 0; i < children.Count; i++)
        {
            var childStart = startAngle + i * childSpan;
            PlaceSubtree(children[i], depth + 1, childStart, childStart + childSpan, cx, cy, byParent, positions, visited);
        }
    }
}
