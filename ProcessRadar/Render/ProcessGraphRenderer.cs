using ProcessRadar.Models;
using SkiaSharp;

namespace ProcessRadar.Render;

/// <summary>
/// Phase 3: draws the radial tree - parent/child edges, nodes scale-in ("pop") over their
/// first <see cref="SpawnAnimation"/> after <see cref="ProcessNode.FirstSeen"/>, and dead
/// nodes fade out over <see cref="FadeAnimation"/> after going non-alive, matching the plan's
/// "short-lived processes shown as a quick flash-and-fade".
///
/// Phase 6: everything is drawn through a <see cref="ViewTransform"/> (zoom + pan) and a device
/// scale. The device scale matters on its own: SKElement hands us a surface in *device pixels*
/// while the layout works in DIPs, so on a 150% display an untransformed draw lands the whole
/// diagram in the top-left two-thirds of the canvas at two-thirds size - the "distortion" this
/// pass exists to remove. Off-screen nodes and edges are culled, which is what makes zooming in
/// on a 400-node tree cheaper rather than more expensive.
/// </summary>
public static class ProcessGraphRenderer
{
    private static readonly TimeSpan SpawnAnimation = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan FadeAnimation = TimeSpan.FromSeconds(1);

    /// <summary>How long a node keeps its anomaly glow ring after <see cref="ProcessNode.LastAnomalyAt"/>
    /// - long enough to catch the eye, short enough to read as "just happened".</summary>
    private static readonly TimeSpan AnomalyFlashDuration = TimeSpan.FromSeconds(2.5);

    /// <summary>Below this zoom the labels are unreadable anyway and drawing them just produces a
    /// grey smear over the dots (and costs a text shaping pass per node), so they're dropped.</summary>
    private const double LabelZoomThreshold = 0.85;

    /// <summary>World-space slack around the viewport when culling, so a node just off the edge
    /// still contributes its edge line and its label doesn't pop in late.</summary>
    private const double CullMargin = 60;

    private static readonly SKColor Background = new(0x0b, 0x11, 0x20);
    private static readonly SKColor EdgeColor = new(0x33, 0x41, 0x55);
    private static readonly SKColor NodeFill = new(0x5e, 0xea, 0xd4);
    private static readonly SKColor DeadFill = new(0xf8, 0x71, 0x71);
    private static readonly SKColor AnomalyGlow = new(0xf8, 0x71, 0x71, 140);
    private static readonly SKColor InjectionColor = new(0xf8, 0x71, 0x71);
    private static readonly SKColor LabelColor = new(0xe8, 0xec, 0xf5);
    private static readonly SKColor HudColor = new(0x94, 0xa3, 0xb8);
    private static readonly SKColor HudBackground = new(0x0b, 0x11, 0x20, 200);

    /// <summary>Looked up once and reused for every frame's node labels and HUD text - creating a
    /// fresh <see cref="SKTypeface"/> per <see cref="Draw"/> call (10fps, plus the debug HUD) did a
    /// font-manager lookup and left the native handle for the GC finalizer every single frame.
    /// The fallback matters on a machine without Consolas; the old call passed the null straight on.</summary>
    private static readonly SKTypeface MonoTypeface = SKTypeface.FromFamilyName("Consolas") ?? SKTypeface.Default;

    /// <summary>Marching-ants speed for injection edges, in dash-pattern units/second - fast
    /// enough to read as "this is live and different from a static tree edge" at a glance.</summary>
    private const double InjectionDashSpeed = 24;
    private static readonly float[] InjectionDashPattern = [8f, 5f];

    /// <summary>Result of one <see cref="Draw"/> call, for the debug HUD and the log - how much of
    /// the frame's work actually reached the screen.</summary>
    public readonly record struct DrawStats(int NodesDrawn, int NodesCulled, int EdgesDrawn, int LabelsDrawn);

