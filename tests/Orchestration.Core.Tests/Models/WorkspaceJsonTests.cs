using System.Text.Json;
using Orchestration.Core.Models;
using Orchestration.Core.Persistence;
using Xunit;

namespace Orchestration.Core.Tests;

public class WorkspaceJsonTests
{
    private static Workspace SampleWorkspace()
    {
        var terminal = new TerminalNode
        {
            Title = "claude",
            X = 10, Y = 20, Width = 720, Height = 420,
            CommandLine = "powershell.exe -NoLogo -NoExit -Command claude",
            WorkingDirectory = @"C:\dev\projeto",
            AutoStart = true
        };
        var note = new NoteNode
        {
            Title = "briefing",
            X = 800, Y = 20, Width = 340, Height = 240,
            FileName = "briefing.md",
            ViewMode = NoteViewMode.Raw
        };

        return new Workspace
        {
            Camera = new Camera { OffsetX = -40, OffsetY = 12, Zoom = 1.25 },
            Nodes = { terminal, note },
            Connections = { new Connection { SourceId = terminal.Id, TargetId = note.Id } }
        };
    }

    [Fact]
    public void Workspace_RoundTripsBothNodeKinds()
    {
        var original = SampleWorkspace();

        string json = JsonSerializer.Serialize(original, TetherJson.Options);
        var loaded = JsonSerializer.Deserialize<Workspace>(json, TetherJson.Options)!;

        var terminal = Assert.IsType<TerminalNode>(loaded.Nodes[0]);
        var note = Assert.IsType<NoteNode>(loaded.Nodes[1]);

        Assert.Equal("powershell.exe -NoLogo -NoExit -Command claude", terminal.CommandLine);
        Assert.Equal(@"C:\dev\projeto", terminal.WorkingDirectory);
        Assert.True(terminal.AutoStart);
        Assert.Equal("briefing.md", note.FileName);
        Assert.Equal(NoteViewMode.Raw, note.ViewMode);
        Assert.Equal(1.25, loaded.Camera.Zoom);
        Assert.Equal(terminal.Id, loaded.Connections[0].SourceId);
        Assert.False(loaded.Connections[0].Bidirectional);
    }

    [Fact]
    public void Workspace_IsWrittenAsDiffableJson()
    {
        string json = JsonSerializer.Serialize(SampleWorkspace(), TetherJson.Options);

        Assert.Contains("\"$type\": \"terminal\"", json);
        Assert.Contains("\"$type\": \"note\"", json);
        // Enums as names, not integers: the file is meant to be readable and hand-editable.
        Assert.Contains("\"Raw\"", json);
        Assert.Contains("\n", json);
    }

    [Fact]
    public void Workspace_DefaultsToTheCurrentVersion()
    {
        Assert.Equal(Workspace.CurrentVersion, new Workspace().Version);
        Assert.Equal(1.0, new Camera().Zoom);
    }

    [Fact]
    public void AppSettings_RoundTripsWithDefaults()
    {
        var defaults = new AppSettings();
        Assert.Equal(AppTheme.System, defaults.Theme);
        Assert.Equal(1500, defaults.IdleMs);
        Assert.Equal(120_000, defaults.AskTimeoutMs);
        Assert.Equal(5, defaults.MaxCallDepth);
        Assert.True(defaults.SeedAgentInstructions);

        string json = JsonSerializer.Serialize(defaults, TetherJson.Options);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json, TetherJson.Options)!;

        Assert.Equal(defaults.TerminalFontFamily, loaded.TerminalFontFamily);
        Assert.Equal(defaults.TerminalFontSize, loaded.TerminalFontSize);
        Assert.Contains("\"System\"", json);
    }
}
