using System.Text.Json.Serialization;

namespace Orchestration.Core.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(TerminalNode), "terminal")]
[JsonDerivedType(typeof(NoteNode), "note")]
public abstract class NodeBase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string Title { get; set; } = "";
}

public sealed class TerminalNode : NodeBase
{
    public string CommandLine { get; set; } = "powershell.exe -NoLogo";

    /// <summary>Agents, notes and `tether ask` all key off this, so it is per node rather than global.</summary>
    public string WorkingDirectory { get; set; } = "";

    public bool AutoStart { get; set; }
}

public enum NoteViewMode { Raw, Preview }

public sealed class NoteNode : NodeBase
{
    /// <summary>File name inside the notes folder. The markdown itself never lives in workspace.json.</summary>
    public string FileName { get; set; } = "";

    public NoteViewMode ViewMode { get; set; } = NoteViewMode.Preview;
}
