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
    private double _zoom = 1.0, _offsetX, _offsetY;
    private Point _panStart;
    private bool _panning;
    private Point _selectionStart;
    private bool _selecting;

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
        RenderWires();
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
        var point = e.GetCurrentPoint(Viewport);
        if (point.Properties.IsMiddleButtonPressed)
        {
            _panStart = point.Position;
            _panning = Viewport.CapturePointer(e.Pointer);
            e.Handled = _panning;
            return;
        }

        // Only the empty background starts tools or a marquee.
        if (!ReferenceEquals(e.OriginalSource, CanvasBackground)) return;
        if (!point.Properties.IsLeftButtonPressed) return;
        if (TryStartCanvasTool(e)) return;
        SelectNode(null);
        _selectedWire = null;
        RenderWires();
        _selectionStart = point.Position;
        _selecting = Viewport.CapturePointer(e.Pointer);
        SelectionMarquee.Visibility = _selecting ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection(point.Position);
        e.Handled = _selecting;
    }

    private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (TryMoveCanvasTool(e)) return;
        var now = e.GetCurrentPoint(Viewport).Position;
        if (_panning)
        {
            _offsetX += now.X - _panStart.X;
            _offsetY += now.Y - _panStart.Y;
            _panStart = now;
            foreach (var node in _nodes) PlaceNode(node);
            RenderWires();
            RenderAnnotations();
            DrawGrid();
            e.Handled = true;
        }
        else if (_selecting)
        {
            UpdateSelection(now);
            e.Handled = true;
        }
    }

    private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_panning)
        {
            _panning = false;
            Viewport.ReleasePointerCapture(e.Pointer);
            SaveCamera();
            e.Handled = true;
        }
        else if (_selecting)
        {
            _selecting = false;
            SelectionMarquee.Visibility = Visibility.Collapsed;
            Viewport.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
        else
        {
            TryEndCanvasTool(e);
        }
    }

    private void UpdateSelection(Point end)
    {
        double left = Math.Min(_selectionStart.X, end.X);
        double top = Math.Min(_selectionStart.Y, end.Y);
        double width = Math.Abs(end.X - _selectionStart.X);
        double height = Math.Abs(end.Y - _selectionStart.Y);
        Canvas.SetLeft(SelectionMarquee, left);
        Canvas.SetTop(SelectionMarquee, top);
        SelectionMarquee.Width = width;
        SelectionMarquee.Height = height;

        SelectNodes(_nodes.Where(node =>
        {
            double nodeLeft = Canvas.GetLeft(node.View);
            double nodeTop = Canvas.GetTop(node.View);
            return left <= nodeLeft + node.View.ActualWidth &&
                   left + width >= nodeLeft &&
                   top <= nodeTop + node.View.ActualHeight &&
                   top + height >= nodeTop;
        }));
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
            double next = Math.Clamp(
                _zoom * (delta > 0 ? 1.1 : 1 / 1.1),
                Camera.MinZoom,
                Camera.MaxZoom);
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
            RenderWires();
            RenderAnnotations();
            DrawGrid();
            // SaveCamera is the only thing that copies the offsets into the model, so skipping it
            // here would let a later unrelated save write a stale camera over the real one.
            SaveCamera();
        }
        e.Handled = true;
    }

    private void OnResetView(object sender, RoutedEventArgs e)
    {
        Camera camera = Camera.Fit(
            _workspace.Nodes,
            Viewport.ActualWidth,
            Viewport.ActualHeight);
        _zoom = camera.Zoom;
        _offsetX = camera.OffsetX;
        _offsetY = camera.OffsetY;
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

    private void DrawGrid()
    {
        double width = Viewport.ActualWidth;
        double height = Viewport.ActualHeight;
        if (width <= 0 || height <= 0) return;

        GridLines.Children.Clear();
        double worldSpacing = 40;
        while (worldSpacing * _zoom < 20) worldSpacing *= 5;
        double spacing = worldSpacing * _zoom;
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
