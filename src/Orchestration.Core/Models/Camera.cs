namespace Orchestration.Core.Models;

/// <summary>Where the viewport sits over the infinite canvas.</summary>
public sealed class Camera
{
    /// <summary>
    /// Persistence clamps into this range so no consumer has to. A zoom of zero — reachable from a
    /// hand edit or a half-written file — divides by zero in every world-to-screen conversion, and
    /// the view cannot be the one to catch it: Core has non-UI readers too.
    /// </summary>
    public const double MinZoom = 0.05;
    public const double MaxZoom = 2.5;
    public const double DefaultZoom = 1.0;

    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double Zoom { get; set; } = DefaultZoom;

    public static Camera Fit(
        IEnumerable<NodeBase> nodes,
        double viewportWidth,
        double viewportHeight,
        double padding = 64)
    {
        var items = nodes.ToList();
        if (items.Count == 0 || viewportWidth <= 0 || viewportHeight <= 0)
            return new Camera();

        double left = items.Min(node => node.X);
        double top = items.Min(node => node.Y);
        double right = items.Max(node => node.X + node.Width);
        double bottom = items.Max(node => node.Y + node.Height);
        double width = Math.Max(1, right - left);
        double height = Math.Max(1, bottom - top);
        double zoom = Math.Clamp(
            Math.Min(
                Math.Max(1, viewportWidth - padding * 2) / width,
                Math.Max(1, viewportHeight - padding * 2) / height),
            MinZoom,
            MaxZoom);

        return new Camera
        {
            Zoom = zoom,
            OffsetX = (viewportWidth - width * zoom) / 2 - left * zoom,
            OffsetY = (viewportHeight - height * zoom) / 2 - top * zoom
        };
    }
}
