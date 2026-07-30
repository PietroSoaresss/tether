using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Orchestration.Core.Models;
using Windows.Foundation;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace Orchestration.App;

public sealed partial class MainWindow
{
    private readonly Dictionary<Guid, XamlPath> _wirePaths = new();
    private CanvasNode? _wireSource;
    private XamlPath? _rubberWire;
    private Point _wireStart;
    private Guid? _selectedWire;
    private static readonly Windows.UI.Color WireViolet =
        Windows.UI.Color.FromArgb(255, 121, 98, 140);
    private static readonly Windows.UI.Color WireLime =
        Windows.UI.Color.FromArgb(255, 194, 239, 78);

    private void RegisterGraphNode(CanvasNode entry)
    {
        var surface = entry.Node.ConnectionSurface;
        surface.Visibility = ConnectionModeToggle.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        surface.PointerPressed += (_, e) =>
        {
            SelectNode(entry);
            _selectedWire = null;
            _wireSource = entry;
            _wireStart = e.GetCurrentPoint(Viewport).Position;
            _rubberWire = NewPath(WireLime);
            _rubberWire.StrokeThickness = 5;
            Wires.Children.Add(_rubberWire);
            surface.CapturePointer(e.Pointer);
            UpdateRubber(_wireStart);
            e.Handled = true;
        };
        surface.PointerMoved += (_, e) =>
        {
            if (_wireSource == entry) UpdateRubber(e.GetCurrentPoint(Viewport).Position);
        };
        surface.PointerReleased += (_, e) =>
        {
            if (_wireSource != entry) return;
            Point position = e.GetCurrentPoint(Viewport).Position;
            FinishWire(position);
            surface.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        };
        surface.PointerCaptureLost += (_, _) =>
        {
            if (_wireSource == entry) CancelWire();
        };

        RegisterResize(entry);
        entry.Node.DragHandle.DoubleTapped += async (_, e) =>
        {
            e.Handled = true;
            await EditNode(entry);
        };
    }

    private void FinishWire(Point position)
    {
        var source = _wireSource;
        Point start = _wireStart;
        CancelWire();
        if (source is null) return;

        var target = _nodes.FirstOrDefault(node =>
            node != source && IsInside(node.View, position));
        if (target is null) return;
        if (_workspace.Connections.Any(connection =>
                connection.SourceId == source.Model.Id && connection.TargetId == target.Model.Id))
            return;

        var sourceAnchor = RelativeAnchor(source, start);
        var targetAnchor = RelativeAnchor(target, position);
        _workspace.Connections.Add(new Connection
        {
            SourceId = source.Model.Id,
            TargetId = target.Model.Id,
            SourceAnchorX = sourceAnchor.X,
            SourceAnchorY = sourceAnchor.Y,
            TargetAnchorX = targetAnchor.X,
            TargetAnchorY = targetAnchor.Y
        });
        RenderWires();
        _autosave.Touch();
    }

    private bool IsInside(UIElement element, Point point)
    {
        if (element is not FrameworkElement framework) return false;
        Point topLeft = framework.TransformToVisual(Viewport).TransformPoint(new Point());
        return new Rect(topLeft, new Size(framework.ActualWidth, framework.ActualHeight)).Contains(point);
    }

    private void UpdateRubber(Point target)
    {
        if (_rubberWire is null || _wireSource is null) return;
        SetCurve(_rubberWire, _wireStart, target);
    }

    private void CancelWire()
    {
        _wireSource = null;
        if (_rubberWire is not null) Wires.Children.Remove(_rubberWire);
        _rubberWire = null;
    }

