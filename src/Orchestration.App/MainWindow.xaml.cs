using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Orchestration.App.Views;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Orchestration.App;

public sealed partial class MainWindow : Window
{
    private sealed class CanvasNode
    {
        public required FrameworkElement View;
        public required INodeView Node;
        public double X, Y, Width, Height;
    }

    private const double MinZoom = 0.3, MaxZoom = 2.5;

    private readonly List<CanvasNode> _nodes = new();
    private double _zoom = 1.0, _offsetX, _offsetY;
    private Point _panStart;
    private bool _panning;
    private double _spawnCursor;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Orchestration";
        UpdateZoomLabel();

        // Empty canvas teaches nothing. Until a saved workspace exists, seed one of each.
        OnNewTerminal(this, new RoutedEventArgs());
        OnNewNote(this, new RoutedEventArgs());

        Closed += (_, _) =>
        {
            foreach (var node in _nodes)
                if (node.Node is TerminalNodeView terminal) terminal.DisposeSession();
        };
    }

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
    }

    // ---- nodes ------------------------------------------------------------

    private void AddNode(FrameworkElement view, INodeView node, double width, double height)
    {
        // Stagger new nodes so they do not land on top of each other.
        var entry = new CanvasNode
        {
            View = view,
            Node = node,
            X = (40 + _spawnCursor * 28 - _offsetX) / _zoom,
            Y = (40 + _spawnCursor * 28 - _offsetY) / _zoom,
            Width = width,
            Height = height
        };
        _spawnCursor = (_spawnCursor + 1) % 8;

        _nodes.Add(entry);
        World.Children.Add(view);
        PlaceNode(entry);
        node.ApplyZoom(_zoom);
        RegisterDrag(entry);
    }

    private void RemoveNode(FrameworkElement view)
    {
        var entry = _nodes.FirstOrDefault(n => ReferenceEquals(n.View, view));
        if (entry is null) return;
        _nodes.Remove(entry);
        World.Children.Remove(view);
    }

    private void RegisterDrag(CanvasNode entry)
    {
        var handle = entry.Node.DragHandle;
        Point last = default;
        bool dragging = false;

        handle.PointerPressed += (s, e) =>
        {
            last = e.GetCurrentPoint(Viewport).Position;
            dragging = ((UIElement)s).CapturePointer(e.Pointer);
            e.Handled = true;
        };

        handle.PointerMoved += (s, e) =>
        {
            if (!dragging) return;
            var now = e.GetCurrentPoint(Viewport).Position;
            entry.X += (now.X - last.X) / _zoom;
            entry.Y += (now.Y - last.Y) / _zoom;
            last = now;
            PlaceNode(entry);
        };

        void EndDrag(object s, PointerRoutedEventArgs e)
        {
            if (!dragging) return;
            dragging = false;
            ((UIElement)s).ReleasePointerCapture(e.Pointer);
        }

        handle.PointerReleased += EndDrag;
        handle.PointerCaptureLost += (s, e) => dragging = false;
    }

    private void OnNewTerminal(object sender, RoutedEventArgs e)
    {
        var terminal = new TerminalNodeView { CommandLine = "powershell.exe -NoLogo" };
        terminal.CloseRequested += view => RemoveNode(view);
        AddNode(terminal, terminal, 720, 420);
    }

    private void OnNewNote(object sender, RoutedEventArgs e)
    {
        var note = new NoteNodeView { Markdown = "# Nota\n\nTexto em markdown." };
        note.CloseRequested += view => RemoveNode(view);
        AddNode(note, note, 340, 240);
    }
}
