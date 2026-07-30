using Microsoft.UI.Xaml;

namespace Orchestration.App.Views;

/// <summary>A node the canvas can move around. The handle is the only area that starts a drag.</summary>
public interface INodeView
{
    UIElement DragHandle { get; }
    UIElement ConnectionSurface { get; }
    UIElement ResizeGrip { get; }
    void ApplyZoom(double zoom);
    void SetSelected(bool selected);

    /// <summary>
    /// Far enough out, the node box is smaller than its own header and the 12 px font floor stops
    /// meaning anything. Collapsed nodes show identity only. This is presentation: a collapsed
    /// terminal keeps its session.
    /// </summary>
    void SetCollapsed(bool collapsed);
}
