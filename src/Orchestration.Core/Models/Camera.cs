namespace Orchestration.Core.Models;

/// <summary>Where the viewport sits over the infinite canvas.</summary>
public sealed class Camera
{
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double Zoom { get; set; } = 1.0;
}
