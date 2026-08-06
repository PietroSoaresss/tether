namespace Orchestration.Core.Models;

/// <summary>
/// One canvas inside a project. Everything that used to hang off the workspace hangs off a tab
/// instead: its own nodes, cables, annotations and camera. A project is a list of these.
/// </summary>
public sealed class CanvasTab
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Canvas";
    public Camera Camera { get; set; } = new();
    public List<NodeBase> Nodes { get; set; } = new();
    public List<Connection> Connections { get; set; } = new();
    public List<CanvasItem> CanvasItems { get; set; } = new();
}
