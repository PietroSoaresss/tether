using System.Text.Json.Serialization;

namespace Orchestration.Core.Models;

public sealed class Workspace
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public string ProjectDirectory { get; set; } = "";

    /// <summary>Never empty after a load: <c>WorkspaceStore</c> guarantees at least one.</summary>
    public List<CanvasTab> Tabs { get; set; } = new();

    public Guid ActiveTabId { get; set; }

    // The v1 shape: one canvas, straight on the workspace. Migrate folds these into the first tab
    // and blanks them, so they are read once and never written again. They stay declared because
    // deleting them would make every workspace written before tabs load empty.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Camera? Camera { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<NodeBase>? Nodes { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Connection>? Connections { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CanvasItem>? CanvasItems { get; set; }
}
