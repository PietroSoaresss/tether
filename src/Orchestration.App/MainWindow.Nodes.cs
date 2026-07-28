using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Orchestration.App.Views;
using Windows.Foundation;

namespace Orchestration.App;

/// <summary>Creating, removing and dragging the things on the canvas.</summary>
public sealed partial class MainWindow
{
    private sealed class CanvasNode
    {
        public required FrameworkElement View;
        public required INodeView Node;
        public double X, Y, Width, Height;
    }

    private readonly List<CanvasNode> _nodes = new();
    private double _spawnCursor;

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
