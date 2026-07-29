namespace Orchestration.Core.Models;

/// <summary>Where the viewport sits over the infinite canvas.</summary>
public sealed class Camera
{
    /// <summary>
    /// Persistence clamps into this range so no consumer has to. A zoom of zero — reachable from a
    /// hand edit or a half-written file — divides by zero in every world-to-screen conversion, and
    /// the view cannot be the one to catch it: Core has non-UI readers too.
    /// </summary>
    public const double MinZoom = 0.3;
    public const double MaxZoom = 2.5;
    public const double DefaultZoom = 1.0;

    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double Zoom { get; set; } = DefaultZoom;
}
