namespace Orchestration.Core.Models;

public sealed class Workspace
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string ProjectDirectory { get; set; } = "";
    public Camera Camera { get; set; } = new();
    public List<NodeBase> Nodes { get; set; } = new();
    public List<Connection> Connections { get; set; } = new();
    public List<CanvasItem> CanvasItems { get; set; } = new();
}
