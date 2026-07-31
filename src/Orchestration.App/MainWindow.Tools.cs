using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Orchestration.App.Views;
using Orchestration.Core.Models;
using Windows.Foundation;
using Windows.System;

namespace Orchestration.App;

public sealed partial class MainWindow
{
    private readonly Dictionary<Guid, FrameworkElement> _annotationViews = new();
    private string _canvasTool = "select";
    private string _canvasColor = "#F5F3F7";
    private double _drawSize = 3;
    private double _textSize = 18;
    private CanvasItem? _activeStroke;
    private Polyline? _activeStrokeView;

    private void OnCanvasToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tool }) return;
        SetCanvasTool(tool);
    }

    private void SetCanvasTool(string tool)
    {
        _canvasTool = tool;
        SelectCanvasTool.IsChecked = tool == "select";
        TextCanvasTool.IsChecked = tool == "text";
        DrawCanvasTool.IsChecked = tool == "draw";
        EraseCanvasTool.IsChecked = tool == "erase";
        UpdateCanvasToolContext();
    }

    private bool HandleCanvasToolShortcut(VirtualKey key)
    {
        string? tool = key switch
        {
            VirtualKey.V => "select",
            VirtualKey.T => "text",
            VirtualKey.P => "draw",
            VirtualKey.E => "erase",
            _ => null
        };
        if (tool is null) return false;
        SetCanvasTool(tool);
        return true;
    }

    private void UpdateCanvasToolContext()
    {
        bool terminal = _selectedNode?.Model is TerminalNode;
        CanvasContextLabel.Text = terminal
            ? "Terminal"
            : _canvasTool switch
            {
                "text" => "Texto",
                "draw" => "Traço",
                "erase" => "Apagar",
                _ => "Cor"
            };

        bool hasSize = !terminal && _canvasTool is "text" or "draw";
        CanvasSizeDivider.Visibility = hasSize ? Visibility.Visible : Visibility.Collapsed;
        CanvasSizeDown.Visibility = hasSize ? Visibility.Visible : Visibility.Collapsed;
        CanvasSizeLabel.Visibility = hasSize ? Visibility.Visible : Visibility.Collapsed;
        CanvasSizeUp.Visibility = hasSize ? Visibility.Visible : Visibility.Collapsed;
        CanvasSizeLabel.Text = _canvasTool == "text"
            ? $"{_textSize:0}"
            : $"{_drawSize:0}";
    }

    private void OnCanvasColor(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string color }) return;
        _canvasColor = color;
        if (_selectedNode is { Model: TerminalNode terminal, Node: TerminalNodeView view })
        {
            terminal.AccentColor = color;
            view.ApplyAccent(color);
            _autosave.Touch();
        }
    }

    private void OnCanvasSize(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string deltaText } ||
            !double.TryParse(deltaText, CultureInfo.InvariantCulture, out double delta))
            return;

        if (_canvasTool == "text")
            _textSize = Math.Clamp(_textSize + delta * 2, 10, 48);
        else if (_canvasTool == "draw")
            _drawSize = Math.Clamp(_drawSize + delta, 1, 12);
        UpdateCanvasToolContext();
    }

    private bool TryStartCanvasTool(PointerRoutedEventArgs e)
    {
        if (_canvasTool == "select") return false;
        Point screen = e.GetCurrentPoint(Viewport).Position;

        if (_canvasTool == "text")
        {
            AddCanvasText(screen);
            e.Handled = true;
            return true;
        }

        if (_canvasTool == "draw")
        {
            var world = ScreenToWorld(screen);
            _activeStroke = new CanvasItem
            {
                Kind = CanvasItemKind.Stroke,
                Color = _canvasColor,
                Size = _drawSize,
                Points = { new CanvasPoint { X = world.X, Y = world.Y } }
            };
            _workspace.CanvasItems.Add(_activeStroke);
            _activeStrokeView = (Polyline)EnsureAnnotationView(_activeStroke);
            PositionAnnotation(_activeStroke, _activeStrokeView);
            Viewport.CapturePointer(e.Pointer);
            e.Handled = true;
            return true;
        }

        return _canvasTool == "erase";
    }

    private void OnCanvasDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_canvasTool != "select" ||
            !ReferenceEquals(e.OriginalSource, CanvasBackground)) return;
        AddCanvasText(e.GetPosition(Viewport));
        e.Handled = true;
    }

    private void AddCanvasText(Point screen)
    {
        var world = ScreenToWorld(screen);
        var item = new CanvasItem
        {
            Kind = CanvasItemKind.Text,
            X = world.X,
            Y = world.Y,
            Text = "Texto",
            Color = _canvasColor,
            Size = _textSize
        };
        _workspace.CanvasItems.Add(item);
        var view = (Grid)EnsureAnnotationView(item);
        PositionAnnotation(item, view);
        EditCanvasText(view);
        _autosave.Touch();
    }

    private bool TryMoveCanvasTool(PointerRoutedEventArgs e)
    {
        if (_activeStroke is null || _activeStrokeView is null) return false;
        Point screen = e.GetCurrentPoint(Viewport).Position;
        var world = ScreenToWorld(screen);
        CanvasPoint last = _activeStroke.Points[^1];
        double dx = (world.X - last.X) * _zoom;
        double dy = (world.Y - last.Y) * _zoom;
        if (Math.Sqrt(dx * dx + dy * dy) >= 2)
        {
            _activeStroke.Points.Add(new CanvasPoint { X = world.X, Y = world.Y });
            _activeStrokeView.Points.Add(screen);
        }
        e.Handled = true;
        return true;
    }

    private bool TryEndCanvasTool(PointerRoutedEventArgs e)
    {
        if (_activeStroke is null) return false;
        _activeStroke = null;
        _activeStrokeView = null;
        Viewport.ReleasePointerCapture(e.Pointer);
        _autosave.Touch();
        e.Handled = true;
        return true;
    }

    private (double X, double Y) ScreenToWorld(Point point) =>
        ((point.X - _offsetX) / _zoom, (point.Y - _offsetY) / _zoom);

    private void RenderAnnotations()
    {
        var live = _workspace.CanvasItems.Select(item => item.Id).ToHashSet();
        foreach (Guid stale in _annotationViews.Keys.Where(id => !live.Contains(id)).ToList())
        {
            Annotations.Children.Remove(_annotationViews[stale]);
            _annotationViews.Remove(stale);
        }

        foreach (CanvasItem item in _workspace.CanvasItems)
            PositionAnnotation(item, EnsureAnnotationView(item));
    }

    private FrameworkElement EnsureAnnotationView(CanvasItem item)
    {
        if (_annotationViews.TryGetValue(item.Id, out var existing)) return existing;

        FrameworkElement view;
        if (item.Kind == CanvasItemKind.Stroke)
        {
            var line = new Polyline
            {
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            };
            line.PointerPressed += (_, e) => TryEraseAnnotation(item, e);
            view = line;
        }
        else
        {
            var label = new TextBlock
            {
                Text = item.Text,
                MaxWidth = 480,
                TextWrapping = TextWrapping.Wrap
            };
            var editor = new TextBox
            {
                Text = item.Text,
                MinWidth = 80,
                MaxWidth = 480,
                Padding = new Thickness(0),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Visibility = Visibility.Collapsed
            };
            var text = new Grid();
            text.Children.Add(label);
            text.Children.Add(editor);
            label.DoubleTapped += (_, e) =>
            {
                if (_canvasTool == "erase") return;
                EditCanvasText(text);
                e.Handled = true;
            };
            editor.TextChanged += (_, _) =>
            {
                item.Text = editor.Text;
                label.Text = editor.Text;
                _autosave.Touch();
            };
            editor.LostFocus += (_, _) =>
            {
                editor.Visibility = Visibility.Collapsed;
                label.Visibility = Visibility.Visible;
            };
            text.PointerPressed += (_, e) => TryEraseAnnotation(item, e);
            view = text;
        }

        _annotationViews[item.Id] = view;
        Annotations.Children.Add(view);
        return view;
    }

    private void PositionAnnotation(CanvasItem item, FrameworkElement view)
    {
        var color = new SolidColorBrush(ParseCanvasColor(item.Color));
        if (view is Polyline line)
        {
            line.Stroke = color;
            line.StrokeThickness = Math.Max(1, item.Size * _zoom);
            line.Points.Clear();
            foreach (CanvasPoint point in item.Points)
                line.Points.Add(new Point(point.X * _zoom + _offsetX, point.Y * _zoom + _offsetY));
            return;
        }

        var text = (Grid)view;
        var label = (TextBlock)text.Children[0];
        var editor = (TextBox)text.Children[1];
        if (label.Text != item.Text) label.Text = item.Text;
        if (editor.Text != item.Text) editor.Text = item.Text;
        label.Foreground = editor.Foreground = color;
        label.FontSize = editor.FontSize = Math.Clamp(item.Size * _zoom, 10, 72);
        Canvas.SetLeft(text, item.X * _zoom + _offsetX);
        Canvas.SetTop(text, item.Y * _zoom + _offsetY);
    }

    private static void EditCanvasText(Grid text)
    {
        var label = (TextBlock)text.Children[0];
        var editor = (TextBox)text.Children[1];
        label.Visibility = Visibility.Collapsed;
        editor.Visibility = Visibility.Visible;
        editor.Focus(FocusState.Programmatic);
        editor.SelectAll();
    }

    private void TryEraseAnnotation(CanvasItem item, PointerRoutedEventArgs e)
    {
        if (_canvasTool != "erase" ||
            !e.GetCurrentPoint(Viewport).Properties.IsLeftButtonPressed) return;
        _workspace.CanvasItems.Remove(item);
        if (_annotationViews.Remove(item.Id, out var view))
            Annotations.Children.Remove(view);
        _autosave.Touch();
        e.Handled = true;
    }

    private static Windows.UI.Color ParseCanvasColor(string? color)
    {
        if (color?.Length == 7 &&
            uint.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
            return Windows.UI.Color.FromArgb(
                255,
                (byte)(rgb >> 16),
                (byte)(rgb >> 8),
                (byte)rgb);
        return Windows.UI.Color.FromArgb(255, 245, 243, 247);
    }
}
