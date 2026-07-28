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

        return workspace is null ? new Workspace() : Normalize(Migrate(workspace));
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
                // v0's only repair was the zoom, which Normalize now enforces for every version;
                // the arm stays as the seam the next migration hangs off.
                goto case 1;
            case 1:
            default:
                break;
        }

        workspace.Version = Workspace.CurrentVersion;
        return workspace;
    }

    /// <summary>
    /// Repairs the shapes the deserializer cannot guarantee. Every member declared null in the file
    /// comes back null, and each one is dereferenced downstream without asking — which crashes the
    /// app at launch, past the .bak fallback that exists precisely to survive a corrupt file.
    /// </summary>
    private static Workspace Normalize(Workspace workspace)
    {
        workspace.Camera ??= new Camera();
        workspace.Nodes ??= new List<NodeBase>();
        workspace.Connections ??= new List<Connection>();

        workspace.Nodes.RemoveAll(node => node is null);
        workspace.Connections.RemoveAll(connection => connection is null);

        // A zero, negative or non-finite zoom is not "too small", it is absent: snapping it to the
        // minimum would drop the user into a canvas they never zoomed out of.
        var camera = workspace.Camera;
        camera.Zoom = double.IsFinite(camera.Zoom) && camera.Zoom > 0
            ? Math.Clamp(camera.Zoom, Camera.MinZoom, Camera.MaxZoom)
            : Camera.DefaultZoom;

        return workspace;
    }
}
