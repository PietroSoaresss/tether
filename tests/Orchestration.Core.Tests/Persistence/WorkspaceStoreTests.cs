using Orchestration.Core.Models;
using Orchestration.Core.Persistence;
using Xunit;

namespace Orchestration.Core.Tests;

public class WorkspaceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tether-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TetherPaths _paths;

    public WorkspaceStoreTests() => _paths = new TetherPaths(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Paths_SitUnderTheGivenRoot()
    {
        Assert.Equal(Path.Combine(_root, "workspace.json"), _paths.WorkspaceFile);
        Assert.Equal(Path.Combine(_root, "settings.json"), _paths.SettingsFile);
        Assert.Equal(Path.Combine(_root, "notes"), _paths.NotesFolder);
    }

    [Fact]
    public void DefaultPaths_ResolveUnderRoamingAppData()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tether");
        Assert.Equal(expected, new TetherPaths().Root);
    }

    [Fact]
    public void Load_WithNoFile_ReturnsAnEmptyWorkspace()
    {
        var store = new WorkspaceStore(_paths);

        var workspace = store.Load();

        Assert.Equal(ReadOutcome.None, store.LastLoadOutcome);
        Assert.Empty(workspace.Nodes);
        Assert.Empty(workspace.Connections);
        Assert.Equal(1.0, workspace.Camera.Zoom);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsTheGraph()
    {
        var store = new WorkspaceStore(_paths);
        var terminal = new TerminalNode { Title = "claude", CommandLine = "cmd" };
        var note = new NoteNode { Title = "nota", FileName = "nota.md" };
        var original = new Workspace
        {
            Camera = new Camera { OffsetX = 5, OffsetY = 6, Zoom = 0.75 },
            Nodes = { terminal, note },
            Connections = { new Connection { SourceId = terminal.Id, TargetId = note.Id, Bidirectional = true } }
        };

        store.Save(original);
        var loaded = store.Load();

        Assert.Equal(ReadOutcome.Primary, store.LastLoadOutcome);
        Assert.Equal(2, loaded.Nodes.Count);
        Assert.Equal("claude", Assert.IsType<TerminalNode>(loaded.Nodes[0]).Title);
        Assert.Equal("nota.md", Assert.IsType<NoteNode>(loaded.Nodes[1]).FileName);
        Assert.True(loaded.Connections[0].Bidirectional);
        Assert.Equal(0.75, loaded.Camera.Zoom);
    }

    [Fact]
    public void Load_RecoversFromTheBackupWhenThePrimaryIsCorrupt()
    {
        var store = new WorkspaceStore(_paths);
        store.Save(new Workspace { Nodes = { new NoteNode { Title = "sobrevivente", FileName = "a.md" } } });
        store.Save(new Workspace { Nodes = { new NoteNode { Title = "mais novo", FileName = "b.md" } } });

        File.WriteAllText(_paths.WorkspaceFile, "{ truncado");

        var loaded = store.Load();

        Assert.Equal(ReadOutcome.Backup, store.LastLoadOutcome);
        Assert.Equal("sobrevivente", loaded.Nodes[0].Title);
    }

    [Fact]
    public void Load_MigratesAVersionZeroFile()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(_paths.WorkspaceFile,
            """
            {
              "Version": 0,
              "Camera": { "OffsetX": 0, "OffsetY": 0, "Zoom": 0 },
              "Nodes": [],
              "Connections": []
            }
            """);

        var workspace = new WorkspaceStore(_paths).Load();

        // A zero zoom would divide by zero the moment the canvas placed a node.
        Assert.Equal(1.0, workspace.Camera.Zoom);
        Assert.Equal(Workspace.CurrentVersion, workspace.Version);
    }

    [Fact]
    public void Save_StampsTheCurrentVersion()
    {
        var store = new WorkspaceStore(_paths);
        store.Save(new Workspace { Version = 0 });

        Assert.Contains($"\"Version\": {Workspace.CurrentVersion}", File.ReadAllText(_paths.WorkspaceFile));
    }
}
