using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Orchestration.Core.Models;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Orchestration.App;

/// <summary>Camera: pan, zoom and where each node lands on screen.</summary>
public sealed partial class MainWindow
{
    // Camera owns the range; keeping a second pair here is what let the view and the store disagree.
    private const double MinZoom = Camera.MinZoom, MaxZoom = Camera.MaxZoom;

    private const double CollapseZoom = Camera.CollapseZoom;

    /// <summary>Smallest a collapsed card is allowed to get on screen. See <see cref="PlaceNode"/>.</summary>
    private const double MinCardWidth = 208, MinCardHeight = 44;

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
        // A collapsed card is a label, not a scaled node, so it stops shrinking with the canvas:
        // at 15% a 720-wide node is 108 px, which clips away the very name the card exists to show.
        // The floor is the badge plus a few characters of title; longer titles still ellipsize.
        bool collapsed = _zoom < CollapseZoom;
        node.View.Width = collapsed ? Math.Max(node.Width * _zoom, MinCardWidth) : node.Width * _zoom;
        node.View.Height = collapsed ? Math.Max(node.Height * _zoom, MinCardHeight) : node.Height * _zoom;
        RenderWires();
    }

    private void ApplyLayout()
    {
        // Only the canvas on screen. Nodes on other tabs keep their sessions but not their layout:
        // they get placed when their tab comes back, and their zoom is whatever that tab's is.
        foreach (var node in ActiveNodes())
        {
            PlaceNode(node);
            node.Node.ApplyZoom(_zoom);
            node.Node.SetCollapsed(_zoom < CollapseZoom);
        }
        RenderAnnotations();
        DrawGrid();
        UpdateZoomLabel();
    }

    private void UpdateZoomLabel() => ZoomLabel.Text = $"{_zoom * 100:0}%";

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Viewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        DrawGrid();
    }

    // ---- pan and zoom -----------------------------------------------------

    private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Only the empty background pans; pointer events over a node belong to the node.
        if (!ReferenceEquals(e.OriginalSource, CanvasBackground)) return;
        if (TryStartCanvasTool(e)) return;
        SelectNode(null);
        _selectedWire = null;
        RenderWires();
        _panStart = e.GetCurrentPoint(Viewport).Position;
        _panning = Viewport.CapturePointer(e.Pointer);
    }

    private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (TryMoveCanvasTool(e)) return;
        if (!_panning) return;
        var now = e.GetCurrentPoint(Viewport).Position;
        _offsetX += now.X - _panStart.X;
        _offsetY += now.Y - _panStart.Y;
        _panStart = now;
        foreach (var node in ActiveNodes()) PlaceNode(node);
        RenderAnnotations();
        DrawGrid();
    }

    private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (TryEndCanvasTool(e)) return;
        if (!_panning) return;
        _panning = false;
        Viewport.ReleasePointerCapture(e.Pointer);
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
            ZoomAt(point.Position, delta > 0);
        }
        else
        {
            bool shiftDown = InputKeyboardSource
                .GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(CoreVirtualKeyStates.Down);
            if (shiftDown) _offsetX += delta; else _offsetY += delta;
            foreach (var node in ActiveNodes()) PlaceNode(node);
            RenderAnnotations();
            DrawGrid();
            // SaveCamera is the only thing that copies the offsets into the model, so skipping it
            // here would let a later unrelated save write a stale camera over the real one.
            SaveCamera();
        }
        e.Handled = true;
    }

    /// <summary>One zoom step, keeping the world point under <paramref name="anchor"/> pinned.</summary>
    private void ZoomAt(Point anchor, bool zoomIn)
    {
        double previous = _zoom;
        double next = Math.Clamp(_zoom * (zoomIn ? 1.1 : 1 / 1.1), MinZoom, MaxZoom);
        if (Math.Abs(next - previous) < 0.0001) return;

        double ratio = next / previous;
        _offsetX = anchor.X - (anchor.X - _offsetX) * ratio;
        _offsetY = anchor.Y - (anchor.Y - _offsetY) * ratio;
        _zoom = next;
        ApplyLayout();
        SaveCamera();
    }

    /// <summary>
    /// Ctrl+wheel that landed on a terminal's web content, forwarded by the page. Without this the
    /// gesture never reaches the canvas: the pointer is over a WebView2, so the browser answers
    /// first and zooms that one terminal's page. The node's centre is the anchor — the page's own
    /// coordinates do not survive the trip across the WebView2 boundary, and with the pointer on a
    /// node, keeping that node where it is was the intent anyway.
    /// </summary>
    private void ZoomAtNode(FrameworkElement view, double pageDelta)
    {
        Point centre = view
            .TransformToVisual(Viewport)
            .TransformPoint(new Point(view.ActualWidth / 2, view.ActualHeight / 2));
        // A page wheel delta is positive scrolling down — the opposite sign of the pointer's.
        ZoomAt(centre, pageDelta < 0);
    }

    /// <summary>
    /// Frames everything on the canvas. The button has always been called "ajustar zoom" but only
    /// reset to 100% at the origin, which on a wide canvas strands every node the user placed far
    /// out and reads as the canvas having lost them.
    /// </summary>
    private void OnResetView(object sender, RoutedEventArgs e)
    {
        double width = Viewport.ActualWidth, height = Viewport.ActualHeight;
        if (!TryContentBounds(out Rect content) || width <= 0 || height <= 0)
        {
            _zoom = Camera.DefaultZoom;
            _offsetX = _offsetY = 0;
        }
        else
        {
            const double padding = 60;
            double scale = Math.Min(
                (width - padding * 2) / Math.Max(content.Width, 1),
                (height - padding * 2) / Math.Max(content.Height, 1));
            _zoom = Math.Clamp(scale, MinZoom, MaxZoom);
            _offsetX = width / 2 - (content.X + content.Width / 2) * _zoom;
            _offsetY = height / 2 - (content.Y + content.Height / 2) * _zoom;
        }
        ApplyLayout();
        SaveCamera();
    }

    /// <summary>World-space bounding box of every node and annotation, or false when empty.</summary>
    private bool TryContentBounds(out Rect bounds)
    {
        double left = double.MaxValue, top = double.MaxValue;
        double right = double.MinValue, bottom = double.MinValue;

        void Grow(double x, double y, double w, double h)
        {
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x + w);
            bottom = Math.Max(bottom, y + h);
        }

        foreach (var node in ActiveNodes()) Grow(node.X, node.Y, node.Width, node.Height);
        foreach (var item in _canvas.CanvasItems)
        {
            if (item.Points.Count > 0)
                foreach (var point in item.Points) Grow(point.X, point.Y, 0, 0);
            else
                Grow(item.X, item.Y, 0, 0);
        }

        bounds = new Rect(left, top, Math.Max(right - left, 1), Math.Max(bottom - top, 1));
        return left != double.MaxValue;
    }

    private void SaveCamera()
    {
        _canvas.Camera.OffsetX = _offsetX;
        _canvas.Camera.OffsetY = _offsetY;
        _canvas.Camera.Zoom = _zoom;
        _autosave.Touch();
    }

    private void DrawGrid()
    {
        double width = Viewport.ActualWidth;
        double height = Viewport.ActualHeight;
        if (width <= 0 || height <= 0) return;

        GridLines.Children.Clear();

        // A fixed 40-unit cell becomes 4 px at the far end of the zoom range, which is thousands of
        // Line objects rebuilt on every pan. Stepping the cell by 5 keeps the on-screen spacing in a
        // readable band, so the line count stays in the dozens at any zoom.
        double cell = 40;
        while (cell * _zoom < 24) cell *= 5;
        while (cell * _zoom > 160) cell /= 5;

        double spacing = cell * _zoom;
        double startX = ((_offsetX % spacing) + spacing) % spacing;
        double startY = ((_offsetY % spacing) + spacing) % spacing;
        var minor = new SolidColorBrush(Windows.UI.Color.FromArgb(24, 121, 98, 140));
        var major = new SolidColorBrush(Windows.UI.Color.FromArgb(56, 121, 98, 140));

        for (double x = startX; x <= width; x += spacing)
        {
            long index = (long)Math.Round((x - _offsetX) / spacing);
            GridLines.Children.Add(new Line
            {
                X1 = x, X2 = x, Y1 = 0, Y2 = height,
                Stroke = index % 5 == 0 ? major : minor,
                StrokeThickness = index % 5 == 0 ? 1.2 : 1
            });
        }
        for (double y = startY; y <= height; y += spacing)
        {
            long index = (long)Math.Round((y - _offsetY) / spacing);
            GridLines.Children.Add(new Line
            {
                X1 = 0, X2 = width, Y1 = y, Y2 = y,
                Stroke = index % 5 == 0 ? major : minor,
                StrokeThickness = index % 5 == 0 ? 1.2 : 1
            });
        }
    }
}
