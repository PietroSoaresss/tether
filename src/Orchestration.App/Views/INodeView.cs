using Microsoft.UI.Xaml;

namespace Orchestration.App.Views;

/// <summary>A node the canvas can move around. The handle is the only area that starts a drag.</summary>
public interface INodeView
{
    UIElement DragHandle { get; }
    void ApplyZoom(double zoom);
}
