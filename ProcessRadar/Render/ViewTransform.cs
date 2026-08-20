namespace ProcessRadar.Render;

/// <summary>
/// Phase 6: zoom/pan state for the graph canvas.
///
/// Three coordinate spaces are in play and conflating them is what makes a Skia-in-WPF diagram
/// look distorted:
/// <list type="bullet">
///   <item><description><b>world</b> - what <see cref="Graph.RadialTreeLayout"/> produces, sized to
///   the element's DIP size.</description></item>
///   <item><description><b>screen (DIP)</b> - where the mouse lives. screen = Pan + world * Zoom.</description></item>
///   <item><description><b>canvas (device px)</b> - what Skia actually paints into, which on a
///   150% display is 1.5x the DIP size. canvas = screen * DeviceScale.</description></item>
/// </list>
/// The renderer applies Scale(DeviceScale) -> Translate(Pan) -> Scale(Zoom) so Pan and every
/// stroke width stay in DIPs, and hit-testing only has to undo the DIP half of it.
/// </summary>
public sealed class ViewTransform
{
    public const double MinZoom = 0.2;
    public const double MaxZoom = 12.0;

    /// <summary>One wheel notch. 1.15 is small enough to feel continuous, big enough that you get
    /// from "unreadable dots" to "readable labels" in a couple of flicks.</summary>
    public const double WheelStep = 1.15;

    public double Zoom { get; private set; } = 1.0;
    public double PanX { get; private set; }
    public double PanY { get; private set; }

    public bool IsDefault => Zoom == 1.0 && PanX == 0 && PanY == 0;

    /// <summary>Screen (DIP) point -> world point, for hit-testing a click against layout output.</summary>
    public (double X, double Y) ToWorld(double screenX, double screenY) =>
        ((screenX - PanX) / Zoom, (screenY - PanY) / Zoom);

    /// <summary>World point -> screen (DIP) point.</summary>
    public (double X, double Y) ToScreen(double worldX, double worldY) =>
        (worldX * Zoom + PanX, worldY * Zoom + PanY);

    /// <summary>Zooms by <paramref name="factor"/> keeping the world point currently under
    /// (<paramref name="screenX"/>, <paramref name="screenY"/>) pinned there - i.e. the diagram
    /// grows out of the cursor rather than out of the top-left corner.</summary>
    public void ZoomAt(double screenX, double screenY, double factor)
    {
        var (worldX, worldY) = ToWorld(screenX, screenY);
        var target = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
        if (target == Zoom)
            return;

        Zoom = target;
        PanX = screenX - worldX * Zoom;
        PanY = screenY - worldY * Zoom;
    }

    public void PanBy(double dxScreen, double dyScreen)
    {
        PanX += dxScreen;
        PanY += dyScreen;
    }

    public void Reset()
    {
        Zoom = 1.0;
        PanX = 0;
        PanY = 0;
    }
}
