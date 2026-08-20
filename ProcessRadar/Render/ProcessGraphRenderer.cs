using ProcessRadar.Models;
using SkiaSharp;

namespace ProcessRadar.Render;

/// <summary>
/// Phase 3: draws the radial tree - parent/child edges, nodes scale-in ("pop") over their
/// first <see cref="SpawnAnimation"/> after <see cref="ProcessNode.FirstSeen"/>, and dead
/// nodes fade out over <see cref="FadeAnimation"/> after going non-alive, matching the plan's
/// "short-lived processes shown as a quick flash-and-fade".
/// </summary>
public static class ProcessGraphRenderer
{
    private static readonly TimeSpan SpawnAnimation = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan FadeAnimation = TimeSpan.FromSeconds(1);

    /// <summary>How long a node keeps its anomaly glow ring after <see cref="ProcessNode.LastAnomalyAt"/>
    /// - long enough to catch the eye, short enough to read as "just happened".</summary>
    private static readonly TimeSpan AnomalyFlashDuration = TimeSpan.FromSeconds(2.5);

    private static readonly SKColor Background = new(0x0b, 0x11, 0x20);
    private static readonly SKColor EdgeColor = new(0x33, 0x41, 0x55);
    private static readonly SKColor NodeFill = new(0x5e, 0xea, 0xd4);
    private static readonly SKColor DeadFill = new(0xf8, 0x71, 0x71);
    private static readonly SKColor AnomalyGlow = new(0xf8, 0x71, 0x71, 140);
    private static readonly SKColor InjectionColor = new(0xf8, 0x71, 0x71);
    private static readonly SKColor LabelColor = new(0xe8, 0xec, 0xf5);

    /// <summary>Marching-ants speed for injection edges, in dash-pattern units/second - fast
    /// enough to read as "this is live and different from a static tree edge" at a glance.</summary>
    private const double InjectionDashSpeed = 24;
    private static readonly float[] InjectionDashPattern = [8f, 5f];

    public static void Draw(
        SKCanvas canvas,
        IReadOnlyList<RadialPosition> positions,
        IReadOnlyDictionary<int, ProcessNode> nodesByPid,
        IReadOnlyList<Models.InjectionEdge> injectionEdges,
        DateTime now)
    {
        canvas.Clear(Background);

        var posByPid = new Dictionary<int, RadialPosition>(positions.Count);
        foreach (var p in positions)
            posByPid[p.Pid] = p;

        using var edgePaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = EdgeColor };
        foreach (var (pid, node) in nodesByPid)
        {
            if (!posByPid.TryGetValue(pid, out var pos) || !posByPid.TryGetValue(node.ParentPid, out var parentPos))
                continue;
            if (node.ParentPid == pid)
                continue;
            canvas.DrawLine((float)parentPos.X, (float)parentPos.Y, (float)pos.X, (float)pos.Y, edgePaint);
        }

        var dashPhase = (float)(now.TimeOfDay.TotalSeconds * InjectionDashSpeed
            % (InjectionDashPattern[0] + InjectionDashPattern[1]));
        using var injectionPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2.5f,
            Color = InjectionColor,
            StrokeCap = SKStrokeCap.Round,
            PathEffect = SKPathEffect.CreateDash(InjectionDashPattern, dashPhase),
        };
        foreach (var edge in injectionEdges)
        {
            if (!posByPid.TryGetValue(edge.SourcePid, out var a) || !posByPid.TryGetValue(edge.TargetPid, out var b))
                continue;
            canvas.DrawLine((float)a.X, (float)a.Y, (float)b.X, (float)b.Y, injectionPaint);
        }

        using var nodePaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var anomalyGlowPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = AnomalyGlow, ImageFilter = SKImageFilter.CreateBlur(10, 10) };
        using var font = new SKFont(SKTypeface.FromFamilyName("Consolas"), 10);
        using var textPaint = new SKPaint { IsAntialias = true, Color = LabelColor };

        foreach (var p in positions)
        {
            if (!nodesByPid.TryGetValue(p.Pid, out var node))
                continue;

            var scale = SpawnScale(node, now);
            var alpha = node.Alive ? 1.0 : FadeAlpha(node, now);
            if (scale <= 0 || alpha <= 0)
                continue;

            var radius = 6.0 * scale;

            if (IsFlashing(node.LastAnomalyAt, now))
                canvas.DrawCircle((float)p.X, (float)p.Y, (float)radius + 6, anomalyGlowPaint);

            var color = node.Alive ? NodeFill : DeadFill;
            nodePaint.Color = color.WithAlpha((byte)(alpha * 255));
            canvas.DrawCircle((float)p.X, (float)p.Y, (float)radius, nodePaint);

            if (alpha > 0.5)
            {
                textPaint.Color = LabelColor.WithAlpha((byte)(alpha * 255));
                canvas.DrawText(node.Name, (float)p.X + (float)radius + 3, (float)p.Y + 3, font, textPaint);
            }
        }
    }

    private static double SpawnScale(ProcessNode node, DateTime now)
    {
        var elapsed = now - node.FirstSeen;
        if (elapsed >= SpawnAnimation)
            return 1.0;
        if (elapsed < TimeSpan.Zero)
            return 1.0;
        return Math.Clamp(elapsed.TotalMilliseconds / SpawnAnimation.TotalMilliseconds, 0, 1);
    }

    private static double FadeAlpha(ProcessNode node, DateTime now)
    {
        var sinceDeath = now - node.LastSeen;
        if (sinceDeath >= FadeAnimation)
            return 0;
        return 1.0 - Math.Clamp(sinceDeath.TotalMilliseconds / FadeAnimation.TotalMilliseconds, 0, 1);
    }

    private static bool IsFlashing(DateTime? lastAnomalyAt, DateTime now) =>
        lastAnomalyAt is DateTime at && now - at <= AnomalyFlashDuration;
}
