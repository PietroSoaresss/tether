using System.Globalization;
using Microsoft.UI.Input;
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
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace Orchestration.App;

public sealed partial class MainWindow
{
    /// <summary>Below this much pointer travel a gesture counts as a click, not a drag.</summary>
    private const double DragThreshold = 12;

    private static readonly string[] ShapeTools = { "arrow", "rect", "ellipse", "diamond" };
    private static readonly string[] PlacementTools = { "place-terminal", "place-note", "place-browser" };

    private readonly Dictionary<Guid, FrameworkElement> _annotationViews = new();
    private string _canvasTool = "select";
    private string _canvasColor = "#F5F3F7";
    private double _drawSize = 3;

    /// <summary>
    /// Style the next text will be created with. It is a CanvasItem rather than one field per
    /// property so that "apply to the selection" and "apply to the next one" are the same code.
    /// </summary>
    private readonly CanvasItem _textDefaults = new() { Kind = CanvasItemKind.Text, Size = 18 };
    private CanvasItem? _activeStroke;
    private Polyline? _activeStrokeView;
    private CanvasItem? _selectedItem;

    private Rectangle? _rubberBand;
    private Point _rubberStart;
    private bool _rubberActive;

    private void OnCanvasToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tool }) return;
        SetCanvasTool(tool);
    }

    /// <summary>Arms a node-placement gesture: the next click or drag on the canvas creates it.</summary>
    private void ArmPlacement(string tool)
    {
        if (ConnectionModeToggle.IsChecked == true) ConnectionModeToggle.IsChecked = false;
        SetCanvasTool(tool);
    }

    private void SetCanvasTool(string tool)
    {
        _canvasTool = tool;
        // Walking the ruler beats a line per button: the ruler grew from four tools to nine.
        foreach (var button in CanvasToolRuler.Children.OfType<ToggleButton>())
            button.IsChecked = (string?)button.Tag == tool;

        if (tool != "select") SelectAnnotation(null);
        Viewport.SetCursor(tool switch
        {
            "select" => InputSystemCursorShape.Arrow,
            "erase" => InputSystemCursorShape.Cross,
            _ => InputSystemCursorShape.Cross
        });
        UpdateCanvasToolContext();
        RenderAnnotations();
    }

    private bool HandleCanvasToolShortcut(VirtualKey key)
    {
        string? tool = key switch
        {
            VirtualKey.V => "select",
            VirtualKey.T => "text",
            VirtualKey.P => "draw",
            VirtualKey.E => "erase",
            VirtualKey.A => "arrow",
            VirtualKey.R => "rect",
            VirtualKey.O => "ellipse",
            VirtualKey.D => "diamond",
            VirtualKey.Escape => "select",
            _ => null
        };
        if (tool is null) return false;
        if (key == VirtualKey.Escape) CancelRubber();
        SetCanvasTool(tool);
        return true;
    }

    private void UpdateCanvasToolContext()
    {
        bool terminal = _selectedNode?.Model is TerminalNode;
        CanvasContextLabel.Text = terminal
            ? "Terminal"
            : _selectedItem is not null
                ? "Selecao"
                : _canvasTool switch
                {
                    "text" => "Texto",
                    "draw" => "Traco",
                    "erase" => "Apagar",
                    "arrow" => "Seta",
                    "rect" => "Retangulo",
                    "ellipse" => "Elipse",
                    "diamond" => "Losango",
                    "place-terminal" => "Terminal: clique ou arraste",
                    "place-note" => "Nota: clique ou arraste",
                    "place-browser" => "Navegador: clique ou arraste",
                    _ => "Cor"
                };

        // Size applies to a selected item, or to whatever the next stroke/text/shape will be.
        bool hasSize = !terminal &&
                       (_selectedItem is not null ||
                        _canvasTool is "text" or "draw" || ShapeTools.Contains(_canvasTool));
        CanvasSizeDivider.Visibility = hasSize ? Visibility.Visible : Visibility.Collapsed;
        CanvasSizeDown.Visibility = hasSize ? Visibility.Visible : Visibility.Collapsed;
        CanvasSizeLabel.Visibility = hasSize ? Visibility.Visible : Visibility.Collapsed;
        CanvasSizeUp.Visibility = hasSize ? Visibility.Visible : Visibility.Collapsed;
        CanvasSizeLabel.Text = $"{CurrentSize():0}";

        // The text group follows the same rule: edit the selected text, or arm the next one.
        var style = TextStyleSource();
        CanvasTextGroup.Visibility = style is null ? Visibility.Collapsed : Visibility.Visible;
        if (style is null) return;

        TextFontUi.IsChecked = style.Font == "ui";
        TextFontSerif.IsChecked = style.Font == "serif";
        TextFontMono.IsChecked = style.Font == "mono";
        TextBoldToggle.IsChecked = style.Bold;
        TextItalicToggle.IsChecked = style.Italic;
        TextAlignLeft.IsChecked = style.Align == "left";
        TextAlignCenter.IsChecked = style.Align == "center";
        TextAlignRight.IsChecked = style.Align == "right";
    }

    /// <summary>The item text styling applies to: the selected text, else the armed defaults.</summary>
    private CanvasItem? TextStyleSource() =>
        _selectedItem is { Kind: CanvasItemKind.Text } selected ? selected
        : _selectedItem is null && _canvasTool == "text" ? _textDefaults
        : null;

    private double CurrentSize() =>
        _selectedItem?.Size ?? (_canvasTool == "text" ? _textDefaults.Size : _drawSize);

    private void OnCanvasTextFont(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string font }) StyleText(item => item.Font = font);
    }

    private void OnCanvasTextBold(object sender, RoutedEventArgs e) =>
        StyleText(item => item.Bold = !item.Bold);

    private void OnCanvasTextItalic(object sender, RoutedEventArgs e) =>
        StyleText(item => item.Italic = !item.Italic);

    private void OnCanvasTextAlign(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string align }) StyleText(item => item.Align = align);
    }

    /// <summary>Applies a style change to the selected text, or to whatever the next one will be.</summary>
    private void StyleText(Action<CanvasItem> apply)
    {
        if (TextStyleSource() is not { } target) return;

        apply(target);
        if (!ReferenceEquals(target, _textDefaults))
        {
            PositionAnnotation(target, _annotationViews[target.Id]);
            _autosave.Touch();
        }
        UpdateCanvasToolContext();
    }

    private void OnCanvasColor(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string color }) return;
        _canvasColor = color;

        if (_selectedItem is not null)
        {
            _selectedItem.Color = color;
            PositionAnnotation(_selectedItem, _annotationViews[_selectedItem.Id]);
            _autosave.Touch();
            return;
        }

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

        if (_selectedItem is { } item)
        {
            item.Size = item.Kind == CanvasItemKind.Text
                ? Math.Clamp(item.Size + delta * 2, 10, 96)
                : Math.Clamp(item.Size + delta, 1, 12);
            PositionAnnotation(item, _annotationViews[item.Id]);
            _autosave.Touch();
        }
        else if (_canvasTool == "text")
            _textDefaults.Size = Math.Clamp(_textDefaults.Size + delta * 2, 10, 96);
        else
            _drawSize = Math.Clamp(_drawSize + delta, 1, 12);

        UpdateCanvasToolContext();
    }

    // ---- gestures ---------------------------------------------------------

    private bool TryStartCanvasTool(PointerRoutedEventArgs e)
    {
        if (_canvasTool == "select")
        {
            SelectAnnotation(null);
            return false;
        }

        Point screen = e.GetCurrentPoint(Viewport).Position;

        if (_canvasTool == "text")
        {
            var world = ScreenToWorld(screen);
            var item = new CanvasItem
            {
                Kind = CanvasItemKind.Text,
                X = world.X,
                Y = world.Y,
                Color = _canvasColor,
                Size = _textDefaults.Size,
                Font = _textDefaults.Font,
                Bold = _textDefaults.Bold,
                Italic = _textDefaults.Italic,
                Align = _textDefaults.Align
            };
            _canvas.CanvasItems.Add(item);
            PositionAnnotation(item, EnsureAnnotationView(item));
            // Born empty and straight into the caret. EndTextEdit drops it again if nothing is
            // typed, so a stray click cannot leave an invisible item on the canvas.
            BeginTextEdit(item);
            SetCanvasTool("select");
            SelectAnnotation(item);
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
            _canvas.CanvasItems.Add(_activeStroke);
            _activeStrokeView = (Polyline)EnsureAnnotationView(_activeStroke);
            PositionAnnotation(_activeStroke, _activeStrokeView);
            Viewport.CapturePointer(e.Pointer);
            e.Handled = true;
            return true;
        }

        if (ShapeTools.Contains(_canvasTool) || PlacementTools.Contains(_canvasTool))
        {
            StartRubber(screen);
            Viewport.CapturePointer(e.Pointer);
            e.Handled = true;
            return true;
        }

        return _canvasTool == "erase";
    }

    private bool TryMoveCanvasTool(PointerRoutedEventArgs e)
    {
        Point screen = e.GetCurrentPoint(Viewport).Position;

        if (_rubberActive)
        {
            UpdateRubber(screen);
            e.Handled = true;
            return true;
        }

        if (_activeStroke is null || _activeStrokeView is null) return false;
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
        if (_rubberActive)
        {
            FinishRubber(e.GetCurrentPoint(Viewport).Position);
            Viewport.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            return true;
        }

        if (_activeStroke is null) return false;
        _activeStroke = null;
        _activeStrokeView = null;
        Viewport.ReleasePointerCapture(e.Pointer);
        _autosave.Touch();
        e.Handled = true;
        return true;
    }

    // ---- rubber band ------------------------------------------------------

    private void StartRubber(Point screen)
    {
        _rubberStart = screen;
        _rubberActive = true;
        _rubberBand = new Rectangle
        {
            Stroke = new SolidColorBrush(ParseCanvasColor(_canvasColor)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            IsHitTestVisible = false
        };
        Canvas.SetLeft(_rubberBand, screen.X);
        Canvas.SetTop(_rubberBand, screen.Y);
        Annotations.Children.Add(_rubberBand);
    }

    private void UpdateRubber(Point screen)
    {
        if (_rubberBand is null) return;
        Canvas.SetLeft(_rubberBand, Math.Min(_rubberStart.X, screen.X));
        Canvas.SetTop(_rubberBand, Math.Min(_rubberStart.Y, screen.Y));
        _rubberBand.Width = Math.Abs(screen.X - _rubberStart.X);
        _rubberBand.Height = Math.Abs(screen.Y - _rubberStart.Y);
    }

    private void CancelRubber()
    {
        if (_rubberBand is not null) Annotations.Children.Remove(_rubberBand);
        _rubberBand = null;
        _rubberActive = false;
    }

    private void FinishRubber(Point screen)
    {
        string tool = _canvasTool;
        bool dragged = Math.Abs(screen.X - _rubberStart.X) >= DragThreshold ||
                       Math.Abs(screen.Y - _rubberStart.Y) >= DragThreshold;
        var start = ScreenToWorld(_rubberStart);
        var end = ScreenToWorld(screen);
        CancelRubber();

        if (PlacementTools.Contains(tool))
        {
            double x = Math.Min(start.X, end.X);
            double y = Math.Min(start.Y, end.Y);
            double width = Math.Abs(end.X - start.X);
            double height = Math.Abs(end.Y - start.Y);

            if (tool == "place-terminal")
            {
                if (dragged) CreateTerminal(x, y, Math.Max(width, 240), Math.Max(height, 160));
                else CreateTerminal(start.X, start.Y);
            }
            else if (tool == "place-browser")
            {
                if (dragged) CreateBrowser(x, y, Math.Max(width, 240), Math.Max(height, 160));
                else CreateBrowser(start.X, start.Y);
            }
            else
            {
                if (dragged) CreateNote(x, y, Math.Max(width, 160), Math.Max(height, 100));
                else CreateNote(start.X, start.Y);
            }
            SetCanvasTool("select");
            return;
        }

        // A shape needs two distinct corners; a stray click would persist an invisible item.
        if (!dragged) return;

        var item = new CanvasItem
        {
            Kind = tool switch
            {
                "arrow" => CanvasItemKind.Arrow,
                "rect" => CanvasItemKind.Rectangle,
                "ellipse" => CanvasItemKind.Ellipse,
                _ => CanvasItemKind.Diamond
            },
            Color = _canvasColor,
            Size = _drawSize,
            Points =
            {
                // An arrow keeps its direction; a box is normalised to top-left / bottom-right.
                new CanvasPoint { X = start.X, Y = start.Y },
                new CanvasPoint { X = end.X, Y = end.Y }
            }
        };
        _canvas.CanvasItems.Add(item);
        PositionAnnotation(item, EnsureAnnotationView(item));
        _autosave.Touch();
        SetCanvasTool("select");
    }

    private (double X, double Y) ScreenToWorld(Point point) =>
        ((point.X - _offsetX) / _zoom, (point.Y - _offsetY) / _zoom);

    private Point WorldToScreen(CanvasPoint point) =>
        new(point.X * _zoom + _offsetX, point.Y * _zoom + _offsetY);

    // ---- rendering --------------------------------------------------------

    private void RenderAnnotations()
    {
        var live = _canvas.CanvasItems.Select(item => item.Id).ToHashSet();
        foreach (Guid stale in _annotationViews.Keys.Where(id => !live.Contains(id)).ToList())
        {
            Annotations.Children.Remove(_annotationViews[stale]);
            _annotationViews.Remove(stale);
        }

        foreach (CanvasItem item in _canvas.CanvasItems)
            PositionAnnotation(item, EnsureAnnotationView(item));
    }

    private FrameworkElement EnsureAnnotationView(CanvasItem item)
    {
        if (_annotationViews.TryGetValue(item.Id, out var existing)) return existing;

        FrameworkElement view = item.Kind switch
        {
            CanvasItemKind.Stroke => new Polyline
            {
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            },
            // Text is a rendered label until someone double-taps it. A permanently live TextBox
            // eats the pointer for its own caret and selection, which is why text was the one
            // annotation that could not be dragged.
            CanvasItemKind.Text => new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 480 },
            CanvasItemKind.Rectangle => new Rectangle { Fill = TransparentFill() },
            CanvasItemKind.Ellipse => new Ellipse { Fill = TransparentFill() },
            CanvasItemKind.Diamond => new Polygon { Fill = TransparentFill() },
            _ => new XamlPath
            {
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            }
        };

        HookAnnotation(item, view);
        _annotationViews[item.Id] = view;
        Annotations.Children.Add(view);
        return view;
    }

    private void HookAnnotation(CanvasItem item, FrameworkElement view)
    {
        view.PointerPressed += (_, e) => OnAnnotationPressed(item, e);
        if (item.Kind != CanvasItemKind.Text) return;

        view.DoubleTapped += (_, e) =>
        {
            if (_canvasTool != "select") return;
            BeginTextEdit(item);
            e.Handled = true;
        };
    }

    /// <summary>Swaps the rendered label for a live editor, in place.</summary>
    private void BeginTextEdit(CanvasItem item)
    {
        if (_annotationViews[item.Id] is TextBox already)
        {
            already.Focus(FocusState.Programmatic);
            return;
        }

        var box = new TextBox
        {
            Text = item.Text,
            MinWidth = 100,
            MaxWidth = 480,
            Padding = new Thickness(2),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        box.TextChanged += (_, _) =>
        {
            item.Text = box.Text;
            _autosave.Touch();
        };
        box.LostFocus += (_, _) => EndTextEdit(item);

        SwapAnnotationView(item, box);
        box.Focus(FocusState.Programmatic);
        box.SelectAll();
    }

    private void EndTextEdit(CanvasItem item)
    {
        if (_annotationViews.GetValueOrDefault(item.Id) is not TextBox) return;

        // Nothing typed: the item would render as an invisible zero-size label nobody can select.
        if (string.IsNullOrWhiteSpace(item.Text))
        {
            _canvas.CanvasItems.Remove(item);
            if (_annotationViews.Remove(item.Id, out var stale)) Annotations.Children.Remove(stale);
            if (_selectedItem?.Id == item.Id) SelectAnnotation(null);
            _autosave.Touch();
            return;
        }

        SwapAnnotationView(item, new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 480 });
        _autosave.Touch();
    }

    private void SwapAnnotationView(CanvasItem item, FrameworkElement replacement)
    {
        var previous = _annotationViews[item.Id];
        // Keep the z-order: annotations are drawn in list order, so appending would lift the text
        // above everything drawn after it just because it was edited.
        int index = Annotations.Children.IndexOf(previous);
        Annotations.Children.RemoveAt(index);
        Annotations.Children.Insert(index, replacement);
        _annotationViews[item.Id] = replacement;
        HookAnnotation(item, replacement);
        PositionAnnotation(item, replacement);
    }

    /// <summary>Shapes are outlines, but a hollow shape can only be grabbed by its 1 px edge.</summary>
    private static Brush TransparentFill() =>
        new SolidColorBrush(Windows.UI.Color.FromArgb(1, 0, 0, 0));

    private static FontFamily TextFontFamily(string? font) => new(font switch
    {
        "mono" => "Cascadia Mono, Consolas",
        "serif" => "Georgia, Times New Roman",
        _ => "Segoe UI Variable Text"
    });

    private void PositionAnnotation(CanvasItem item, FrameworkElement view)
    {
        var brush = new SolidColorBrush(ParseCanvasColor(item.Color));
        bool selected = _selectedItem?.Id == item.Id;
        double thickness = Math.Max(1, item.Size * _zoom);
        // Only select and erase should reach an annotation; a shape tool needs the bare canvas
        // underneath so a drag that starts over an existing shape still draws.
        view.IsHitTestVisible = _canvasTool is "select" or "erase";

        switch (item.Kind)
        {
            case CanvasItemKind.Text:
            {
                // Not the node rule: a label owes nothing to a column count, so it floors instead
                // of vanishing. See Camera.LabelSize.
                double size = Camera.LabelSize(item.Size, _zoom);
                var weight = item.Bold
                    ? Microsoft.UI.Text.FontWeights.Bold
                    : Microsoft.UI.Text.FontWeights.Normal;
                var slant = item.Italic
                    ? Windows.UI.Text.FontStyle.Italic
                    : Windows.UI.Text.FontStyle.Normal;
                var family = TextFontFamily(item.Font);
                var alignment = item.Align switch
                {
                    "center" => TextAlignment.Center,
                    "right" => TextAlignment.Right,
                    _ => TextAlignment.Left
                };

                if (view is TextBox box)
                {
                    if (box.Text != item.Text) box.Text = item.Text;
                    box.Foreground = brush;
                    box.FontSize = size;
                    box.FontWeight = weight;
                    box.FontStyle = slant;
                    box.FontFamily = family;
                    box.TextAlignment = alignment;
                    box.BorderBrush = SelectionBrush();
                    box.BorderThickness = new Thickness(1);
                }
                else
                {
                    var label = (TextBlock)view;
                    label.Text = item.Text;
                    // A TextBlock has no border to light up, so selection recolours it — the same
                    // way a selected stroke or shape already reads.
                    label.Foreground = selected ? SelectionBrush() : brush;
                    label.FontSize = size;
                    label.FontWeight = weight;
                    label.FontStyle = slant;
                    label.FontFamily = family;
                    label.TextAlignment = alignment;
                }

                Canvas.SetLeft(view, item.X * _zoom + _offsetX);
                Canvas.SetTop(view, item.Y * _zoom + _offsetY);
                return;
            }

            case CanvasItemKind.Stroke:
            {
                var line = (Polyline)view;
                line.Stroke = selected ? SelectionBrush() : brush;
                line.StrokeThickness = thickness;
                line.Points.Clear();
                foreach (CanvasPoint point in item.Points) line.Points.Add(WorldToScreen(point));
                return;
            }
        }

        if (item.Points.Count < 2) return;
        Point a = WorldToScreen(item.Points[0]);
        Point b = WorldToScreen(item.Points[^1]);
        var shapeBrush = selected ? SelectionBrush() : brush;

        switch (item.Kind)
        {
            case CanvasItemKind.Arrow:
            {
                var path = (XamlPath)view;
                path.Stroke = shapeBrush;
                path.StrokeThickness = thickness;
                path.Data = ArrowGeometry(a, b, thickness);
                return;
            }

            case CanvasItemKind.Diamond:
            {
                var polygon = (Polygon)view;
                polygon.Stroke = shapeBrush;
                polygon.StrokeThickness = thickness;
                double midX = (a.X + b.X) / 2, midY = (a.Y + b.Y) / 2;
                polygon.Points.Clear();
                polygon.Points.Add(new Point(midX, Math.Min(a.Y, b.Y)));
                polygon.Points.Add(new Point(Math.Max(a.X, b.X), midY));
                polygon.Points.Add(new Point(midX, Math.Max(a.Y, b.Y)));
                polygon.Points.Add(new Point(Math.Min(a.X, b.X), midY));
                return;
            }

            default:
            {
                var shape = (Shape)view;
                shape.Stroke = shapeBrush;
                shape.StrokeThickness = thickness;
                shape.Width = Math.Abs(b.X - a.X);
                shape.Height = Math.Abs(b.Y - a.Y);
                Canvas.SetLeft(shape, Math.Min(a.X, b.X));
                Canvas.SetTop(shape, Math.Min(a.Y, b.Y));
                return;
            }
        }
    }

    private static Brush SelectionBrush() =>
        (Brush)Application.Current.Resources["TetherAccentLimeBrush"];

    private static Geometry ArrowGeometry(Point from, Point to, double thickness)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        var geometry = new PathGeometry();

        var shaft = new PathFigure { StartPoint = from, IsClosed = false };
        shaft.Segments.Add(new LineSegment { Point = to });
        geometry.Figures.Add(shaft);

        // Too short to carry a head without the head swallowing the whole arrow.
        if (length < 1) return geometry;

        double head = Math.Clamp(length * 0.25, thickness * 2.5, 26);
        double ux = dx / length, uy = dy / length;
        var basePoint = new Point(to.X - ux * head, to.Y - uy * head);
        double half = head * 0.45;

        var tip = new PathFigure
        {
            StartPoint = new Point(basePoint.X - uy * half, basePoint.Y + ux * half),
            IsClosed = true
        };
        tip.Segments.Add(new LineSegment { Point = to });
        tip.Segments.Add(new LineSegment
        {
            Point = new Point(basePoint.X + uy * half, basePoint.Y - ux * half)
        });
        geometry.Figures.Add(tip);
        return geometry;
    }

    // ---- selecting, moving and erasing ------------------------------------

    private void OnAnnotationPressed(CanvasItem item, PointerRoutedEventArgs e)
    {
        if (_canvasTool == "erase")
        {
            _canvas.CanvasItems.Remove(item);
            if (_annotationViews.Remove(item.Id, out var view)) Annotations.Children.Remove(view);
            if (_selectedItem?.Id == item.Id) SelectAnnotation(null);
            _autosave.Touch();
            e.Handled = true;
            return;
        }

        if (_canvasTool != "select") return;
        SelectAnnotation(item);

        // While the caret is live the pointer belongs to the editor; a rendered label drags like
        // any other annotation.
        if (_annotationViews[item.Id] is TextBox) return;
        StartAnnotationDrag(item, e);
        e.Handled = true;
    }

    private void StartAnnotationDrag(CanvasItem item, PointerRoutedEventArgs e)
    {
        var view = _annotationViews[item.Id];
        Point last = e.GetCurrentPoint(Viewport).Position;
        bool dragging = view.CapturePointer(e.Pointer);
        if (!dragging) return;

        void Move(object sender, PointerRoutedEventArgs args)
        {
            Point now = args.GetCurrentPoint(Viewport).Position;
            double dx = (now.X - last.X) / _zoom, dy = (now.Y - last.Y) / _zoom;
            last = now;
            foreach (var point in item.Points) { point.X += dx; point.Y += dy; }
            item.X += dx;
            item.Y += dy;
            PositionAnnotation(item, view);
        }

        void Done(object sender, PointerRoutedEventArgs args)
        {
            view.PointerMoved -= Move;
            view.PointerReleased -= Done;
            view.PointerCaptureLost -= Done;
            view.ReleasePointerCapture(args.Pointer);
            _autosave.Touch();
        }

        view.PointerMoved += Move;
        view.PointerReleased += Done;
        view.PointerCaptureLost += Done;
    }

    private void SelectAnnotation(CanvasItem? item)
    {
        if (ReferenceEquals(_selectedItem, item)) return;
        var previous = _selectedItem;
        _selectedItem = item;

        if (previous is not null && _annotationViews.TryGetValue(previous.Id, out var oldView))
            PositionAnnotation(previous, oldView);
        if (item is not null && _annotationViews.TryGetValue(item.Id, out var newView))
            PositionAnnotation(item, newView);

        UpdateCanvasToolContext();
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
