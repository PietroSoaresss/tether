using System.Text.Json;
using Orchestration.Core.Models;

namespace Orchestration.Core.Persistence;

public sealed class WorkspaceStore
{
    private readonly TetherPaths _paths;

    public WorkspaceStore(TetherPaths paths) => _paths = paths;

    /// <summary>How the last <see cref="Load"/> went, so the UI can warn about a recovered file.</summary>
    public ReadOutcome LastLoadOutcome { get; private set; } = ReadOutcome.None;

    public Workspace Load()
    {
        LastLoadOutcome = AtomicFile.TryRead<Workspace>(
            _paths.WorkspaceFile,
            json => JsonSerializer.Deserialize<Workspace>(json, TetherJson.Options),
            out var workspace);

        return workspace is null ? new Workspace() : Migrate(workspace);
    }

    public void Save(Workspace workspace)
    {
        workspace.Version = Workspace.CurrentVersion;
        AtomicFile.Write(_paths.WorkspaceFile, JsonSerializer.Serialize(workspace, TetherJson.Options));
    }

    /// <summary>Linear migration: each case repairs its version, then falls through to the next.</summary>
    private static Workspace Migrate(Workspace workspace)
    {
        switch (workspace.Version)
        {
            case <= 0:
                if (workspace.Camera.Zoom <= 0) workspace.Camera.Zoom = 1.0;
                goto case 1;
            case 1:
            default:
                break;
        }

        workspace.Version = Workspace.CurrentVersion;
        return workspace;
    }
}