    public static DrawStats Draw(
        SKCanvas canvas,
        IReadOnlyList<RadialPosition> positions,
        IReadOnlyDictionary<int, ProcessNode> nodesByPid,
        IReadOnlyList<Models.InjectionEdge> injectionEdges,
        DateTime now,
        ViewTransform view,
        double deviceScale,
        double viewportWidthDip,
        double viewportHeightDip,
        IReadOnlyList<string>? hudLines = null)
    {
        canvas.Clear(Background);

        var posByPid = new Dictionary<int, RadialPosition>(positions.Count);
        foreach (var p in positions)
            posByPid[p.Pid] = p;

        // Visible world rectangle, so culling can be a plain bounds test per node.
        var (worldLeft, worldTop) = view.ToWorld(0, 0);
        var (worldRight, worldBottom) = view.ToWorld(viewportWidthDip, viewportHeightDip);
        worldLeft -= CullMargin;
        worldTop -= CullMargin;
        worldRight += CullMargin;
        worldBottom += CullMargin;

        bool Visible(RadialPosition p) =>
            p.X >= worldLeft && p.X <= worldRight && p.Y >= worldTop && p.Y <= worldBottom;

        var nodesDrawn = 0;
        var nodesCulled = 0;
        var edgesDrawn = 0;
        var labelsDrawn = 0;

        canvas.Save();
        canvas.Scale((float)deviceScale);
        canvas.Translate((float)view.PanX, (float)view.PanY);
        canvas.Scale((float)view.Zoom);

        // Sizes are specified in DIPs and then multiplied by the canvas transform, so a naive
        // draw makes a 10x zoom produce 10x-tall glyphs. Dividing straight through by zoom is the
        // other extreme: dots would stay exactly as small as they are now and zooming would only
        // spread them out. sqrt(zoom) splits the difference - 4x zoom spreads nodes 4x apart and
        // draws them 2x bigger, so the dots genuinely get readable without the labels turning
        // into billboards. invZoom is kept for the things that should stay hairline-thin.
        var invZoom = 1.0 / view.Zoom;
        var sizeFactor = Math.Sqrt(view.Zoom) / view.Zoom;

        using var edgePaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)invZoom,
            Color = EdgeColor,
        };
        foreach (var (pid, node) in nodesByPid)
        {
            if (!posByPid.TryGetValue(pid, out var pos) || !posByPid.TryGetValue(node.ParentPid, out var parentPos))
                continue;
            if (node.ParentPid == pid)
                continue;
            if (!Visible(pos) && !Visible(parentPos))
                continue;
            canvas.DrawLine((float)parentPos.X, (float)parentPos.Y, (float)pos.X, (float)pos.Y, edgePaint);
            edgesDrawn++;
        }

        var dashPhase = (float)(now.TimeOfDay.TotalSeconds * InjectionDashSpeed
            % (InjectionDashPattern[0] + InjectionDashPattern[1]));
        var dashPattern = new[] { (float)(InjectionDashPattern[0] * sizeFactor), (float)(InjectionDashPattern[1] * sizeFactor) };
        using var injectionPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)(2.5 * sizeFactor),
            Color = InjectionColor,
            StrokeCap = SKStrokeCap.Round,
            PathEffect = SKPathEffect.CreateDash(dashPattern, (float)(dashPhase * sizeFactor)),
        };
        foreach (var edge in injectionEdges)
        {
            if (!posByPid.TryGetValue(edge.SourcePid, out var a) || !posByPid.TryGetValue(edge.TargetPid, out var b))
                continue;
            if (!Visible(a) && !Visible(b))
                continue;
            canvas.DrawLine((float)a.X, (float)a.Y, (float)b.X, (float)b.Y, injectionPaint);
        }

        using var nodePaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var anomalyGlowPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = AnomalyGlow,
            ImageFilter = SKImageFilter.CreateBlur((float)(10 * sizeFactor), (float)(10 * sizeFactor)),
        };
        using var font = new SKFont(MonoTypeface,(float)(11 * sizeFactor));
        using var textPaint = new SKPaint { IsAntialias = true, Color = LabelColor };

        var drawLabels = view.Zoom >= LabelZoomThreshold;

        foreach (var p in positions)
        {
            if (!nodesByPid.TryGetValue(p.Pid, out var node))
                continue;

            if (!Visible(p))
            {
                nodesCulled++;
                continue;
            }

            var scale = SpawnScale(node, now);
            var alpha = node.Alive ? 1.0 : FadeAlpha(node, now);
            if (scale <= 0 || alpha <= 0)
                continue;

            var radius = 6.0 * scale * sizeFactor;
            nodesDrawn++;

            if (IsFlashing(node.LastAnomalyAt, now))
                canvas.DrawCircle((float)p.X, (float)p.Y, (float)(radius + 6 * sizeFactor), anomalyGlowPaint);

            var color = node.Alive ? NodeFill : DeadFill;
            nodePaint.Color = color.WithAlpha((byte)(alpha * 255));
            canvas.DrawCircle((float)p.X, (float)p.Y, (float)radius, nodePaint);

            if (drawLabels && alpha > 0.5)
            {
                textPaint.Color = LabelColor.WithAlpha((byte)(alpha * 255));
                canvas.DrawText(node.Name,
                    (float)(p.X + radius + 3 * sizeFactor),
                    (float)(p.Y + 3 * sizeFactor),
                    SKTextAlign.Left, font, textPaint);
                labelsDrawn++;
            }
        }

        canvas.Restore();

        if (hudLines is { Count: > 0 })
            DrawHud(canvas, hudLines, deviceScale);

        return new DrawStats(nodesDrawn, nodesCulled, edgesDrawn, labelsDrawn);
    }

    /// <summary>Draws the debug overlay in screen space (device pixels), deliberately outside the
    /// zoom/pan transform so it stays legible and put no matter where the view is.</summary>
    private static void DrawHud(SKCanvas canvas, IReadOnlyList<string> lines, double deviceScale)
    {
        using var font = new SKFont(MonoTypeface,(float)(11 * deviceScale));
        using var textPaint = new SKPaint { IsAntialias = true, Color = HudColor };
        using var boxPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = HudBackground };

        var pad = (float)(6 * deviceScale);
        var lineHeight = font.Size * 1.4f;
        var width = 0f;
        foreach (var line in lines)
            width = Math.Max(width, font.MeasureText(line));

        canvas.DrawRect(pad, pad, width + pad * 2, lines.Count * lineHeight + pad, boxPaint);

        var y = pad * 2 + font.Size;
        foreach (var line in lines)
        {
            canvas.DrawText(line, pad * 2, y, SKTextAlign.Left, font, textPaint);
            y += lineHeight;
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
