using Orchestration.Core.Models;
using Orchestration.Core.Persistence;
using Xunit;

namespace Orchestration.Core.Tests;

public class CanvasTabTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tether-tabs-" + Guid.NewGuid().ToString("N"));
    private readonly TetherPaths _paths;

    public CanvasTabTests() => _paths = new TetherPaths(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void TabsRoundTripWithTheirOwnCameraAndContent()
    {
        var store = new WorkspaceStore(_paths);
        var first = new CanvasTab
        {
            Name = "backend",
            Camera = new Camera { OffsetX = 5, Zoom = 0.75 },
            Nodes = { new TerminalNode { Title = "claude", CommandLine = "cmd" } }
        };
        var second = new CanvasTab
        {
            Name = "notas",
            Camera = new Camera { OffsetX = -300, Zoom = 2 },
            CanvasItems = { new CanvasItem { Kind = CanvasItemKind.Text, Text = "plano" } }
        };

        store.Save(new Workspace { Tabs = { first, second }, ActiveTabId = second.Id });
        var loaded = store.Load();

        Assert.Equal(2, loaded.Tabs.Count);
        Assert.Equal(second.Id, loaded.ActiveTabId);
        Assert.Equal("backend", loaded.Tabs[0].Name);
        Assert.Equal(0.75, loaded.Tabs[0].Camera.Zoom);
        Assert.Equal("claude", loaded.Tabs[0].Nodes[0].Title);
        Assert.Equal(2, loaded.Tabs[1].Camera.Zoom);
        Assert.Equal("plano", loaded.Tabs[1].CanvasItems[0].Text);
    }

    /// <summary>The whole point of keeping the v1 fields on the model: nobody loses their canvas.</summary>
    [Fact]
    public void AVersionOneFileBecomesASingleTab()
    {
        WriteWorkspace("""
            {
              "Version": 1,
              "ProjectDirectory": "C:\\dev\\projeto",
              "Camera": { "OffsetX": 12, "OffsetY": 34, "Zoom": 1.5 },
              "Nodes": [ { "$type": "note", "Title": "briefing", "FileName": "b.md" } ],
              "Connections": [],
              "CanvasItems": [ { "Kind": "Text", "Text": "arquitetura" } ]
            }
            """);

        var loaded = new WorkspaceStore(_paths).Load();

        var tab = Assert.Single(loaded.Tabs);
        Assert.Equal(tab.Id, loaded.ActiveTabId);
        Assert.Equal("briefing", tab.Nodes[0].Title);
        Assert.Equal("arquitetura", tab.CanvasItems[0].Text);
        Assert.Equal(1.5, tab.Camera.Zoom);
        Assert.Equal(@"C:\dev\projeto", loaded.ProjectDirectory);
        Assert.Equal(Workspace.CurrentVersion, loaded.Version);
    }

    /// <summary>
    /// Folding has to happen once. Reading the file the migration produced must not wrap it again,
    /// or a canvas multiplies on every launch.
    /// </summary>
    [Fact]
    public void MigratingIsNotRepeatedOnTheNextLoad()
    {
        WriteWorkspace("""
            {
              "Version": 1,
              "Camera": { "Zoom": 1 },
              "Nodes": [ { "$type": "note", "Title": "unica", "FileName": "u.md" } ],
              "Connections": []
            }
            """);

        var store = new WorkspaceStore(_paths);
        var migrated = store.Load();
        store.Save(migrated);

        var again = store.Load();

        Assert.Single(again.Tabs);
        Assert.Equal("unica", Assert.Single(again.Tabs[0].Nodes).Title);
    }

    /// <summary>
    /// The app dereferences the active canvas without asking, so "no tabs" cannot reach it — not
    /// from a fresh install, not from a file someone emptied by hand.
    /// </summary>
    [Theory]
    [InlineData("""{ "Version": 2, "Tabs": [] }""")]
    [InlineData("""{ "Version": 2, "Tabs": null }""")]
    [InlineData("""{ "Version": 2 }""")]
    public void AWorkspaceAlwaysHasAtLeastOneCanvas(string json)
    {
        WriteWorkspace(json);

        var loaded = new WorkspaceStore(_paths).Load();

        var tab = Assert.Single(loaded.Tabs);
        Assert.Equal(tab.Id, loaded.ActiveTabId);
    }

    [Fact]
    public void AnActiveIdPointingNowhereFallsBackToTheFirstTab()
    {
        var store = new WorkspaceStore(_paths);
        store.Save(new Workspace
        {
            Tabs = { new CanvasTab { Name = "a" }, new CanvasTab { Name = "b" } },
            ActiveTabId = Guid.NewGuid()
        });

        var loaded = store.Load();

        Assert.Equal(loaded.Tabs[0].Id, loaded.ActiveTabId);
    }

    /// <summary>
    /// A cable whose other end is on another canvas can never be drawn or selected, so it could
    /// only ever sit in the file. Nothing in the UI makes one; a hand edit does.
    /// </summary>
    [Fact]
    public void CablesWithNoEndpointOnTheirCanvasAreDropped()
    {
        var a = new TerminalNode { Title = "a", CommandLine = "cmd" };
        var b = new TerminalNode { Title = "b", CommandLine = "cmd" };
        var store = new WorkspaceStore(_paths);
        store.Save(new Workspace
        {
            Tabs =
            {
                new CanvasTab
                {
                    Nodes = { a },
                    Connections = { new Connection { SourceId = a.Id, TargetId = b.Id } }
                },
                new CanvasTab { Nodes = { b } }
            }
        });

        var loaded = store.Load();

        Assert.Empty(loaded.Tabs[0].Connections);
        Assert.Equal("a", Assert.Single(loaded.Tabs[0].Nodes).Title);
    }

    private void WriteWorkspace(string json)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(_paths.WorkspaceFile, json);
    }
}
