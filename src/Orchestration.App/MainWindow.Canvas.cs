using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Orchestration.App;

/// <summary>Camera: pan, zoom and where each node lands on screen.</summary>
public sealed partial class MainWindow
{
    private const double MinZoom = 0.3, MaxZoom = 2.5;

    private double _zoom = 1.0, _offsetX, _offsetY;
    private Point _panStart;
    private bool _panning;

    // ---- canvas transform -------------------------------------------------

    /// <summary>
    /// Zoom is baked into each node's position and size rather than a RenderTransform:
    /// WebView2 is not composed by XAML, so scaling it leaves the web content unpainted.
    /// </summary>
    private void PlaceNode(CanvasNode node)
    {
        Canvas.SetLeft(node.View, node.X * _zoom + _offsetX);
        Canvas.SetTop(node.View, node.Y * _zoom + _offsetY);
        node.View.Width = node.Width * _zoom;
        node.View.Height = node.Height * _zoom;
    }

    private void ApplyLayout()
    {
        foreach (var node in _nodes)
        {
            PlaceNode(node);
            node.Node.ApplyZoom(_zoom);
        }
        UpdateZoomLabel();
    }

    private void UpdateZoomLabel() => ZoomLabel.Text = $"{_zoom * 100:0}%";

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e) =>
        Viewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };

    // ---- pan and zoom -----------------------------------------------------

    private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Only the empty background pans; pointer events over a node belong to the node.
        if (!ReferenceEquals(e.OriginalSource, World)) return;
        _panStart = e.GetCurrentPoint(Viewport).Position;
        _panning = World.CapturePointer(e.Pointer);
    }

    private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning) return;
        var now = e.GetCurrentPoint(Viewport).Position;
        _offsetX += now.X - _panStart.X;
        _offsetY += now.Y - _panStart.Y;
        _panStart = now;
        foreach (var node in _nodes) PlaceNode(node);
    }

    private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        World.ReleasePointerCapture(e.Pointer);
        SaveCamera();
    }

    private void OnCanvasWheel(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Viewport);
        int delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;

        bool ctrlDown = InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);

        if (ctrlDown)
        {
            double previous = _zoom;
            double next = Math.Clamp(_zoom * (delta > 0 ? 1.1 : 1 / 1.1), MinZoom, MaxZoom);
            if (Math.Abs(next - previous) < 0.0001) return;

            // Keep the world point under the cursor pinned while the scale changes.
            double ratio = next / previous;
            _offsetX = point.Position.X - (point.Position.X - _offsetX) * ratio;
            _offsetY = point.Position.Y - (point.Position.Y - _offsetY) * ratio;
            _zoom = next;
            ApplyLayout();
            SaveCamera();
        }
        else
        {
            bool shiftDown = InputKeyboardSource
                .GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(CoreVirtualKeyStates.Down);
            if (shiftDown) _offsetX += delta; else _offsetY += delta;
            foreach (var node in _nodes) PlaceNode(node);
        }
        e.Handled = true;
    }

    private void OnResetView(object sender, RoutedEventArgs e)
    {
        _zoom = 1.0;
        _offsetX = _offsetY = 0;
        ApplyLayout();
        SaveCamera();
    }

    private void SaveCamera()
    {
        _workspace.Camera.OffsetX = _offsetX;
        _workspace.Camera.OffsetY = _offsetY;
        _workspace.Camera.Zoom = _zoom;
        _autosave.Touch();
    }
}
