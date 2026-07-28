using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Orchestration.App.Views;
using Orchestration.Core.Models;
using Windows.Foundation;

namespace Orchestration.App;

/// <summary>Creating, removing and dragging the things on the canvas.</summary>
public sealed partial class MainWindow
{
    private sealed class CanvasNode
    {
        public required FrameworkElement View;
        public required INodeView Node;
        public required NodeBase Model;

        public double X { get => Model.X; set => Model.X = value; }
        public double Y { get => Model.Y; set => Model.Y = value; }
        public double Width { get => Model.Width; set => Model.Width = value; }
        public double Height { get => Model.Height; set => Model.Height = value; }
    }

    private readonly List<CanvasNode> _nodes = new();
    private double _spawnCursor;

    /// <summary>Adds a node that already has a model — used both by the toolbar and by workspace load.</summary>
    private void AddNode(FrameworkElement view, INodeView node, NodeBase model)
    {
        var entry = new CanvasNode { View = view, Node = node, Model = model };

        _nodes.Add(entry);
        _workspace.Nodes.Add(model);
        World.Children.Add(view);
        PlaceNode(entry);
        node.ApplyZoom(_zoom);
        RegisterDrag(entry);
        // AddNode serves both the toolbar and the load path; only the former is a user edit.
        if (!_loading) _autosave.Touch();
    }

    /// <summary>Stagger new nodes in world space so they do not land on top of each other.</summary>
    private (double X, double Y) NextSpawnPoint()
    {
        var point = ((40 + _spawnCursor * 28 - _offsetX) / _zoom, (40 + _spawnCursor * 28 - _offsetY) / _zoom);
        _spawnCursor = (_spawnCursor + 1) % 8;
        return point;
    }

    private void RemoveNode(FrameworkElement view)
    {
        var entry = _nodes.FirstOrDefault(n => ReferenceEquals(n.View, view));
        if (entry is null) return;

        _nodes.Remove(entry);
        _workspace.Nodes.Remove(entry.Model);
        _workspace.Connections.RemoveAll(c => c.SourceId == entry.Model.Id || c.TargetId == entry.Model.Id);
        World.Children.Remove(view);
        _autosave.Touch();
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
            _autosave.Touch();
        }

        handle.PointerReleased += EndDrag;
        handle.PointerCaptureLost += (s, e) =>
        {
            // PointerMoved has already written the new position into the model, so losing capture
            // to a focus steal still has to schedule the save that PointerReleased would have.
            if (!dragging) return;
            dragging = false;
            _autosave.Touch();
        };
    }

    private void OnNewTerminal(object sender, RoutedEventArgs e)
    {
        var (x, y) = NextSpawnPoint();
        Materialize(new TerminalNode
        {
            Title = "terminal",
            X = x, Y = y, Width = 720, Height = 420,
            CommandLine = "powershell.exe -NoLogo",
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        });
    }

    private void OnNewNote(object sender, RoutedEventArgs e)
    {
        var (x, y) = NextSpawnPoint();
        Materialize(new NoteNode { Title = "nota", X = x, Y = y, Width = 340, Height = 240 });
    }

    /// <summary>Builds the view for a model. The one place that knows model kind maps to view kind.</summary>
    private void Materialize(NodeBase model)
    {
        switch (model)
        {
            case TerminalNode terminalModel:
            {
                var view = new TerminalNodeView
                {
                    CommandLine = terminalModel.CommandLine,
                    StartDirectory = string.IsNullOrEmpty(terminalModel.WorkingDirectory) ? null : terminalModel.WorkingDirectory
                };
                view.CloseRequested += RemoveNode;
                AddNode(view, view, model);
                break;
            }
            case NoteNode:
            {
                var view = new NoteNodeView { Markdown = "# Nota\n\nTexto em markdown." };
                view.CloseRequested += RemoveNode;
                AddNode(view, view, model);
                break;
            }
            // LoadWorkspace clears the node list before materializing, so a kind that silently
            // fell through here would be dropped from memory and then erased from disk.
            default:
                throw new NotSupportedException($"No view for node kind {model.GetType().Name}.");
        }
    }
}
