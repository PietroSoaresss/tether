namespace Orchestration.Core.Models;

/// <summary>
/// Serialized by name, so new members are backward compatible and order is free to change.
/// Arrow, Rectangle, Ellipse and Diamond all store their geometry as the first and last
/// <see cref="CanvasItem.Points"/> entry — two corners, or two ends — so none of them needed a
/// new field on the model.
/// </summary>
public enum CanvasItemKind { Stroke, Text, Arrow, Rectangle, Ellipse, Diamond }

public sealed class CanvasPoint
{
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class CanvasItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public CanvasItemKind Kind { get; set; }
    public List<CanvasPoint> Points { get; set; } = new();
    public double X { get; set; }
    public double Y { get; set; }
    public string Text { get; set; } = "";
    public string Color { get; set; } = "#F5F3F7";
    public double Size { get; set; } = 3;
}
