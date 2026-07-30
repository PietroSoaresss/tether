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
    /// <summary>
    /// Which <see cref="AgentKind"/> this is. Files written before this field existed come back with
    /// the default and get backfilled from <see cref="CommandLine"/> once, on load.
    /// </summary>
    public string Kind { get; set; } = AgentKind.PowerShell.Id;

    public string CommandLine { get; set; } = "powershell.exe -NoLogo";

    /// <summary>Agents, notes and `tether ask` all key off this, so it is per node rather than global.</summary>
    public string WorkingDirectory { get; set; } = "";

    public bool AutoStart { get; set; }
    public string AccentColor { get; set; } = "";
}

public enum NoteViewMode { Raw, Preview }

public sealed class NoteNode : NodeBase
{
    /// <summary>Project that owns the note. Its Markdown lives in that project's notes folder.</summary>
    public string WorkingDirectory { get; set; } = "";

    /// <summary>File name inside the project notes folder. Markdown never lives in workspace.json.</summary>
    public string FileName { get; set; } = "";

    public NoteViewMode ViewMode { get; set; } = NoteViewMode.Preview;
}