    private void OnConnectionModeChanged(object sender, RoutedEventArgs e)
    {
        bool enabled = ConnectionModeToggle.IsChecked == true;
        if (enabled)
        {
            ConnectionModeToggle.Background =
                (Brush)Application.Current.Resources["TetherAccentLimeBrush"];
            ConnectionModeToggle.Foreground =
                (Brush)Application.Current.Resources["TetherAccentOnLimeBrush"];
            ConnectionModeToggle.BorderBrush =
                (Brush)Application.Current.Resources["TetherAccentLimeBrush"];
        }
        else
        {
            ConnectionModeToggle.ClearValue(Control.BackgroundProperty);
            ConnectionModeToggle.ClearValue(Control.ForegroundProperty);
            ConnectionModeToggle.ClearValue(Control.BorderBrushProperty);
        }
        foreach (var node in _nodes)
            node.Node.ConnectionSurface.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!enabled) CancelWire();
    }

    private void RenderWires()
    {
        var live = _workspace.Connections.Select(connection => connection.Id).ToHashSet();
        foreach (var stale in _wirePaths.Keys.Where(id => !live.Contains(id)).ToList())
        {
            Wires.Children.Remove(_wirePaths[stale]);
            _wirePaths.Remove(stale);
        }

        foreach (var connection in _workspace.Connections)
        {
            var source = _nodes.FirstOrDefault(node => node.Model.Id == connection.SourceId);
            var target = _nodes.FirstOrDefault(node => node.Model.Id == connection.TargetId);
            if (source is null || target is null) continue;

            if (!_wirePaths.TryGetValue(connection.Id, out var path))
            {
                path = NewPath(WireViolet);
                path.Tag = connection.Id;
                path.PointerPressed += (_, e) =>
                {
                    SelectNode(null);
                    _selectedWire = (Guid)path.Tag;
                    RenderWires();
                    e.Handled = true;
                };
                path.PointerEntered += (_, _) =>
                {
                    path.Stroke = new SolidColorBrush(WireLime);
                    path.StrokeThickness = WireThickness(true);
                };
                path.PointerExited += (_, _) => RenderWires();
                path.RightTapped += (_, e) => ShowWireMenu(connection, path, e.GetPosition(path));
                _wirePaths[connection.Id] = path;
                Wires.Children.Add(path);
            }

            bool selected = _selectedWire == connection.Id;
            path.Stroke = new SolidColorBrush(selected ? WireLime : WireViolet);
            path.StrokeThickness = WireThickness(selected);
            SetCurve(
                path,
                AnchorPoint(source, connection.SourceAnchorX, connection.SourceAnchorY),
                AnchorPoint(target, connection.TargetAnchorX, connection.TargetAnchorY));
        }
    }

    private void ShowWireMenu(Connection connection, FrameworkElement target, Point point)
    {
        var bidirectional = new ToggleMenuFlyoutItem
        {
            Text = "Bidirecional",
            IsChecked = connection.Bidirectional
        };
        bidirectional.Click += (_, _) =>
        {
            connection.Bidirectional = bidirectional.IsChecked;
            _autosave.Touch();
        };
        var delete = new MenuFlyoutItem { Text = "Excluir" };
        delete.Click += (_, _) => DeleteWire(connection.Id);
        var menu = new MenuFlyout();
        menu.Items.Add(bidirectional);
        menu.Items.Add(delete);
        menu.ShowAt(target, point);
    }

    private void DeleteWire(Guid id)
    {
        _workspace.Connections.RemoveAll(connection => connection.Id == id);
        if (_selectedWire == id) _selectedWire = null;
        RenderWires();
        _autosave.Touch();
    }

    private void RegisterResize(CanvasNode entry)
    {
        Point last = default;
        bool resizing = false;
        entry.Node.ResizeGrip.PointerPressed += (sender, e) =>
        {
            SelectNode(entry);
            _selectedWire = null;
            RenderWires();
            last = e.GetCurrentPoint(Viewport).Position;
            resizing = ((UIElement)sender).CapturePointer(e.Pointer);
            e.Handled = true;
        };
        entry.Node.ResizeGrip.PointerMoved += (_, e) =>
        {
            if (!resizing) return;
            Point now = e.GetCurrentPoint(Viewport).Position;
            double minWidth = entry.Model is TerminalNode ? 240 : 160;
            double minHeight = entry.Model is TerminalNode ? 160 : 100;
            entry.Width = Math.Max(minWidth, entry.Width + (now.X - last.X) / _zoom);
            entry.Height = Math.Max(minHeight, entry.Height + (now.Y - last.Y) / _zoom);
            last = now;
            PlaceNode(entry);
        };
        entry.Node.ResizeGrip.PointerReleased += (sender, e) =>
        {
            if (!resizing) return;
            resizing = false;
            ((UIElement)sender).ReleasePointerCapture(e.Pointer);
            _autosave.Touch();
        };
    }

    private async Task EditNode(CanvasNode entry)
    {
        var title = new TextBox { Header = "Titulo", Text = entry.Model.Title };
        var details = new TextBox();
        var folder = new TextBox();
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(title);

        if (entry.Model is TerminalNode terminal)
        {
            details.Header = "Comando";
            details.Text = terminal.CommandLine;
            folder.Header = "Pasta de trabalho";
            folder.Text = terminal.WorkingDirectory;
            panel.Children.Add(details);
            panel.Children.Add(folder);
        }
        else if (entry.Model is NoteNode note)
        {
            details.Header = "Arquivo";
            details.Text = note.FileName;
            panel.Children.Add(details);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Editar no",
            Content = panel,
            PrimaryButtonText = "Salvar",
            CloseButtonText = "Cancelar"
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        entry.Model.Title = title.Text.Trim();
        if (entry.Model is TerminalNode terminalModel)
        {
            terminalModel.CommandLine = details.Text;
            terminalModel.WorkingDirectory = folder.Text;
            ((Views.TerminalNodeView)entry.Node).Title = entry.Model.Title;
        }
        else if (entry.Model is NoteNode noteModel && details.Text != noteModel.FileName)
        {
            try
            {
                string? folderPath = NoteFolder(noteModel);
                _noteFiles.Resolve(details.Text, folderPath);
                if (!_noteFiles.Exists(details.Text, folderPath))
                    _noteFiles.Write(
                        details.Text,
                        ((Views.NoteNodeView)entry.Node).Markdown,
                        folderPath);
                noteModel.FileName = details.Text;
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
            {
                ShowRecoveryNotice(e.Message);
            }
        }

        if (entry.Node is Views.NoteNodeView noteView) noteView.Title = entry.Model.Title;
        _autosave.Touch();
    }

    private (double X, double Y) RelativeAnchor(CanvasNode node, Point point)
    {
        Point topLeft = node.View.TransformToVisual(Viewport).TransformPoint(new Point());
        return (
            Math.Clamp((point.X - topLeft.X) / Math.Max(1, node.View.ActualWidth), 0, 1),
            Math.Clamp((point.Y - topLeft.Y) / Math.Max(1, node.View.ActualHeight), 0, 1));
    }

    private Point AnchorPoint(CanvasNode node, double x, double y) => new(
        node.X * _zoom + _offsetX + Math.Clamp(x, 0, 1) * node.Width * _zoom,
        node.Y * _zoom + _offsetY + Math.Clamp(y, 0, 1) * node.Height * _zoom);

    /// <summary>
    /// Cables scale with the canvas, or zooming out leaves 4 px ribbons dominating nodes that have
    /// shrunk past them. The floor keeps a wire clickable at the far end of the range.
    /// </summary>
    private double WireThickness(bool emphasised) => Math.Max(1.5, (emphasised ? 5 : 4) * _zoom);

    private static XamlPath NewPath(Windows.UI.Color color) => new()
    {
        Stroke = new SolidColorBrush(color),
        StrokeThickness = 4,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round
    };

    private static void SetCurve(XamlPath path, Point from, Point to)
    {
        double bend = Math.Max(60, Math.Abs(to.X - from.X) * .45);
        double direction = to.X >= from.X ? 1 : -1;
        path.Data = new PathGeometry
        {
            Figures =
            {
                new PathFigure
                {
                    StartPoint = from,
                    Segments =
                    {
                        new BezierSegment
                        {
                            Point1 = new Point(from.X + bend * direction, from.Y),
                            Point2 = new Point(to.X - bend * direction, to.Y),
                            Point3 = to
                        }
                    }
                }
            }
        };
    }
}
