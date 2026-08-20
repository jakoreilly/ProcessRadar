using ProcessRadar.Models;

namespace ProcessRadar.Diagnostics;

/// <summary>
/// Watches consecutive layout passes for the three things that read as "the diagram is distorted":
/// nodes teleporting between frames (the layout is stateless and recomputed every frame, so any
/// instability in sibling ordering becomes visible jitter), nodes stacked on top of each other
/// (radial rings run out of angular room long before they run out of nodes), and nodes placed
/// outside the visible canvas (deep trees whose outer rings simply don't fit).
/// UI-thread only, so no locking.
/// </summary>
public sealed class LayoutDiagnostics
{
    /// <summary>A node shifting more than this between two frames 100ms apart is a jump, not motion -
    /// nothing in this layout is supposed to animate its position at all.</summary>
    private const double JumpThreshold = 6.0;

    /// <summary>Cell size for the overlap histogram - roughly one node diameter plus its label gap,
    /// so two nodes in one cell really are drawn on top of each other.</summary>
    private const double OverlapCellSize = 14.0;

    private Dictionary<int, (double X, double Y)> _previous = new();

    public int LastJumpCount { get; private set; }
    public int LastOverlapCount { get; private set; }
    public int LastOffscreenCount { get; private set; }

    public void Inspect(IReadOnlyList<RadialPosition> positions, int nodeCount, double worldWidth, double worldHeight)
    {
        var current = new Dictionary<int, (double X, double Y)>(positions.Count);
        var occupied = new Dictionary<(int, int), int>(positions.Count);

        var jumps = 0;
        var maxJump = 0.0;
        var jumpExample = 0;
        var offscreen = 0;
        var overlapping = 0;

        foreach (var p in positions)
        {
            current[p.Pid] = (p.X, p.Y);

            if (_previous.TryGetValue(p.Pid, out var was))
            {
                var moved = Math.Sqrt((p.X - was.X) * (p.X - was.X) + (p.Y - was.Y) * (p.Y - was.Y));
                if (moved > JumpThreshold)
                {
                    jumps++;
                    if (moved > maxJump)
                    {
                        maxJump = moved;
                        jumpExample = p.Pid;
                    }
                }
            }

            if (p.X < 0 || p.Y < 0 || p.X > worldWidth || p.Y > worldHeight)
                offscreen++;

            var cell = ((int)Math.Floor(p.X / OverlapCellSize), (int)Math.Floor(p.Y / OverlapCellSize));
            occupied.TryGetValue(cell, out var inCell);
            occupied[cell] = inCell + 1;
            if (inCell >= 1)
                overlapping++;
        }

        _previous = current;
        LastJumpCount = jumps;
        LastOverlapCount = overlapping;
        LastOffscreenCount = offscreen;

        // Nodes the layout never placed: a cycle in the parent chain, or a subtree whose root was
        // filtered out. Either way they silently vanish from the picture, which reads as corruption.
        var unplaced = nodeCount - positions.Count;

        if (jumps > 0)
            DebugLog.Throttled("layout-jump", TimeSpan.FromSeconds(2), "Layout",
                () => $"{jumps}/{positions.Count} nodes moved >{JumpThreshold:F0}px since the last frame " +
                      $"(worst {maxJump:F0}px, pid {jumpExample}) - unstable sibling ordering shows up as jitter");

        if (overlapping > 0)
            DebugLog.Throttled("layout-overlap", TimeSpan.FromSeconds(5), "Layout",
                () => $"{overlapping}/{positions.Count} nodes share a {OverlapCellSize:F0}px cell with another node - " +
                      "rings are angularly saturated, so dots and labels are drawn on top of each other");

        if (offscreen > 0)
            DebugLog.Throttled("layout-offscreen", TimeSpan.FromSeconds(5), "Layout",
                () => $"{offscreen}/{positions.Count} nodes lie outside the {worldWidth:F0}x{worldHeight:F0} canvas - " +
                      "zoom out or pan to reach them");

        if (unplaced > 0)
            DebugLog.Throttled("layout-unplaced", TimeSpan.FromSeconds(5), "Layout",
                () => $"{unplaced} of {nodeCount} nodes were never placed (parent cycle, or an ancestor missing " +
                      "from the filtered set) and are invisible this frame");
    }
}
