namespace Orchestration.Core.Models;

/// <summary>Where the viewport sits over the infinite canvas.</summary>
public sealed class Camera
{
    /// <summary>
    /// Persistence clamps into this range so no consumer has to. A zoom of zero — reachable from a
    /// hand edit or a half-written file — divides by zero in every world-to-screen conversion, and
    /// the view cannot be the one to catch it: Core has non-UI readers too.
    /// </summary>
    /// <summary>
    /// The single source of truth for the zoom range: the view used to keep its own narrower pair,
    /// so a workspace could persist a zoom the UI could neither reach nor reproduce.
    /// </summary>
    public const double MinZoom = 0.1;
    public const double MaxZoom = 4.0;
    public const double DefaultZoom = 1.0;

    /// <summary>
    /// Below this a node stops being a scaled view and becomes a card: identity only, session still
    /// running. This is what keeps PRODUCT.md principle 5 — a card is readable at any zoom, whereas
    /// a terminal with its font clamped is a handful of garbled columns.
    /// </summary>
    public const double CollapseZoom = 0.4;

    /// <summary>
    /// The type size a node draws at. Strictly proportional, and that is a correctness requirement,
    /// not taste: a terminal runs at <c>(width × zoom) / (charWidth × zoom)</c> columns, constant
    /// only while both sides scale together. A clamp takes zoom out of the numerator and the node
    /// starts resizing the pseudoconsole on every notch, so the shell reflows its output. Legibility
    /// when the box gets small is <see cref="CollapseZoom"/>'s job. The floor here is only the
    /// "a font size must be positive" rule that xterm and XAML both impose.
    /// </summary>
    public static double FontSize(double baseSize, double zoom) => Math.Max(baseSize * zoom, 1);

    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double Zoom { get; set; } = DefaultZoom;
}
