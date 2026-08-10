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
    /// Below this a node stops rendering content and becomes a card — session still running. The
    /// threshold sits where the miniature stops carrying information: under 15% a terminal's type
    /// is below 2 px, pure noise, and hiding the WebView2s is what buys the frame time back on a
    /// canvas with many of them. Identity at a distance comes from position, colour and zooming
    /// back in, the way it does on any minimap — not from a card that refuses to shrink.
    /// </summary>
    public const double CollapseZoom = 0.15;

    /// <summary>
    /// The type size a node draws at. Strictly proportional, and that is a correctness requirement,
    /// not taste: a terminal runs at <c>(width × zoom) / (charWidth × zoom)</c> columns, constant
    /// only while both sides scale together. A clamp takes zoom out of the numerator and the node
    /// starts resizing the pseudoconsole on every notch, so the shell reflows its output. Legibility
    /// when the box gets small is <see cref="CollapseZoom"/>'s job. The floor here is only the
    /// "a font size must be positive" rule that xterm and XAML both impose.
    /// </summary>
    public static double FontSize(double baseSize, double zoom) => Math.Max(baseSize * zoom, 1);

    /// <summary>
    /// The scale a node's header paints at. Above 1× it clamps: the bar and its letters hold their
    /// standard size no matter how far in the user goes. Below 1× it tracks the zoom, so a
    /// zoomed-out canvas is a field of faithful miniatures — full-size headers on shrunken bodies
    /// left every node mostly bar, and density is the whole reason anyone zooms out.
    /// </summary>
    public static double ChromeScale(double zoom) => Math.Min(zoom, 1);

    /// <summary>Below this a label is a smudge rather than a word.</summary>
    public const double MinLabelSize = 12;

    /// <summary>
    /// Type the user wrote onto the canvas itself. Unlike a node body nothing measures columns
    /// against it, so it is free to stop shrinking — and it has to: text written at 30 is 3 px at
    /// the far end of the zoom range, and zooming out to find what you wrote is the whole reason
    /// anyone zooms out. The floor never exceeds the size the user picked, or a 10 px label would
    /// come back bigger than they set it at 100%.
    /// </summary>
    public static double LabelSize(double baseSize, double zoom) =>
        Math.Max(baseSize * zoom, Math.Min(baseSize, MinLabelSize));

    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double Zoom { get; set; } = DefaultZoom;
}
