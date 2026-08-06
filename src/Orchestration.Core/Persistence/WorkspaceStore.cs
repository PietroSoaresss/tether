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

        // A missing file goes through the same repair as a present one: "no tabs yet" and "a file
        // with no tabs" have to end up at the same place, or the first canvas exists only sometimes.
        return Normalize(Migrate(workspace ?? new Workspace()));
    }

    public void Save(Workspace workspace)
    {
        // Normalized on the way out too, not just in. A workspace assembled in memory with the v1
        // fields set would otherwise be written as a v2 file with an empty tab list — every node
        // silently gone, and no way to tell afterwards.
        Normalize(workspace);
        workspace.Version = Workspace.CurrentVersion;
        AtomicFile.Write(_paths.WorkspaceFile, JsonSerializer.Serialize(workspace, TetherJson.Options));
    }

    /// <summary>Linear migration: each case repairs its version, then falls through to the next.</summary>
    private static Workspace Migrate(Workspace workspace)
    {
        switch (workspace.Version)
        {
            case <= 0:
                // v0's only repair was the zoom, and v1's was folding its single canvas into a tab.
                // Normalize now does both for every version, so the arms are just the seam the next
                // migration hangs off.
                goto case 1;
            case 1:
                goto case 2;
            case 2:
            default:
                break;
        }

        workspace.Version = Workspace.CurrentVersion;
        return workspace;
    }

    /// <summary>
    /// v1 kept a single canvas straight on the workspace. Folding it into the first tab is keyed off
    /// the fields being present rather than off the version number, because an in-memory workspace
    /// carries the current version and the old shape at the same time.
    /// </summary>
    private static void FoldLegacyCanvas(Workspace workspace)
    {
        if (workspace.Nodes is null &&
            workspace.Connections is null &&
            workspace.CanvasItems is null &&
            workspace.Camera is null)
            return;

        workspace.Tabs ??= new List<CanvasTab>();
        workspace.Tabs.Insert(0, new CanvasTab
        {
            Name = "Canvas 1",
            Camera = workspace.Camera ?? new Camera(),
            Nodes = workspace.Nodes ?? new List<NodeBase>(),
            Connections = workspace.Connections ?? new List<Connection>(),
            CanvasItems = workspace.CanvasItems ?? new List<CanvasItem>()
        });
        workspace.ActiveTabId = workspace.Tabs[0].Id;

        // Consumed. Leaving them set would write both shapes on the next save, and folding a file
        // that is already folded would duplicate its canvas on every load.
        workspace.Camera = null;
        workspace.Nodes = null;
        workspace.Connections = null;
        workspace.CanvasItems = null;
    }

    /// <summary>
    /// Repairs the shapes the deserializer cannot guarantee. Every member declared null in the file
    /// comes back null, and each one is dereferenced downstream without asking — which crashes the
    /// app at launch, past the .bak fallback that exists precisely to survive a corrupt file.
    /// </summary>
    private static Workspace Normalize(Workspace workspace)
    {
        workspace.ProjectDirectory ??= "";
        FoldLegacyCanvas(workspace);
        workspace.Tabs ??= new List<CanvasTab>();
        workspace.Tabs.RemoveAll(tab => tab is null);
        // Every caller dereferences the active canvas without asking, so there is always one.
        if (workspace.Tabs.Count == 0) workspace.Tabs.Add(new CanvasTab { Name = "Canvas 1" });

        foreach (var tab in workspace.Tabs) Normalize(tab);

        // An id pointing at a tab that is not there leaves the app with no canvas to show.
        if (workspace.Tabs.All(tab => tab.Id != workspace.ActiveTabId))
            workspace.ActiveTabId = workspace.Tabs[0].Id;

        return workspace;
    }

    private static void Normalize(CanvasTab tab)
    {
        tab.Name ??= "Canvas";
        tab.Camera ??= new Camera();
        tab.Nodes ??= new List<NodeBase>();
        tab.Connections ??= new List<Connection>();
        tab.CanvasItems ??= new List<CanvasItem>();

        tab.Nodes.RemoveAll(node => node is null);
        tab.Connections.RemoveAll(connection => connection is null);
        tab.CanvasItems.RemoveAll(item => item is null || IsInvisible(item));
        foreach (var item in tab.CanvasItems)
        {
            item.Points ??= new List<CanvasPoint>();
            item.Text ??= "";
            item.Color ??= "#F5F3F7";
            item.Font ??= "ui";
            item.Align ??= "left";
        }
        foreach (var terminal in tab.Nodes.OfType<TerminalNode>())
        {
            terminal.AccentColor ??= "";
            terminal.CommandLine ??= AgentKind.PowerShell.CommandLine;
            // Written before Kind existed, or by a newer build that knows a kind this one does not:
            // recover it from the command line rather than mislabelling every old terminal.
            if (string.IsNullOrWhiteSpace(terminal.Kind) ||
                !AgentKind.All.Any(kind => kind.Id == terminal.Kind))
                terminal.Kind = AgentKind.FromCommandLine(terminal.CommandLine).Id;
        }

        // A cable whose other end is not on this canvas can never be drawn or selected, so it would
        // only ever sit in the file. Nothing in the UI can create one; a hand edit can.
        var live = tab.Nodes.Select(node => node.Id).ToHashSet();
        tab.Connections.RemoveAll(c => !live.Contains(c.SourceId) || !live.Contains(c.TargetId));

        // A zero, negative or non-finite zoom is not "too small", it is absent: snapping it to the
        // minimum would drop the user into a canvas they never zoomed out of.
        var camera = tab.Camera;
        camera.Zoom = double.IsFinite(camera.Zoom) && camera.Zoom > 0
            ? Math.Clamp(camera.Zoom, Camera.MinZoom, Camera.MaxZoom)
            : Camera.DefaultZoom;
    }

    /// <summary>
    /// An item that renders as nothing and so can never be selected or erased — it just accumulates
    /// in the file. The editor already drops a blank text when the caret leaves it, but that hook
    /// cannot run if the app is closed or killed mid-edit, and a stroke is written on pointer-down
    /// so a click that never became a drag leaves a one-point polyline behind.
    /// </summary>
    private static bool IsInvisible(CanvasItem item) => item.Kind switch
    {
        CanvasItemKind.Text => string.IsNullOrWhiteSpace(item.Text),
        CanvasItemKind.Stroke => item.Points is null || item.Points.Count < 2,
        // Every other kind spans two corners; one point is half a shape.
        _ => item.Points is null || item.Points.Count < 2
    };
}
