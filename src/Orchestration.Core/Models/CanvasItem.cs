namespace Orchestration.Core.Models;

public enum CanvasItemKind { Stroke, Text }

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
