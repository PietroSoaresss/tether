namespace Orchestration.Core.Persistence;

/// <summary>
/// Where the product keeps its data. The root is injectable purely so tests can point at a
/// throwaway directory instead of the real profile.
/// </summary>
public sealed class TetherPaths
{
    public TetherPaths(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tether");
        NotesFolder = Path.Combine(Root, "notes");
    }

    public string Root { get; }
    public string NotesFolder { get; }
    public string WorkspaceFile => Path.Combine(Root, "workspace.json");
    public string SettingsFile => Path.Combine(Root, "settings.json");
}
