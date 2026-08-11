using System.Text.Json.Serialization;

namespace Orchestration.Core.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(TerminalNode), "terminal")]
[JsonDerivedType(typeof(NoteNode), "note")]
[JsonDerivedType(typeof(BrowserNode), "browser")]
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

public sealed class BrowserNode : NodeBase
{
    /// <summary>Last address navigated to; written back on every navigation so reload restores it.</summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// What the user typed in the address box, made navigable. Local hosts get http because dev
    /// servers speak http, and the address box is exactly where "localhost:3000" gets typed;
    /// everything else without a scheme gets https. No search-engine fallback: this is a preview
    /// pane, not a browser product.
    /// </summary>
    public static string CompleteUrl(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Contains("://")) return trimmed;
        bool local = trimmed.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.StartsWith("127.");
        return (local ? "http://" : "https://") + trimmed;
    }
}
